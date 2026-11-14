import QtQuick
import QtQuick.Controls as Controls
import QtQuick.Layouts
import QtQuick.Effects
import Quickshell
import Quickshell.Io
import Quickshell.Wayland
import qs.Ui as Ui
import qs.Commons as Commons
import "common" as Common

Ui.BarWidget {
    id: root
    moduleName: "dev.rolottr.BambuBar"
    property var snapshot: ({})
    property var widgetIds: []
    property bool opened: false
    property bool contextOnly: false
    property string confirmation: ""
    property string confirmationLabel: ""
    property var drafts: ({})
    implicitWidth: button.implicitWidth
    implicitHeight: button.implicitHeight
    function t(key) { return (snapshot.labels || {})[key] || key }
    function open() { opened = true }
    function close() { opened = false }
    function toggle() { opened = !opened }
    function closeForPopoutSwitch() { close() }
    function send(action, value) {
        bridge.write(JSON.stringify({action: action, value: value, printer: snapshot.printer ? snapshot.printer.id : ""}) + "\n")
    }
    function activate(field, value) {
        if (field.key === "panel") { contextOnly = false; opened = true; return }
        if (field.note === "confirmation") { confirmation = field.key; confirmationLabel = field.label; return }
        if (field.key === "speed") { send("speed:" + value); return }
        send(field.key, value)
        if (["show", "settings", "camera", "quit"].indexOf(field.key) !== -1) close()
    }
    onOpenedChanged: send("open", opened)
    Kit { id: nativeKit }
    Process {
        id: bridge
        command: ["python3", Quickshell.env("HOME") + "/.config/omarchy/plugins/dev.rolottr.BambuBar/bridge.py", "--desktop"]
        running: true; stdinEnabled: true
        stdout: SplitParser {
            onRead: data => {
                try {
                    root.snapshot = JSON.parse(data).desktop || {}
                    const ids = (root.snapshot.widgets || []).map(w => w.id)
                    if (JSON.stringify(ids) !== JSON.stringify(root.widgetIds)) root.widgetIds = ids
                } catch (e) {}
            }
        }
    }
    Ui.BarIconButton {
        id: button
        anchors.fill: parent
        bar: root.bar
        labelText: root.snapshot.panelText || ""
        tooltipText: root.snapshot.title || "Bambu Bar"
        iconComponent: Component {
            Image {
                width: 24; height: 24
                source: Qt.resolvedUrl("tray-icons/dev.rolottr.BambuBar-tray-" + (root.snapshot.panelIcon || "offline") + "-symbolic.svg")
                sourceSize: Qt.size(24, 24)
                layer.enabled: true
                layer.effect: MultiEffect { brightness: 1; colorization: 1; colorizationColor: button.foreground }
            }
        }
        onPressed: function(mouseButton) {
            if (mouseButton === Qt.MiddleButton) root.send("show")
            else { root.contextOnly = mouseButton === Qt.RightButton; root.toggle() }
        }
    }
    Ui.PopupCard {
        id: popup
        anchorItem: button
        bar: root.bar
        owner: root
        open: root.opened
        contentWidth: fittedContentWidth(420, 420)
        contentHeight: fittedContentHeight(body.implicitHeight, 850)
        Controls.ScrollView {
            font: nativeKit.nativeFont
            anchors.fill: parent
            clip: true
            contentWidth: availableWidth
            ColumnLayout {
                id: body
                width: parent.width
                spacing: 12
                Text { Layout.fillWidth: true; text: root.snapshot.title || "Bambu Bar"; font.family: nativeKit.fontFamily; font.bold: true; color: nativeKit.foreground; wrapMode: Text.Wrap; textFormat: Text.PlainText }
                Common.Field {
                    Layout.fillWidth: true; visible: (root.snapshot.printers || []).length > 1
                    field: ({kind: "choice", key: "select", label: "Printer", value: root.snapshot.printer ? root.snapshot.printer.id : "", options: (root.snapshot.printers || []).map(p => ({value:p.id, label:p.name}))})
                    kit: nativeKit
                    controller: ({activate: function(field, value) { bridge.write(JSON.stringify({action:"select", printer:value}) + "\n") }})
                }
                Common.PrinterCard { visible: !root.contextOnly; Layout.fillWidth: true; kit: nativeKit; controller: root; printer: root.snapshot.printer || {}; compact: true }
                Flow {
                    Layout.fillWidth: true; spacing: 8
                    Repeater {
                        model: (root.contextOnly ? [{key:"panel",label:"Quick view"}] : []).concat([{key:"show",label:"Open Bambu Bar"},{key:"settings",label:"Settings"},{key:"quit",label:"Quit"}])
                        Common.Field { required property var modelData; field: Object.assign({kind:"button"},modelData,{label:root.t(modelData.label)}); kit: nativeKit; controller: root }
                    }
                }
            }
        }
        Ui.ConfirmDialog {
            anchors.fill: parent
            id: confirmationDialog
            opened: root.confirmation !== ""
            selectedIndex: 0
            focus: opened
            Keys.onPressed: event => { event.accepted = handleKey(event) }
            message: root.confirmationLabel + "?"
            onConfirmed: { root.send(root.confirmation === "stop" ? "stop-confirmed" : root.confirmation, true); root.confirmation = "" }
            onCanceled: root.confirmation = ""
        }

    }
    Shortcut { enabled: root.opened && root.confirmation === ""; sequence: "Escape"; onActivated: root.close() }
    function placedWidgets() {
        if (!button.QsWindow.window || button.QsWindow.window.screen !== Quickshell.screens[0]) return []
        const positions = {}
        return (snapshot.widgets || []).map(w => {
            const height = w.size === "small" ? 190 : w.size === "large" ? 380 : 260
            let p = positions[w.corner] || {x:0,y:0}
            if (p.y + height > Quickshell.screens[0].height - 100) p = {x:p.x + 400,y:0}
            const placed = Object.assign({}, w, {offsetX:p.x, offsetY:p.y, cardHeight:height})
            positions[w.corner] = {x:p.x,y:p.y + height + 16}
            return placed
        })
    }
    Variants {
        // Desktop cards belong to one output, avoiding duplicate cards on multi-monitor bars.
        model: button.QsWindow.window && button.QsWindow.window.screen === Quickshell.screens[0] ? root.widgetIds : []
        delegate: PanelWindow {
            required property string modelData
            readonly property var widgetData: root.placedWidgets().find(w => w.id === modelData) || {size:"small",corner:"top-right",offsetX:0,offsetY:0,cardHeight:190,state:{}}
            screen: Quickshell.screens[0]
            color: "transparent"
            implicitWidth: widgetData.size === "small" ? 190 : widgetData.size === "large" ? 380 : 320
            implicitHeight: widgetData.cardHeight
            anchors.top: !widgetData.corner.startsWith("bottom")
            anchors.bottom: widgetData.corner.startsWith("bottom")
            anchors.left: widgetData.corner.endsWith("left")
            anchors.right: !widgetData.corner.endsWith("left")
            margins.top: 55 + widgetData.offsetY; margins.bottom: 24 + widgetData.offsetY; margins.left: 24 + widgetData.offsetX; margins.right: 24 + widgetData.offsetX
            exclusiveZone: 0
            WlrLayershell.layer: WlrLayer.Bottom
            WlrLayershell.keyboardFocus: WlrKeyboardFocus.None
            Rectangle {
                anchors.fill: parent; color: nativeKit.card; border.color: nativeKit.border; radius: nativeKit.radius
                ColumnLayout {
                    anchors.fill: parent; anchors.margins: 14; spacing: 8
                    Text { font.family: nativeKit.fontFamily; Layout.fillWidth: true; text: widgetData.state.name || "Bambu Bar"; color: nativeKit.foreground; font.bold: true; elide: Text.ElideRight; textFormat: Text.PlainText }
                    Text { font.family: nativeKit.fontFamily; Layout.fillWidth: true; text: widgetData.state.caption || ""; color: widgetData.state.state === "failed" ? nativeKit.negative : nativeKit.accent; elide: Text.ElideRight; textFormat: Text.PlainText }
                    RowLayout {
                        Layout.fillWidth: true
                        Image { visible: widgetData.size !== "small"; Layout.preferredWidth: 95; Layout.preferredHeight: 100; source: widgetData.state.art ? "file://" + widgetData.state.art : ""; fillMode: Image.PreserveAspectFit }
                        ColumnLayout {
                            Layout.fillWidth: true
                            Text { font.family: nativeKit.fontFamily; text: String(widgetData.state.progress || 0) + "%"; color: nativeKit.foreground; font.pixelSize: 30 }
                            Text { font.family: nativeKit.fontFamily; Layout.fillWidth: true; text: widgetData.state.stateLine || ""; color: nativeKit.muted; wrapMode: Text.Wrap; maximumLineCount: 2; elide: Text.ElideRight; textFormat: Text.PlainText }
                        }
                    }
                    Common.Progress { kit: nativeKit; Layout.fillWidth: true; value: widgetData.state.progress || 0 }
                    Text { font.family: nativeKit.fontFamily; visible: widgetData.size !== "small"; Layout.fillWidth: true; text: widgetData.state.headline || ""; color: nativeKit.foreground; elide: Text.ElideRight; textFormat: Text.PlainText }
                    Text { font.family: nativeKit.fontFamily; visible: widgetData.size === "large"; Layout.fillWidth: true; text: (widgetData.state.temps || []).map(t => t.label + " " + t.value).join(" · "); color: nativeKit.foreground; wrapMode: Text.Wrap; textFormat: Text.PlainText }
                    RowLayout {
                        visible: widgetData.size === "large"
                        Layout.fillWidth: true
                        Repeater {
                            model: (widgetData.state.spools || []).slice(0,4)
                            ColumnLayout {
                                required property var modelData
                                Layout.fillWidth: true
                                Rectangle { Layout.alignment: Qt.AlignHCenter; width: 24; height: 24; radius: 12; color: modelData.color || "transparent"; border.color: nativeKit.border }
                                Text { font.family: nativeKit.fontFamily; Layout.alignment: Qt.AlignHCenter; text: modelData.label; color: nativeKit.foreground; textFormat: Text.PlainText }
                                Text { font.family: nativeKit.fontFamily; Layout.alignment: Qt.AlignHCenter; text: modelData.name; color: nativeKit.muted; textFormat: Text.PlainText }
                            }
                        }
                    }
                    Text { font.family: nativeKit.fontFamily; visible: widgetData.size === "large" && (!widgetData.state.issue || widgetData.state.issue.text !== widgetData.state.stateLine); Layout.fillWidth: true; text: widgetData.state.issue ? widgetData.state.issue.text : widgetData.state.detail || ""; color: nativeKit.muted; wrapMode: Text.Wrap; maximumLineCount: 2; elide: Text.ElideRight; textFormat: Text.PlainText }
                    Item { Layout.fillHeight: true }
                }
                MouseArea { anchors.fill: parent; onClicked: bridge.write(JSON.stringify({action:"show",printer:widgetData.state.serial || ""}) + "\n") }
            }
        }
    }
}
