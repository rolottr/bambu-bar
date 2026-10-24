import AppKit
import Foundation

/// `--probe <host> <serial> <access-code> [seconds]`: connects like the app does, prints what happens and a
/// summary of the first full report, then exits. For diagnosing a printer from Terminal without the UI.
/// Add `--insecure-tls` for the local test broker. Pass `keychain` as the code to use the one Bambu Bar saved, and
/// `--raw-filament` to print the AMS and external spool fields as the printer sent them.
///
/// `--probe-moonraker <host[:port]> [seconds]` does the same for a Klipper printer with Moonraker (port 7125 unless
/// given): it asks `GET /printer/info` first, then connects like the app does and prints the first status and every
/// tenth one after it. It only reads: no pause, stop, light or speed command is ever sent.
@MainActor
enum Probe {
    static var arguments: (host: String, serial: String, code: String, seconds: Double)? {
        let a = CommandLine.arguments
        guard let i = a.firstIndex(of: "--probe"), i + 3 < a.count else { return nil }
        let seconds = i + 4 < a.count ? Double(a[i + 4]) ?? 20 : 20
        let code = a[i + 3] == "keychain" ? Keychain.accessCode(for: a[i + 2]) ?? "" : a[i + 3]
        return (a[i + 1], a[i + 2], code, seconds)
    }

    static var moonrakerArguments: (host: String, seconds: Double)? {
        let a = CommandLine.arguments
        guard let i = a.firstIndex(of: "--probe-moonraker"), i + 1 < a.count else { return nil }
        let seconds = i + 2 < a.count ? Double(a[i + 2]) ?? 20 : 20
        return (a[i + 1], seconds)
    }

    private static var connection: PrinterConnection?

    static func run(host: String, serial: String, code: String, seconds: Double) {
        let started = Date()
        func log(_ text: String) {
            print(String(format: "[%5.1fs] ", Date().timeIntervalSince(started)) + text)
            fflush(stdout)
        }
        let connection = PrinterConnection()
        self.connection = connection
        var reports = 0
        connection.onPhase = { phase in
            log("phase: \(phase)")
            if phase != .connecting, let verdict = BambuTrust.lastVerdict { log("certificate: \(verdict)") }
        }
        connection.onAck = { command, ok, reason in log("ack: \(command) \(ok ? "ok" : "failed") \(reason ?? "")") }
        connection.onStatus = { status, doc in
            reports += 1
            if reports == 1 || reports % 10 == 0 {
                log("status #\(reports): \(summary(status))")
            }
            if reports == 1, CommandLine.arguments.contains("--raw-filament") {
                var raw: [String: Any] = [:]
                for key in ["ams", "vt_tray", "vir_slot"] { raw[key] = doc.print[key] }
                if let data = try? JSONSerialization.data(withJSONObject: raw, options: [.prettyPrinted, .sortedKeys]) {
                    log("raw filament:\n" + String(decoding: data, as: UTF8.self))
                }
            }
        }
        log("connecting to \(host) as \(serial)")
        connection.start(PrinterConfig(host: host, serial: serial, name: serial, modelCode: ""), accessCode: code,
                         insecure: CommandLine.arguments.contains("--insecure-tls"))
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds) {
            log("done: \(reports) status updates")
            connection.stop()
            exit(reports > 0 ? 0 : 1)
        }
    }

    static func runMoonraker(host: String, seconds: Double) {
        let started = Date()
        func log(_ text: String) {
            print(String(format: "[%5.1fs] ", Date().timeIntervalSince(started)) + text)
            fflush(stdout)
        }
        var config = PrinterConfig(host: host, serial: "", name: host, modelCode: "", kind: .moonraker)
        let endpoint = config.endpoint
        let connection = PrinterConnection()
        self.connection = connection
        var reports = 0
        connection.onPhase = { phase in log("phase: \(phase)") }
        connection.onAck = { command, ok, reason in log("ack: \(command) \(ok ? "ok" : "failed") \(reason ?? "")") }
        connection.onStatus = { status, _ in
            reports += 1
            if reports == 1 || reports % 10 == 0 {
                log("status #\(reports): \(summary(status))")
            }
        }
        log("asking \(endpoint.host):\(endpoint.port) for /printer/info")
        Task { @MainActor in
            switch await MoonrakerClient.hostname(host: endpoint.host, port: endpoint.port) {
            case .success(let name):
                // The model decides what else is read (a Creality K1's own status), as when the printer is added.
                config.modelCode = MoonrakerClient.modelCode(forHostname: name)
                log("Moonraker answered, Klipper host \"\(name)\", model \(config.modelName)")
            case .failure(let error): log("info failed: \(error.message)")
            }
            log("connecting to \(endpoint.host):\(endpoint.port)")
            connection.start(config, accessCode: "", insecure: false)
            DispatchQueue.main.asyncAfter(deadline: .now() + seconds) {
                log("done: \(reports) status updates")
                connection.stop()
                exit(reports > 0 ? 0 : 1)
            }
        }
    }

    private static func summary(_ s: PrinterStatus) -> String {
        var lines = [
            "\(s.state.title), stage \(s.stageID.map(String.init) ?? "-") (\(s.stageText ?? "-"))",
            "job \"\(s.jobName)\" \(s.progress)% layer \(s.layer)/\(s.totalLayers), \(s.remainingMinutes) min left",
            "nozzle \(s.nozzle.current)/\(s.nozzle.target)\(s.secondNozzle.map { " left \($0.current)/\($0.target)" } ?? "")\(s.leftNozzleInUse.map { $0 ? " (left in use)" : " (right in use)" } ?? "") bed \(s.bed.current)/\(s.bed.target) chamber \(s.chamber.map { "\($0)" } ?? "-")",
            "fans part \(s.partFan ?? -1)% aux \(s.auxFan ?? -1)% exhaust \(s.exhaustFan ?? -1)%  speed \(s.speed.title)",
            "light \(s.chamberLightOn.map { $0 ? "on" : "off" } ?? "-") door \(s.doorOpen.map { $0 ? "open" : "closed" } ?? "-") wifi \(s.wifiSignal ?? 0) dBm",
            "controls locked: \(s.controlsLocked)  firmware \(s.firmware ?? "-")  nozzle \(s.nozzleDiameter ?? 0) \(s.nozzleType ?? "-")",
        ]
        for unit in s.ams {
            let spools = unit.spools.map { "\($0.label) \($0.isLoaded ? "\($0.brand ?? "?") \($0.displayName) \($0.colorName ?? "#" + $0.color.prefix(6)) \($0.remainText)" : "empty")" }
            lines.append("\(unit.title) \(unit.humidity.map { "\($0)%RH" } ?? "") \(unit.temperature.map { "\($0)°" } ?? ""): " + spools.joined(separator: ", "))
        }
        if let ext = s.externalSpool { lines.append("external: \(ext.isLoaded ? "\(ext.brand ?? "?") \(ext.displayName) \(ext.colorName ?? "#" + ext.color.prefix(6))" : "empty")") }
        lines.append("active spool: \(s.activeSpoolID.map(String.init) ?? "-")")
        for issue in s.issues { lines.append("issue \(issue.id) [\(issue.severity.title)]: \(issue.message)") }
        return lines.joined(separator: "\n         ")
    }
}
