import AppKit
import CoreMedia
import Foundation
import ImageIO
import UniformTypeIdentifiers
import VideoToolbox

/// `--probe <host> <serial> <access-code> [seconds]`: connects like the app does, prints what happens and a
/// summary of the first full report, then exits. For diagnosing a printer from Terminal without the UI.
/// Add `--insecure-tls` for the local test broker. Pass `keychain` as the code to use the one Bambu Bar saved, and
/// `--raw-filament` to print the AMS and external spool fields as the printer sent them, `--raw-all` for the whole
/// first full report.
@MainActor
enum Probe {
    static var arguments: (host: String, serial: String, code: String, seconds: Double)? {
        let a = CommandLine.arguments
        guard let i = a.firstIndex(of: "--probe"), i + 3 < a.count else { return nil }
        let seconds = i + 4 < a.count ? Double(a[i + 4]) ?? 20 : 20
        let code = a[i + 3] == "keychain" ? Keychain.accessCode(for: a[i + 2]) ?? "" : a[i + 3]
        return (a[i + 1], a[i + 2], code, seconds)
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
            if reports == 1, CommandLine.arguments.contains("--raw-all"),
               let data = try? JSONSerialization.data(withJSONObject: doc.print, options: [.prettyPrinted, .sortedKeys]) {
                log("raw report:\n" + String(decoding: data, as: UTF8.self))
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

    // MARK: - Status dump

    /// `--dump-status <out-dir> <report.json>...`: runs each saved report through the parser and the model's
    /// capabilities, as the app does, and writes what it shows as `<out-dir>/<name>.json`. The Windows port's tests
    /// compare their parser against these (shared/ModelReports/expected). A report is either a fleet fixture
    /// (`model`, `serial`, `print`) or a captured sample (`get_version`, `pushall`, named `<model>-sample.json`).
    /// Run it in English so the texts match: `-AppleLanguages '(en)'`.
    static var dumpStatusArguments: (output: URL, inputs: [URL])? {
        let a = CommandLine.arguments
        guard let i = a.firstIndex(of: "--dump-status"), i + 2 < a.count else { return nil }
        let rest = a[(i + 2)...].prefix { !$0.hasPrefix("-") }
        return (URL(fileURLWithPath: a[i + 1]), rest.map { URL(fileURLWithPath: $0) })
    }

    static func runDumpStatus(output: URL, inputs: [URL]) {
        var failed = false
        try? FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
        for input in inputs {
            guard let data = try? Data(contentsOf: input),
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                print("can't read \(input.path)"); failed = true; continue
            }
            var doc = ReportDocument()
            let name = input.deletingPathExtension().lastPathComponent
            let serial = json["serial"] as? String ?? ""
            let model: PrinterModel?
            if let print = json["print"] as? [String: Any] {
                doc.apply(["print": print])
                model = PrinterModel.find(code: json["model"] as? String ?? "", serial: serial)
            } else {
                if let version = json["get_version"] as? [String: Any] { doc.apply(version) }
                if let pushall = json["pushall"] as? [String: Any] { doc.apply(pushall) }
                model = PrinterModel.named(name.replacingOccurrences(of: "-sample", with: ""))
            }
            var status = ReportParser.status(from: doc, hms: .shared, serial: serial.isEmpty ? nil : serial)
            status.fit(to: model)
            let out = output.appendingPathComponent("\(name).json")
            guard let text = try? JSONSerialization.data(withJSONObject: golden(status), options: [.prettyPrinted, .sortedKeys]),
                  (try? (text + Data("\n".utf8)).write(to: out)) != nil else {
                print("can't write \(out.path)"); failed = true; continue
            }
            print("\(name): \(model?.name ?? "unknown model") → \(out.lastPathComponent)")
        }
        exit(failed ? 1 : 0)
    }

    /// What the app shows from a status, with every optional present (null when missing), under the Swift names.
    private static func golden(_ s: PrinterStatus) -> [String: Any] {
        func opt(_ value: Any?) -> Any { value ?? NSNull() }
        func temp(_ t: Temperature?) -> Any { t.map { ["current": $0.current, "target": $0.target] } ?? NSNull() }
        func spool(_ s: Spool?) -> Any {
            guard let s else { return NSNull() }
            return ["id": s.id, "unit": s.unit, "slot": s.slot, "material": s.material, "name": s.name, "color": s.color,
                    "colors": s.colors, "remain": opt(s.remain), "weight": opt(s.weight), "isLoaded": s.isLoaded,
                    "isBambu": s.isBambu, "preset": opt(s.preset), "brand": opt(s.brand), "colorName": opt(s.colorName),
                    "nozzleRange": opt(s.nozzleRange.map { [$0.lowerBound, $0.upperBound] }), "label": s.label,
                    "gramsLeft": opt(s.gramsLeft)] as [String: Any]
        }
        let units: [[String: Any]] = s.ams.map { u in
            ["id": u.id, "kind": u.kind.rawValue, "title": u.title, "humidity": opt(u.humidity),
             "humidityLevel": opt(u.humidityLevel), "temperature": opt(u.temperature),
             "dryingMinutesLeft": u.dryingMinutesLeft, "spools": u.spools.map(spool)]
        }
        let issues: [[String: Any]] = s.issues.map { ["id": $0.id, "severity": $0.severity.rawValue, "message": $0.message] }
        return [
            "state": s.state.rawValue, "stageID": opt(s.stageID), "jobName": s.jobName, "progress": s.progress,
            "remainingMinutes": s.remainingMinutes, "layer": s.layer, "totalLayers": s.totalLayers,
            "nozzle": temp(s.nozzle), "bed": temp(s.bed), "chamber": opt(s.chamber), "chamberTarget": opt(s.chamberTarget),
            "secondNozzle": temp(s.secondNozzle), "leftNozzleInUse": opt(s.leftNozzleInUse), "partFan": opt(s.partFan),
            "auxFan": opt(s.auxFan), "exhaustFan": opt(s.exhaustFan), "heatbreakFan": opt(s.heatbreakFan),
            "leftAuxFan": opt(s.leftAuxFan), "circulationFan": opt(s.circulationFan), "speed": s.speed.rawValue,
            "chamberLightOn": opt(s.chamberLightOn), "doorOpen": opt(s.doorOpen), "wifiSignal": opt(s.wifiSignal),
            "ams": units, "externalSpool": spool(s.externalSpool), "activeSpoolID": opt(s.activeSpoolID), "issues": issues,
            "controlsLocked": s.controlsLocked, "firmware": opt(s.firmware), "nozzleDiameter": opt(s.nozzleDiameter),
            "nozzleType": opt(s.nozzleType), "liveViewEnabled": opt(s.liveViewEnabled), "hasSDCard": opt(s.hasSDCard),
            "plateIndex": opt(s.plateIndex), "updateAvailable": s.updateAvailable, "taskID": opt(s.taskID),
            "canceled": s.canceled, "stateTitle": s.stateTitle, "detailText": s.detailText,
            "activeSpoolLabel": opt(s.activeSpool?.label),
        ]
    }

    // MARK: - Camera

    /// `--probe-camera <host> <serial> <access-code|keychain> [seconds] [--jpeg|--rtsp] [--model <name>]`: opens the camera
    /// like the app does, logs the handshake (certificate, RTSP replies, SDP) and the frames, decodes the first one
    /// and saves it as a PNG in the temporary folder. Without `--jpeg`/`--rtsp`, the model decides (a P2S if unknown).
    static var cameraArguments: (host: String, serial: String, code: String, seconds: Double, transport: CameraTransport)? {
        let a = CommandLine.arguments
        guard let i = a.firstIndex(of: "--probe-camera"), i + 3 < a.count else { return nil }
        let seconds = i + 4 < a.count ? Double(a[i + 4]) ?? 15 : 15
        // `--model` takes a name ("P1S") or a model code ("C12"); without it the serial says.
        let name = a.firstIndex(of: "--model").flatMap { $0 + 1 < a.count ? a[$0 + 1] : nil }
        let model = PrinterModel.named(name) ?? PrinterModel.find(code: "", serial: a[i + 2])
        var transport = CameraTransport.forModel(model?.name ?? "P2S")
        if a.contains("--jpeg") { transport = .jpeg }
        if a.contains("--rtsp") { transport = .rtsps }
        let code = a[i + 3] == "keychain" ? Keychain.accessCode(for: a[i + 2]) ?? "" : a[i + 3]
        return (a[i + 1], a[i + 2], code, seconds, transport)
    }

    private static var camera: CameraClient?

    static func runCamera(host: String, serial: String, code: String, seconds: Double, transport: CameraTransport) {
        let started = Date()
        func log(_ text: String) {
            print(String(format: "[%5.1fs] ", Date().timeIntervalSince(started)) + text)
            fflush(stdout)
        }
        let client = makeCameraClient(CameraConfig(host: host, serial: serial, accessCode: code, transport: transport,
                                                   insecure: CommandLine.arguments.contains("--insecure-tls")))
        camera = client
        var frames = 0, keyframes = 0, bytes = 0, saved = false
        client.log = { line in DispatchQueue.main.async { log(line) } }
        client.onEvent = { event in
            switch event {
            case .started(let description, _):
                log("stream: \(description)")
            case .video(let sample, let keyframe):
                frames += 1
                bytes += CMSampleBufferGetTotalSampleSize(sample)
                if keyframe { keyframes += 1 }
                if keyframe, !saved, let image = decode(sample) {
                    saved = true
                    save(image, log: log)
                }
            case .image(let image):
                frames += 1
                if !saved {
                    saved = true
                    save(image, log: log)
                }
            case .failed(let failure):
                log("failed: \(failure.message)")
                exit(1)
            }
        }
        log("opening \(transport == .rtsps ? "RTSPS on port 322" : "the JPEG stream on port 6000") at \(host)")
        client.start()
        Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { _ in
            MainActor.assumeIsolated {
                // JPEG frames arrive decoded, so only the video stream has keyframes and a byte count.
                log(transport == .jpeg ? "\(frames) frames so far" : "\(frames) frames, \(keyframes) keyframes, \(bytes / 1024) KB so far")
            }
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds) {
            let elapsed = Date().timeIntervalSince(started)
            log(String(format: "done: %d frames (%.1f fps)", frames, Double(frames) / elapsed))
            client.stop()
            exit(frames > 0 ? 0 : 1)
        }
    }

    /// Decodes one keyframe with VideoToolbox, which proves the format description and the NAL units are right.
    private static func decode(_ sample: CMSampleBuffer) -> CGImage? {
        guard let format = CMSampleBufferGetFormatDescription(sample) else { return nil }
        var session: VTDecompressionSession?
        guard VTDecompressionSessionCreate(allocator: nil, formatDescription: format, decoderSpecification: nil,
                                           imageBufferAttributes: nil, outputCallback: nil,
                                           decompressionSessionOut: &session) == noErr, let session else { return nil }
        defer { VTDecompressionSessionInvalidate(session) }
        var result: CGImage?
        let status = VTDecompressionSessionDecodeFrame(session, sampleBuffer: sample, flags: [], infoFlagsOut: nil) { status, _, buffer, _, _ in
            guard status == noErr, let buffer else { return }
            VTCreateCGImageFromCVPixelBuffer(buffer, options: nil, imageOut: &result)
        }
        VTDecompressionSessionWaitForAsynchronousFrames(session)
        if status != noErr { print("decode failed: \(status)") }
        return result
    }

    private static func save(_ image: CGImage, log: (String) -> Void) {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("bambubar-camera.png")
        guard let destination = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil) else { return }
        CGImageDestinationAddImage(destination, image, nil)
        if CGImageDestinationFinalize(destination) { log("decoded a \(image.width)×\(image.height) frame: \(url.path)") }
    }

