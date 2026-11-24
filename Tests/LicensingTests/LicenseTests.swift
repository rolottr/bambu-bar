#if !APP_STORE
import Foundation
import Testing
@testable import BambuBarLicensing

private let activationID = "7103adad-ddca-4c23-a523-911fb02fc5f2"
private let fixedNow = Date(timeIntervalSince1970: 1_800_000_000)

private func granted(activation: String? = activationID, benefit: String = PolarConfiguration.benefitID, expiry: Date? = nil) -> PolarLicense {
    PolarLicense(organizationId: PolarConfiguration.organizationID, benefitId: benefit, status: "granted",
                 expiresAt: expiry, activation: activation.map { .init(id: $0) })
}

private func saved(at date: Date? = fixedNow, expiry: Date? = nil) -> LicenseRecord {
    LicenseRecord(key: "test-key", activationID: activationID, installationID: "installation", validatedAt: date, expiresAt: expiry)
}

private final class MemoryLicense: LicensePersistence {
    var record: LicenseRecord?
    var failsWrites = false
    init(_ record: LicenseRecord? = nil) { self.record = record }
    func load() throws -> LicenseRecord? { record }
    func save(_ record: LicenseRecord?) throws {
        if failsWrites { throw LicenseError.storage(-1) }
        self.record = record
    }
}

private final class FakePolar: LicenseAPI, @unchecked Sendable {
    var validation: Result<PolarLicense, Error> = .success(granted())
    var activation: Result<PolarActivation, Error> = .success(.init(id: activationID, licenseKey: granted(activation: nil)))
    var deactivationError: Error?
    var activations = 0
    var deactivations = 0
    var validatedRecord: LicenseRecord?
    var suspendValidation = false
    var pendingValidation: CheckedContinuation<PolarLicense, Error>?
    var validationStarted: (() -> Void)?

    func activate(key: String, installationID: String) async throws -> PolarActivation {
        activations += 1
        return try activation.get()
    }

    func validate(_ record: LicenseRecord) async throws -> PolarLicense {
        validatedRecord = record
        if suspendValidation {
            return try await withCheckedThrowingContinuation {
                pendingValidation = $0
                validationStarted?()
            }
        }
        return try validation.get()
    }

    func deactivate(_ record: LicenseRecord) async throws {
        deactivations += 1
        if let deactivationError { throw deactivationError }
    }
}

private final class PolarHTTPFixture: URLProtocol {
    static var status = 200
    static var headers: [String: String]?
    static var response = Data()
    static var responseURL: URL?
    static var requestCount = 0
    static var lastRequest: URLRequest?
    static var lastBody: [String: Any]?

    static func reset() {
        status = 200
        headers = nil
        response = Data()
        responseURL = nil
        requestCount = 0
        lastRequest = nil
        lastBody = nil
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        Self.requestCount += 1
        Self.lastRequest = request
        var data = request.httpBody ?? Data()
        if let stream = request.httpBodyStream {
            stream.open()
            defer { stream.close() }
            var buffer = [UInt8](repeating: 0, count: 4096)
            while stream.hasBytesAvailable {
                let count = stream.read(&buffer, maxLength: buffer.count)
                if count <= 0 { break }
                data.append(contentsOf: buffer.prefix(count))
            }
        }
        Self.lastBody = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
        let url = Self.responseURL ?? request.url!
        let response = HTTPURLResponse(url: url, statusCode: Self.status, httpVersion: "HTTP/1.1", headerFields: Self.headers)!
        if (300..<400).contains(Self.status), let location = Self.headers?["Location"], let next = URL(string: location) {
            client?.urlProtocol(self, wasRedirectedTo: URLRequest(url: next), redirectResponse: response)
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocolDidFinishLoading(self)
            return
        }
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Self.response)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}

private final class FakeKeychainBackend: Keychain.Backend, @unchecked Sendable {
    private let lock = NSLock()
    var dpItems: [String: Data] = [:]
    var legacyItems: [String: Data] = [:]
    var dpEntitled = true
    var readCalls = 0
    var writeCalls = 0
    var deleteCalls = 0

    private func id(_ service: String, _ account: String) -> String { "\(service)::\(account)" }

