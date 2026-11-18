import QtQuick
import QtQuick.Dialogs
import QtQuick.Controls
import QtQuick.Layouts
import QtQuick.Window
import org.kde.kirigami as Kirigami
import "../common" as Common

Kirigami.ApplicationWindow {
    id: window
    title: content.desktop.simulated ? "Bambu Bar — Virtual printers" : backend.demo ? "Bambu Bar — Demo" : "Bambu Bar"
    width: 1040; height: 760
    minimumWidth: 720; minimumHeight: 460
    visible: true
    onClosing: function(event) { event.accepted = false; visible = false; content.send("visible", false); camera.hide() }
    FileDialog {
        id: importDialog
        title: "Choose Bambu Studio or OrcaSlicer settings"
        nameFilters: ["Slicer settings (*.conf *.json)", "All files (*)"]
        onAccepted: content.send("import-file", selectedFile.toString())
    }
    Kit { id: nativeKit }
    Common.Content {
        id: content
        anchors.fill: parent
        kit: nativeKit
        onRequest: message => backend.send(JSON.stringify(message))
        onShowRequested: { window.show(); window.raise(); window.requestActivate() }
        onImportRequested: importDialog.open()
        onCameraRequested: { camera.show(); camera.raise(); content.send("camera", true) }
        onCameraClosed: camera.close()
        onQuitRequested: Qt.quit()
        onHideRequested: window.close()
        onAppearanceRequested: (mode, palette) => backend.setAppearance(mode)
    }
    Connections { target: backend; function onMessage(message) { content.accept(message) } }
    ApplicationWindow {
        id: camera
        title: (content.desktop.printer ? content.desktop.printer.name : "Bambu Bar") + " — Camera"
        width: 900; height: 580; visible: false
        onVisibilityChanged: content.send("camera", visible && visibility !== Window.Minimized)
        onClosing: function(event) { event.accepted = false; hide(); content.send("camera", false) }
        Image { anchors.fill: parent; fillMode: Image.PreserveAspectFit; source: content.cameraImage; cache: false }
        Column {
            anchors.centerIn: parent; visible: content.cameraImage === ""; spacing: 12
            Label { text: content.cameraStatus }
            Common.Field { visible: content.cameraRetry; kit: nativeKit; controller: content; field: ({kind:"button",key:"camera-retry",label:"Try Again"}) }
        }
        Shortcut { sequence: "Escape"; onActivated: camera.close() }
    }
    Shortcut { sequence: "Ctrl+W"; onActivated: window.close() }
    Component.onCompleted: content.page = backend.initialPage
}
