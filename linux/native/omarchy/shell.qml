import QtQuick
import QtQuick.Dialogs
import QtQuick.Controls
import Quickshell
import Quickshell.Io
import qs.Commons as Commons
import "common" as Common

ShellRoot {
    id: root
    property string colors: ""
    property string shellColors: ""
    property int appearance: 2
    property var palette: null
    function applyTheme() {
        if (!colors) return
        Commons.Color.loadColors(colors)
        Commons.Color.loadShell(shellColors)
        const bg = Commons.Color.background
        const dark = 0.2126 * bg.r + 0.7152 * bg.g + 0.0722 * bg.b < 0.5
        if (palette && appearance !== 2 && (appearance === 0) !== dark) {
            Commons.Color.background = palette.Background
            Commons.Color.foreground = palette.Foreground
            Commons.Color.accent = palette.Accent
            Commons.Color.urgent = palette.Red
            Commons.Color.muted = palette.Secondary
            // Opposite-mode adaptation is local to this app; the desktop shell stays unchanged.
            Commons.Color.shellValues = {"popups.background":palette.Surface,"popups.text":palette.Foreground,"popups.border":palette.Border}
            // Theme state colors may be literal dark-theme hex values. Preserve
            // native sizes/alphas, but adapt their foregrounds with the app mode.
            const states = Object.assign({}, Commons.Style.styleOverrides)
            for (const state of ["normal", "hover-cursor", "selected", "pressed", "focus", "selection"])
                states[state + "-color"] = "foreground"
            Commons.Style.styleOverrides = states
        }
        Commons.Style.refresh()
    }
    FileDialog {
        id: importDialog
        title: "Choose Bambu Studio or OrcaSlicer settings"
        nameFilters: ["Slicer settings (*.conf *.json)", "All files (*)"]
        onAccepted: content.send("import-file", selectedFile.toString())
    }
    FileDialog {
        id: exportDialog
        fileMode: FileDialog.SaveFile
        defaultSuffix: "json"
        nameFilters: ["JSON (*.json)"]
        onAccepted: content.send("export-activity", selectedFile.toString())
    }
    Kit { id: nativeKit }
    // Wayland does not let the application set x/y. Remap after an output's
    // logical geometry changes so our scoped compositor centering rule runs
    // again; otherwise an old position can strand part of the window offscreen.
    Timer {
        id: recenter
        interval: 80
        onTriggered: if (window.visible) { window.visible = false; Qt.callLater(() => window.visible = true) }
    }
    Connections {
        target: window.screen
        function onWidthChanged() { recenter.restart() }
        function onHeightChanged() { recenter.restart() }
    }
    Process {
        id: bridge
        command: ["python3", Quickshell.env("BAMBUBAR_NATIVE_ROOT") + "/bridge.py"]
        running: true
        stdinEnabled: true
        stdout: SplitParser {
            onRead: data => {
                let message
                try { message = JSON.parse(data) } catch (e) { return }
                if (message.type === "theme") {
                    root.colors = message.colors; root.shellColors = message.shell
                    root.applyTheme()
                } else content.accept(message)
            }
        }
    }
    FloatingWindow {
        id: window
        title: content.desktop.simulated ? "Bambu Bar — Virtual printers" : Quickshell.env("BAMBUBAR_DEMO") ? "Bambu Bar — Demo" : "Bambu Bar"
        implicitWidth: Math.min(960, screen ? screen.width - 40 : 960); implicitHeight: Math.min(700, screen ? screen.height - 80 : 700)
        minimumSize: Qt.size(720, 460)
        visible: true
        onScreenChanged: recenter.restart()
        color: nativeKit.background
        onClosed: { visible = false; content.send("visible", false); camera.visible = false }
        Common.Content {
            id: content
            anchors.fill: parent
            kit: nativeKit
            onRequest: message => bridge.write(JSON.stringify(message) + "\n")
            // Remapping also brings an already-open window from another workspace.
            onShowRequested: { window.visible = false; Qt.callLater(() => window.visible = true) }
            onImportRequested: importDialog.open()
            onExportRequested: { exportDialog.title = content.activity?.labels?.export || ""; exportDialog.open() }
            onCameraRequested: { camera.visible = true; content.send("camera", true) }
            onCameraClosed: camera.visible = false
            onQuitRequested: Qt.quit()
            onHideRequested: { window.visible = false; camera.visible = false; content.send("visible", false) }
            onAppearanceRequested: (mode, palette) => {
                if (root.appearance !== mode || JSON.stringify(root.palette) !== JSON.stringify(palette)) {
                    root.appearance = mode; root.palette = palette; root.applyTheme()
                }
            }
        }
        Shortcut { sequence: "Ctrl+W"; onActivated: { window.visible = false; camera.visible = false; content.send("visible", false) } }
    }
    FloatingWindow {
        id: camera
        title: (content.desktop.printer ? content.desktop.printer.name : "Bambu Bar") + " — Camera"
        implicitWidth: Math.min(900, screen ? screen.width - 40 : 900); implicitHeight: Math.min(580, screen ? screen.height - 80 : 580)
        visible: false; color: nativeKit.background
        onVisibleChanged: content.send("camera", visible)
        onClosed: content.send("camera", false)
        Image { anchors.fill: parent; fillMode: Image.PreserveAspectFit; source: content.cameraImage; cache: false }
        Column {
            anchors.centerIn: parent; visible: content.cameraImage === ""; spacing: 12
            Text { text: content.cameraStatus; color: nativeKit.foreground; font.family: nativeKit.fontFamily }
            Common.Field { visible: content.cameraRetry; kit: nativeKit; controller: content; field: ({kind:"button",key:"camera-retry",label:"Try Again"}) }
        }
        Shortcut { sequence: "Escape"; onActivated: { camera.visible = false; content.send("camera", false) } }
    }
}
