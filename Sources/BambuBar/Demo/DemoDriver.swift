import Foundation

/// `--demo [scenario] [--fleet]`: a simulated P2S (plus five more printers with `--fleet`), for screenshots and
/// for trying the app without a printer. Nothing is saved and no network is touched.
@MainActor
final class DemoDriver {
    enum Scenario: String, CaseIterable {
        case printing, preparing, paused, finished, failed, idle, offline, unconfigured
    }

    static var requestedScenario: Scenario? {
        let args = CommandLine.arguments
        guard let i = args.firstIndex(of: "--demo") else { return nil }
        if i + 1 < args.count, let s = Scenario(rawValue: args[i + 1]) { return s }
        return .printing
    }

    var onStatus: ((PrinterStatus, Bool) -> Void)?
    var onPhase: ((PrinterConnection.Phase) -> Void)?

    private let scenario: Scenario
    private var status = PrinterStatus.sample
    private var timer: Timer?
    private var tick = 0

    /// `variant` gives each simulated printer its own job and spools.
    init(scenario: Scenario, variant: Int = 0) {
        self.scenario = scenario
        switch variant {
        case 1:
            status.jobName = "Gridfinity Baseplate 4x4"
            status.ams[0].spools[0].recolor("DE4343", name: "Scarlet Red", product: "PLA Matte", preset: "GFA01")
            status.ams[0].kind = .ams
            status.activeSpoolID = 0
            status.externalSpool = nil
        case 2:
            status.jobName = "Articulated Dragon"
            status.ams[0].kind = .lite
            status.ams[0].spools[1].recolor("8671CB", name: "Purple", product: "PLA Silk+", preset: "GFA06")
            status.activeSpoolID = 1
            status.progress = 31
            status.layer = 88
            status.totalLayers = 284
            status.remainingMinutes = 147
            status.chamber = nil
            status.externalSpool = nil
        case 3:
            status.jobName = "Two-tone Vase"
            status.ams[0].spools[0].recolor("FFFFFF", name: "Jade White", product: "PLA Basic", preset: "GFA00")
            status.ams[0].spools[1].recolor("00AE42", name: "Bambu Green", product: "PLA Basic", preset: "GFA00")
            status.activeSpoolID = 1
            status.progress = 44
            status.layer = 132
            status.totalLayers = 300
            status.remainingMinutes = 188
            status.secondNozzle = Temperature(current: 170, target: 170)
            status.leftNozzleInUse = false
            status.chamber = 41
        case 4:
            status.jobName = "Keychain Batch"
            status.ams[0].kind = .lite
            status.ams[0].spools[3].recolor("E8AFCF", name: "Sakura Pink", product: "PLA Matte", preset: "GFA01")
            status.activeSpoolID = 3
            status.chamber = nil
            status.externalSpool = nil
        case 5:
            status.jobName = "Cable Clips x24"
            status.ams[0].kind = .ams
            status.ams[0].spools[2].recolor("0056B8", name: "Cobalt Blue", product: "PLA Basic", preset: "GFA00")
            status.activeSpoolID = 2
            status.progress = 83
            status.layer = 166
            status.totalLayers = 200
            status.remainingMinutes = 18
            status.externalSpool = nil
        default:
            break
        }
    }