    func read(service: String, account: String, dataProtection: Bool) -> (OSStatus, Data?) {
        lock.lock(); defer { lock.unlock() }
        readCalls += 1
        if dataProtection {
            guard dpEntitled else { return (errSecMissingEntitlement, nil) }
            guard let data = dpItems[id(service, account)] else { return (errSecItemNotFound, nil) }
            return (errSecSuccess, data)
        } else {
            guard let data = legacyItems[id(service, account)] else { return (errSecItemNotFound, nil) }
            return (errSecSuccess, data)
        }
    }

    func write(_ data: Data, service: String, account: String, label: String?, dataProtection: Bool) -> OSStatus {
        lock.lock(); defer { lock.unlock() }
        writeCalls += 1
        if dataProtection {
            guard dpEntitled else { return errSecMissingEntitlement }
            dpItems[id(service, account)] = data
            return errSecSuccess
        } else {
            legacyItems[id(service, account)] = data
            return errSecSuccess
        }
    }

    func delete(service: String, account: String, dataProtection: Bool) -> OSStatus {
        lock.lock(); defer { lock.unlock() }
        deleteCalls += 1
        if dataProtection {
            guard dpEntitled else { return errSecMissingEntitlement }
            return dpItems.removeValue(forKey: id(service, account)) != nil ? errSecSuccess : errSecItemNotFound
        } else {
            return legacyItems.removeValue(forKey: id(service, account)) != nil ? errSecSuccess : errSecItemNotFound
        }
    }
}

@MainActor
@Suite(.serialized)
struct LicenseTests {
    @Test(arguments: [false, true])
    func startupWaitsForValidationBeforeShowingAccessOrActivation(rejected: Bool) async {
        let api = FakePolar()
        api.suspendValidation = true
        let store = LicenseStore(api: api, persistence: MemoryLicense(saved()), now: { fixedNow })
        #expect(store.isInitializing)
        #expect(!store.hasAccess)

        let refresh: Task<Void, Never> = await withCheckedContinuation { started in
            let task = Task { await store.refresh() }
            api.validationStarted = { started.resume(returning: task) }
        }
        #expect(store.isInitializing)
        #expect(store.isBusy)
        #expect(!store.hasAccess)
        api.pendingValidation?.resume(with: rejected ? .failure(LicenseError.rejected) : .success(granted()))
        await refresh.value
        #expect(!store.isInitializing)
        #expect(!store.isBusy)
        #expect(store.hasAccess == !rejected)
    }

    @Test func firstLaunchWithoutLicenseFinishesInitialization() async {
        let store = LicenseStore(api: FakePolar(), persistence: MemoryLicense(), now: { fixedNow })
        #expect(store.isInitializing)
        await store.refresh()
        #expect(!store.isInitializing)
        #expect(!store.hasAccess)
        #expect(store.record == nil)
    }

    @Test func supporterChangesNotifyUpdaterWithoutConsumingAnotherActivation() async {
        let api = FakePolar(), storage = MemoryLicense()
        let store = LicenseStore(api: api, persistence: storage, now: { fixedNow })
        var changes: [Bool] = []
        store.onAccessChange = { changes.append($0) }
        await store.refresh()
        #expect(changes == [false])
        await store.activate(key: "test-key")
        #expect(changes == [false, true])
        await store.refresh()
        #expect(changes == [false, true])
        api.validation = .failure(LicenseError.rejected)
        await store.refresh()
        #expect(changes == [false, true, false])
        api.validation = .success(granted())
        await store.refresh()
        #expect(changes == [false, true, false, true])
        await store.deactivate()
        #expect(changes == [false, true, false, true, false])
        #expect(api.activations == 1)
    }

    @Test func activationPersistsAndRelaunchValidatesSameSlot() async throws {
        let api = FakePolar(), storage = MemoryLicense()
        let first = LicenseStore(api: api, persistence: storage, now: { fixedNow })
        await first.activate(key: "  test-key\n")
        #expect(first.hasAccess)
        #expect(storage.record?.key == "test-key")
        let second = LicenseStore(api: api, persistence: storage, now: { fixedNow })
        #expect(!second.hasAccess)
        await second.refresh()
        #expect(second.hasAccess)
        #expect(api.activations == 1)
        #expect(api.validatedRecord?.activationID == activationID)
        #expect(api.validatedRecord?.installationID == storage.record?.installationID)
    }