    private static func summary(_ s: PrinterStatus) -> String {
        var lines = [
            "\(s.state.title), stage \(s.stageID.map(String.init) ?? "-") (\(s.stageText ?? "-"))",
            "job \"\(s.jobName)\" \(s.progress)% layer \(s.layer)/\(s.totalLayers), \(s.remainingMinutes) min left",
            "nozzle \(s.nozzle.current)/\(s.nozzle.target)\(s.secondNozzle.map { " left \($0.current)/\($0.target)" } ?? "")\(s.leftNozzleInUse.map { $0 ? " (left in use)" : " (right in use)" } ?? "") bed \(s.bed.current)/\(s.bed.target) chamber \(s.chamber.map { "\($0)" } ?? "-")",
            "fans part \(s.partFan ?? -1)% aux \(s.auxFan ?? -1)% exhaust \(s.exhaustFan ?? -1)%  speed \(s.speed.title)",
            "light \(s.chamberLightOn.map { $0 ? "on" : "off" } ?? "-") door \(s.doorOpen.map { $0 ? "open" : "closed" } ?? "-") wifi \(s.wifiSignal ?? 0) dBm",
            "controls locked: \(s.controlsLocked)  firmware \(s.firmware ?? "-")  nozzle \(s.nozzleDiameter ?? 0) \(s.nozzleType ?? "-")",
            "LAN Only Liveview: \(s.liveViewEnabled.map { $0 ? "on" : "off" } ?? "not reported")",
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