    func start() {
        switch scenario {
        case .unconfigured:
            onPhase?(.idle)
            return
        case .offline:
            onPhase?(.offline("The printer isn't reachable on the network."))
            return
        default:
            break
        }
        onPhase?(.online)
        shape()
        emit()
        // README frames script every status themselves.
        guard scenario == .printing || scenario == .preparing, !ReadmeFrames.isActive else { return }
        timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.advance() }
        }
    }

    private func shape() {
        switch scenario {
        case .preparing:
            status.state = .preparing
            status.stageID = 7
            status.progress = 0
            status.layer = 0
            status.remainingMinutes = 168
            status.nozzle = Temperature(current: 164, target: 220)
        case .paused:
            status.state = .paused
            status.stageID = 6
            status.issues = [PrinterIssue(id: "HMS_0700_2300_0002_0001", severity: .serious,
                                          message: HMSCatalog.shared.hmsText("0700230000020001") ?? "AMS A slot 4 filament has run out. Please insert a new filament.",
                                          wikiURL: HMSCatalog.wikiURL(code: "0700230000020001", serial: "01P"))]
        case .finished:
            status.state = .finished
            status.stageID = 255
            status.progress = 100
            status.remainingMinutes = 0
            status.layer = 305
            status.nozzle = Temperature(current: 46, target: 0)
            status.bed = Temperature(current: 41, target: 0)
            status.activeSpoolID = nil
        case .failed:
            status.state = .failed
            status.stageID = 255
            status.nozzle = Temperature(current: 180, target: 0)
            status.issues = [PrinterIssue(id: "0300_4006", severity: .fatal,
                                          message: HMSCatalog.shared.errorText("03004006") ?? "The nozzle is clogged with filament. The print has been stopped.",
                                          wikiURL: HMSCatalog.wikiURL(code: "03004006", serial: "01P"))]
        case .idle:
            status.state = .idle
            status.stageID = 255
            status.jobName = ""
            status.progress = 0
            status.remainingMinutes = 0
            status.layer = 0
            status.totalLayers = 0
            status.nozzle = Temperature(current: 27, target: 0)
            status.bed = Temperature(current: 25, target: 0)
            status.chamber = 24
            status.partFan = 0
            status.auxFan = 0
            status.activeSpoolID = nil
            status.chamberLightOn = false
        default:
            break
        }
    }

    private func advance() {
        tick += 1
        if status.state == .preparing {
            status.nozzle.current = min(status.nozzle.target, status.nozzle.current + 9)
            if status.nozzle.isAtTarget { status.state = .printing; status.stageID = 0 }
        } else if status.state == .printing, tick % 2 == 0, status.progress < 99 {
            status.progress += 1
            status.layer = min(status.totalLayers, Int(Double(status.totalLayers) * Double(status.progress) / 100))
            status.remainingMinutes = max(1, status.remainingMinutes - 2)
            status.updatedAt = Date()
        }
        status.nozzle.current = status.nozzle.target > 0 ? status.nozzle.target + Double([-1, 0, 0, 1].randomElement()!) : status.nozzle.current
        emit()
    }

    private func emit() {
        if status.state == .printing || status.state == .preparing { status.updatedAt = Date() }
        onStatus?(status, false)
    }

    func pause() { status.state = .paused; status.stageID = 16; emit() }
    func resume() { status.state = .printing; status.stageID = 0; emit() }
    func stop() { status.state = .failed; status.stageID = 255; status.issues = []; onStatus?(status, true) }
    func setLight(_ on: Bool) { status.chamberLightOn = on; emit() }
    func setSpeed(_ level: SpeedLevel) { status.speed = level; emit() }

    static func sampleHistory(for serials: [String]) -> [JobRecord] {
        let now = Date()
        func job(_ name: String, hoursAgo: Double, minutes: Double, _ outcome: JobRecord.Outcome, _ color: String, _ material: String, layers: Int) -> JobRecord {
            let end = now.addingTimeInterval(-hoursAgo * 3600)
            return JobRecord(name: name, startedAt: end.addingTimeInterval(-minutes * 60), endedAt: end, outcome: outcome,
                             totalLayers: layers, filamentColor: color, filamentMaterial: material)
        }
        let jobs = [
            job("Cable Clip x12", hoursAgo: 3.2, minutes: 48, .finished, "1C1C1EFF", "PLA", layers: 64),
            job("Benchy", hoursAgo: 20, minutes: 41, .finished, "E2763AFF", "PLA", layers: 240),
            job("Headphone Stand", hoursAgo: 27, minutes: 312, .finished, "F4EEE6FF", "PLA", layers: 812),
            job("Gridfinity Bin 2x3", hoursAgo: 49, minutes: 12, .canceled, "2E7D5BFF", "PETG", layers: 140),
            job("Planter Spiral", hoursAgo: 75, minutes: 95, .failed, "2E7D5BFF", "PETG", layers: 410),
            job("Desk Hook", hoursAgo: 98, minutes: 36, .finished, "1C1C1EFF", "PLA", layers: 120),
        ]
        // Spread over the printers, oldest first per printer like real history.
        return jobs.enumerated().map { index, job in
            var job = job
            job.printerSerial = serials.isEmpty ? nil : serials[index % serials.count]
            return job
        }
    }
}

private extension Spool {
    /// A Bambu Lab spool of another colour, as its tag would report it.
    mutating func recolor(_ hex: String, name colorName: String, product: String, preset: String) {
        color = hex + "FF"
        colors = [color]
        self.colorName = colorName
        name = product
        material = "PLA"
        self.preset = preset
        brand = "Bambu Lab"
        isBambu = true
        weight = 1000
        if remain == nil { remain = 72 }
    }
}