    @Test func existingActivationCannotConsumeAnotherSlot() async {
        let api = FakePolar(), storage = MemoryLicense(saved())
        let store = LicenseStore(api: api, persistence: storage, now: { fixedNow })
        await store.activate(key: "another-key")
        #expect(api.activations == 0)
        #expect(storage.record?.key == "test-key")
    }

    @Test func revokedActivationRemovesSupporterBenefitsAndNeverAutoReactivates() async {
        let api = FakePolar(), storage = MemoryLicense(saved())
        let store = LicenseStore(api: api, persistence: storage, now: { fixedNow })
        await store.refresh()
        #expect(store.hasAccess)
        api.validation = .failure(LicenseError.rejected)
        await store.refresh()
        #expect(!store.hasAccess)
        #expect(storage.record?.validatedAt == nil)
        api.validation = .failure(LicenseError.unavailable)
        let restarted = LicenseStore(api: api, persistence: storage, now: { fixedNow })
        await restarted.refresh()
        #expect(!restarted.hasAccess)
        #expect(api.activations == 0)
    }

    @Test func outagesAllowOnlyBoundedOfflineUse() async {
        let api = FakePolar(), storage = MemoryLicense(saved())
        api.validation = .failure(LicenseError.unavailable)
        var clock = fixedNow.addingTimeInterval(6 * 86400)
        let store = LicenseStore(api: api, persistence: storage, now: { clock })
        await store.refresh()
        #expect(store.hasAccess)
        clock = fixedNow.addingTimeInterval(7 * 86400)
        await store.refresh()
        #expect(!store.hasAccess)
        #expect(storage.record?.validatedAt == fixedNow)
    }

    @Test func malformed200JSONPreservesOfflineGraceWithoutWipingValidatedAt() async {
        let api = FakePolar(), storage = MemoryLicense(saved())
        api.validation = .failure(LicenseError.invalidResponse)
        let store = LicenseStore(api: api, persistence: storage, now: { fixedNow.addingTimeInterval(3600) })
        await store.refresh()
        #expect(store.hasAccess)
        #expect(storage.record?.validatedAt == fixedNow)
        #expect(store.record?.validatedAt == fixedNow)
    }

    @Test func keychainSaveFailureDuringRefreshPreservesPreviousOfflineGrace() async {
        let api = FakePolar(), storage = MemoryLicense(saved(at: fixedNow))
        var clock = fixedNow.addingTimeInterval(3600)
        let store = LicenseStore(api: api, persistence: storage, now: { clock })
        await store.refresh()
        #expect(store.hasAccess)

        storage.failsWrites = true
        clock = fixedNow.addingTimeInterval(7200)
        await store.refresh()
        #expect(store.hasAccess)
        #expect(store.record?.validatedAt == fixedNow.addingTimeInterval(3600))
        #expect(storage.record?.validatedAt == fixedNow.addingTimeInterval(3600))
        #expect(store.message != nil)
    }

    @Test func localGraceDeadlineExpiresSynchronouslyOnBecomeActiveAndClockRollback() async {
        let api = FakePolar(), storage = MemoryLicense(saved(at: fixedNow))
        var clock = fixedNow
        let center = NotificationCenter()
        let store = LicenseStore(api: api, persistence: storage, now: { clock }, notificationCenter: center)
        await store.refresh()
        #expect(store.hasAccess)

        // Advance past 7-day offline grace without any network refresh.
        clock = fixedNow.addingTimeInterval(LicenseRecord.offlineGrace + 1)
        store.enforceAccessDeadline()
        #expect(!store.hasAccess)

        // Re-grant and test clock rollback before validatedAt.
        clock = fixedNow
        await store.refresh()
        #expect(store.hasAccess)
        clock = fixedNow.addingTimeInterval(-10)
        store.enforceAccessDeadline()
        #expect(!store.hasAccess)
        store.stop()
    }

    @Test func expiryAndClockRollbackCannotExtendOfflineUse() {
        #expect(!saved().permitsOfflineUse(at: fixedNow.addingTimeInterval(-1)))
        var record = saved()
        record.expiresAt = fixedNow.addingTimeInterval(30)
        #expect(!record.permitsOfflineUse(at: fixedNow.addingTimeInterval(31)))
        #expect(record.accessDeadline == fixedNow.addingTimeInterval(30))
        #expect(!saved(at: nil).permitsOfflineUse(at: fixedNow))
    }

