#if !APP_STORE
import Foundation
import Testing
@testable import BambuBarLicensing
@testable import BambuBarUpdates

struct UpdateDownloadTests {
    @Test func acceptsProductionEndpointWithOrWithoutQuery() {
        #expect(UpdateDownload.accepts(URL(string: "https://bambubar.app/api/updates/download")!))
        #expect(UpdateDownload.accepts(URL(string: "https://bambubar.app/api/updates/download?version=3.0.1")!))
        #expect(UpdateDownload.accepts(URL(string: "https://BAMBUBAR.APP:443/api/updates/download?version=3.0.1")!))
    }

    @Test func rejectsTrailingSlashAndAlternatePaths() {
        #expect(!UpdateDownload.accepts(URL(string: "https://bambubar.app/api/updates/download/")!))
        #expect(!UpdateDownload.accepts(URL(string: "https://bambubar.app/api/updates/download/?version=3.0.1")!))
        #expect(!UpdateDownload.accepts(URL(string: "https://bambubar.app/api/updates/download/extra")!))
        #expect(!UpdateDownload.accepts(URL(string: "https://bambubar.app/api/updates/download%2F")!))
        #expect(!UpdateDownload.accepts(URL(string: "https://bambubar.app/appcast.xml")!))
    }

    @Test func rejectsUserinfoWrongSchemeHostAndPort() {
        #expect(!UpdateDownload.accepts(URL(string: "https://user@bambubar.app/api/updates/download")!))
        #expect(!UpdateDownload.accepts(URL(string: "https://user:pass@bambubar.app/api/updates/download")!))
        #expect(!UpdateDownload.accepts(URL(string: "http://bambubar.app/api/updates/download")!))
        #expect(!UpdateDownload.accepts(URL(string: "https://bambubar.app:8443/api/updates/download")!))
        #expect(!UpdateDownload.accepts(URL(string: "https://evil.example/api/updates/download")!))
        #expect(!UpdateDownload.accepts(URL(string: "https://bambubar.app.evil.example/api/updates/download")!))
        #expect(!UpdateDownload.accepts(URL(string: "file:///api/updates/download")!))
    }

    @Test func derivesEndpointOriginFromFeedOverride() {
        let localFeed = URL(string: "http://localhost:3201/appcast.xml")!
        #expect(UpdateDownload.accepts(URL(string: "http://localhost:3201/api/updates/download?version=3.0.1")!, feed: localFeed))
        #expect(UpdateDownload.accepts(URL(string: "https://bambubar.app/api/updates/download?version=3.0.1")!, feed: localFeed))
        #expect(!UpdateDownload.accepts(URL(string: "http://localhost:3202/api/updates/download?version=3.0.1")!, feed: localFeed))
        #expect(!UpdateDownload.accepts(URL(string: "https://localhost:3201/api/updates/download?version=3.0.1")!, feed: localFeed))
        #expect(!UpdateDownload.accepts(URL(string: "http://localhost:3201/api/updates/download/")!, feed: localFeed))
        #expect(!UpdateDownload.accepts(URL(string: "http://user@localhost:3201/api/updates/download")!, feed: localFeed))
    }

    @Test func headersCarryExactBambuBarFieldsWhenRecordComplete() {
        let record = LicenseRecord(key: "BB-KEY-1234", activationID: "act-1", installationID: "inst-1")
        let headers = UpdateDownload.headers(for: record)
        #expect(headers == [
            "X-BambuBar-License-Key": "BB-KEY-1234",
            "X-BambuBar-Activation-Id": "act-1",
            "X-BambuBar-Installation-Id": "inst-1",
        ])
        #expect(UpdateDownload.headers(for: nil) == nil)
        #expect(UpdateDownload.headers(for: LicenseRecord(key: "", activationID: "act-1", installationID: "inst-1")) == nil)
        #expect(UpdateDownload.headers(for: LicenseRecord(key: "k", activationID: "", installationID: "inst-1")) == nil)
        #expect(UpdateDownload.headers(for: LicenseRecord(key: "k", activationID: "a", installationID: "")) == nil)
    }
}
#endif