    @Test func otherProductAndMissingActivationAreRejected() async {
        for license in [granted(benefit: "other-product"), granted(activation: nil), granted(activation: "another-install"), granted(expiry: fixedNow)] {
            let api = FakePolar(), storage = MemoryLicense(saved())
            api.validation = .success(license)
            let store = LicenseStore(api: api, persistence: storage, now: { fixedNow })
            await store.refresh()
            #expect(!store.hasAccess)
            #expect(storage.record?.validatedAt == nil)
        }
    }

    @Test func activationLimitDoesNotUnlockOrSaveKey() async {
        let api = FakePolar(), storage = MemoryLicense()
        api.activation = .failure(LicenseError.activationLimit)
        let store = LicenseStore(api: api, persistence: storage, now: { fixedNow })
        await store.activate(key: "test-key")
        #expect(!store.hasAccess)
        #expect(storage.record == nil)
        #expect(store.message?.contains("deactivate") == true)
    }

    @Test func failedPersistenceRollsBackNewActivation() async {
        let api = FakePolar(), storage = MemoryLicense()
        storage.failsWrites = true
        let store = LicenseStore(api: api, persistence: storage, now: { fixedNow })
        await store.activate(key: "test-key")
        #expect(!store.hasAccess)
        #expect(api.deactivations == 1)
        #expect(storage.record == nil)
    }

    @Test func failedDeactivationKeepsRecordForRetry() async {
        let api = FakePolar(), storage = MemoryLicense(saved())
        let store = LicenseStore(api: api, persistence: storage, now: { fixedNow })
        await store.refresh()
        api.deactivationError = LicenseError.unavailable
        await store.deactivate()
        #expect(store.hasAccess)
        #expect(storage.record != nil)
        api.deactivationError = nil
        await store.deactivate()
        #expect(!store.hasAccess)
        #expect(storage.record == nil)
    }

    @Test func deactivationRemovesSupporterBenefitsEvenIfKeychainDeleteFails() async {
        let api = FakePolar(), storage = MemoryLicense(saved())
        let store = LicenseStore(api: api, persistence: storage, now: { fixedNow })
        await store.refresh()
        storage.failsWrites = true
        await store.deactivate()
        #expect(!store.hasAccess)
        #expect(store.record?.validatedAt == nil)
    }

    @Test func polarConfigurationIsValidAndRecordRedactsSecrets() {
        #expect(PolarConfiguration.isConfigured)
        #expect(PolarConfiguration.benefitID.lowercased() != PolarConfiguration.productID.lowercased())
        #expect(!PolarConfiguration.validBenefit(PolarConfiguration.productID))
        #expect(!PolarConfiguration.validBenefit("not-a-uuid"))
        #expect(PolarConfiguration.checkoutURL.host == "buy.polar.sh")
        #expect(PolarConfiguration.supportURL(from: .license) == PolarConfiguration.checkoutURL)

        let record = saved()
        #expect(!String(describing: record).contains("test-key"))
        #expect(!String(reflecting: record).contains("test-key"))
        #expect(Mirror(reflecting: record).children.isEmpty)
    }

    @Test func publicClientSendsScopedActivationAndHandlesHTTPFailures() async throws {
        PolarHTTPFixture.reset()
        let session = PolarClient.makeSession(protocolClasses: [PolarHTTPFixture.self])
        defer { session.invalidateAndCancel() }
        let api = PolarClient(session: session)
        let json = """
        {"organization_id":"\(PolarConfiguration.organizationID)","benefit_id":"\(PolarConfiguration.benefitID)",
         "status":"granted","expires_at":null,"activation":{"id":"\(activationID)"}}
        """
        PolarHTTPFixture.status = 200
        PolarHTTPFixture.response = Data(json.utf8)
        let result = try await api.validate(saved())
        try result.verify(activationID: activationID, now: fixedNow)
        #expect(PolarHTTPFixture.lastRequest?.url?.absoluteString == "https://api.polar.sh/v1/customer-portal/license-keys/validate")
        #expect(PolarHTTPFixture.lastRequest?.value(forHTTPHeaderField: "Authorization") == nil)
        #expect(PolarHTTPFixture.lastBody?["activation_id"] as? String == activationID)
        #expect(PolarHTTPFixture.lastBody?["benefit_id"] as? String == PolarConfiguration.benefitID)
        #expect(PolarHTTPFixture.lastBody?["organization_id"] as? String == PolarConfiguration.organizationID)
        #expect((PolarHTTPFixture.lastBody?["conditions"] as? [String: String])?["installation_id"] == "installation")
        #expect(PolarHTTPFixture.lastBody?["increment_usage"] == nil)

        PolarHTTPFixture.status = 404
        PolarHTTPFixture.response = Data(#"{"detail":"not found"}"#.utf8)
        try await api.deactivate(saved())
        do { _ = try await api.validate(saved()); Issue.record("404 JSON must reject validation") }
        catch LicenseError.rejected {} catch { Issue.record("Unexpected error: \(error)") }
        do { _ = try await api.activate(key: "bad", installationID: "installation"); Issue.record("404 JSON on activate must be invalidKey") }
        catch LicenseError.invalidKey {} catch { Issue.record("Unexpected error: \(error)") }

        PolarHTTPFixture.status = 422
        PolarHTTPFixture.response = Data(#"{"detail":"unprocessable"}"#.utf8)
        do { _ = try await api.activate(key: "bad", installationID: "installation"); Issue.record("422 JSON on activate must be invalidKey") }
        catch LicenseError.invalidKey {} catch { Issue.record("Unexpected error: \(error)") }

        PolarHTTPFixture.status = 403
        PolarHTTPFixture.response = Data(#"{"detail":"limit reached"}"#.utf8)
        do { try await api.deactivate(saved()); Issue.record("403 JSON must not clear a local activation") }
        catch LicenseError.rejected {} catch { Issue.record("Unexpected error: \(error)") }
        do { _ = try await api.activate(key: "test-key", installationID: "installation"); Issue.record("403 JSON on activate must be activationLimit") }
        catch LicenseError.activationLimit {} catch { Issue.record("Unexpected error: \(error)") }

        PolarHTTPFixture.status = 503
        PolarHTTPFixture.response = Data()
        do { _ = try await api.validate(saved()); Issue.record("503 must be an outage") }
        catch LicenseError.unavailable {} catch { Issue.record("Unexpected error: \(error)") }
    }

    @Test func captivePortalNonJSONAndMalformed200AreNeverRefusals() async throws {
        PolarHTTPFixture.reset()
        let session = PolarClient.makeSession(protocolClasses: [PolarHTTPFixture.self])
        defer { session.invalidateAndCancel() }
        let api = PolarClient(session: session)

        // Captive portal HTML with 403 -> .unavailable (not .rejected!)
        PolarHTTPFixture.status = 403
        PolarHTTPFixture.response = Data("<html><body>Sign in to Wi-Fi</body></html>".utf8)
        do { _ = try await api.validate(saved()); Issue.record("HTML 403 must be unavailable") }
        catch LicenseError.unavailable {} catch { Issue.record("Unexpected error: \(error)") }

        // Captive portal HTML with 200 -> .unavailable
        PolarHTTPFixture.status = 200
        PolarHTTPFixture.response = Data("<html><body>Portal</body></html>".utf8)
        do { _ = try await api.validate(saved()); Issue.record("HTML 200 must be unavailable") }
        catch LicenseError.unavailable {} catch { Issue.record("Unexpected error: \(error)") }

        // Malformed 200 JSON object -> .invalidResponse (not a refusal!)
        PolarHTTPFixture.status = 200
        PolarHTTPFixture.response = Data(#"{"unexpected":true}"#.utf8)
        do { _ = try await api.validate(saved()); Issue.record("Malformed 200 JSON must be invalidResponse") }
        catch LicenseError.invalidResponse {} catch { Issue.record("Unexpected error: \(error)") }
    }

    @Test func redirectsAreRefusedAndNeverFollowed() async throws {
        PolarHTTPFixture.reset()
        let session = PolarClient.makeSession(protocolClasses: [PolarHTTPFixture.self])
        defer { session.invalidateAndCancel() }
        let api = PolarClient(session: session)

        PolarHTTPFixture.status = 302
        PolarHTTPFixture.headers = ["Location": "https://evil.example/steal"]
        PolarHTTPFixture.response = Data()
        do { _ = try await api.validate(saved()); Issue.record("Redirect must return unavailable") }
        catch LicenseError.unavailable {} catch { Issue.record("Unexpected error: \(error)") }
        #expect(PolarHTTPFixture.requestCount == 1)
    }

    @Test func responseOver128KiBIsRejectedAsInvalidResponse() async throws {
        PolarHTTPFixture.reset()
        let session = PolarClient.makeSession(protocolClasses: [PolarHTTPFixture.self])
        defer { session.invalidateAndCancel() }
        let api = PolarClient(session: session)

        PolarHTTPFixture.status = 200
        PolarHTTPFixture.response = Data(repeating: UInt8(ascii: " "), count: PolarClient.maxResponseBytes + 1)
        do { _ = try await api.validate(saved()); Issue.record("Body > 128 KiB must throw invalidResponse") }
        catch LicenseError.invalidResponse {} catch { Issue.record("Unexpected error: \(error)") }
    }

    @Test func actualWireResponseDecodesAndChecksScope() throws {
        let json = """
        {"organization_id":"\(PolarConfiguration.organizationID)","benefit_id":"\(PolarConfiguration.benefitID)",
         "status":"granted","expires_at":"2030-01-01T12:30:00.123456Z","activation":{"id":"\(activationID)"}}
        """
        let license = try PolarClient.decoder().decode(PolarLicense.self, from: Data(json.utf8))
        try license.verify(activationID: activationID, now: fixedNow)
        #expect(license.expiresAt != nil)
        #expect(throws: (any Error).self) { try license.verify(activationID: "wrong", now: fixedNow) }
    }

    @Test func keychainMigratesLegacyCachesReadsAndSkipsNoopWrites() throws {
        let backend = FakeKeychainBackend()
        backend.legacyItems["\(Keychain.accessCodeService)::SN123"] = Data("12345678".utf8)
        Keychain.resetForTesting(backend: backend, memoryOnly: false)
        defer { Keychain.resetForTesting() }

        // First read migrates legacy item to data-protection keychain and removes legacy copy.
        #expect(Keychain.accessCode(for: "SN123") == "12345678")
        #expect(backend.dpItems["\(Keychain.accessCodeService)::SN123"] == Data("12345678".utf8))
        #expect(backend.legacyItems["\(Keychain.accessCodeService)::SN123"] == nil)

        // Second read hits in-memory cache with zero additional backend reads.
        let readsAfterFirst = backend.readCalls
        #expect(Keychain.accessCode(for: "SN123") == "12345678")
        #expect(backend.readCalls == readsAfterFirst)

        // Writing the identical code performs zero backend writes.
        let writesBefore = backend.writeCalls
        Keychain.setAccessCode("12345678", for: "SN123")
        #expect(backend.writeCalls == writesBefore)

        // Writing a new code updates data-protection keychain.
        Keychain.setAccessCode("87654321", for: "SN123")
        #expect(backend.writeCalls == writesBefore + 1)
        #expect(backend.dpItems["\(Keychain.accessCodeService)::SN123"] == Data("87654321".utf8))
    }

    @Test func keychainFallsBackWhenUnentitledAndDeveloperModeNeverTouchesBackend() throws {
        let backend = FakeKeychainBackend()
        backend.dpEntitled = false
        backend.legacyItems["\(LicenseKeychain.service)::\(LicenseKeychain.account)"] = try JSONEncoder().encode(saved())
        Keychain.resetForTesting(backend: backend, memoryOnly: false)
        defer { Keychain.resetForTesting() }

        let loaded = try LicenseKeychain().load()
        #expect(loaded?.key == "test-key")
        // Unentitled build must NOT delete the legacy item.
        #expect(backend.legacyItems["\(LicenseKeychain.service)::\(LicenseKeychain.account)"] != nil)

        // Memory-only developer mode never touches the backend at all.
        let devBackend = FakeKeychainBackend()
        Keychain.resetForTesting(backend: devBackend, memoryOnly: true)
        Keychain.setAccessCode("99999999", for: "DEV1")
        #expect(Keychain.accessCode(for: "DEV1") == "99999999")
        Keychain.removeAccessCode(for: "DEV1")
        #expect(Keychain.accessCode(for: "DEV1") == nil)
        #expect(devBackend.readCalls == 0)
        #expect(devBackend.writeCalls == 0)
        #expect(devBackend.deleteCalls == 0)
    }
}
#endif
