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
import "WidgetLayout.mjs" as WidgetLayout

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
    function open() { contextOnly = false; opened = true }
    function close() { opened = false }
    function toggle() { if (opened && !contextOnly) close(); else open() }
    function closeForPopoutSwitch() { close() }
    function send(action, value) {
        bridge.write(JSON.stringify({action: action, value: value, printer: snapshot.printer ? snapshot.printer.id : ""}) + "\n")
    }
    function activate(field, value) {
        if (field.key === "select") { bridge.write(JSON.stringify({action:"select", printer:value}) + "\n"); return }
        if (field.key === "panel") { open(); return }
        if (field.note === "confirmation") { confirmation = field.key; confirmationLabel = field.label; return }
        if (field.key === "speed") { send("speed:" + value); return }
        send(field.key, value)
        if (["show", "settings", "filament", "widgets", "license", "support-panel", "support-menu", "camera", "quit"].indexOf(field.key) !== -1) close()
    }
    onOpenedChanged: { if (!opened) popup.menuBottom = 0; send("open", opened) }
    function fitDropdown(trigger, menuHeight) {
        popup.menuBottom = menuHeight > 0 ? trigger.mapToItem(body, 0, trigger.height).y + menuHeight : 0
    }
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
        labelText: vertical ? "" : root.snapshot.panelText || ""
        fixedWidth: vertical ? -1 : slotSize + (labelText ? panelMetrics.advanceWidth + Commons.Style.space(4) : 0)
        TextMetrics { id: panelMetrics; font.family: button.fontFamily; font.pixelSize: button.fontSize; text: button.labelText }
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
            else {
                const context = mouseButton === Qt.RightButton
                const dismiss = root.opened && root.contextOnly === context
                root.contextOnly = context
                root.opened = !dismiss
            }
        }
    }
    Ui.PopupCard {
        id: popup
        anchorItem: button
        bar: root.bar
        owner: root
        open: root.opened
        contentWidth: root.contextOnly ? fittedContentWidth(220, 220) : fittedContentWidth(420, 420)
        property real menuBottom: 0
        contentHeight: fittedContentHeight(Math.max(body.implicitHeight, menuBottom), 850)
        Controls.ScrollView {
            font: nativeKit.nativeFont
            anchors.fill: parent
            clip: true
            contentWidth: availableWidth
            ColumnLayout {
                id: body
                width: parent.width
                spacing: 12
                Common.QuickView {
                    visible: !root.contextOnly; Layout.fillWidth: true
                    kit: nativeKit; controller: root; snapshot: root.snapshot
                }
                ColumnLayout {
                    visible: root.contextOnly
                    Layout.fillWidth: true
                    spacing: 0
                    Repeater {
                        model: [
                            {key:"panel", label:"Quick view"}, {key:"show", label:"Open Bambu Bar"},
                            {key:"filament", label:"Filament"}, {key:"widgets", label:"Widgets"},
                            {key:"settings", label:"Settings"}, {key:"support-menu", label:"Support this project", icon:"love"},
                            {key:"license", label:"Manage License…", icon:"object-unlocked"}, {key:"quit", label:"Quit"}
                        ]
                        Ui.Button {
                            required property var modelData
                            Layout.fillWidth: true
                            leftAlign: true
                            bordered: false
                            iconText: nativeKit.glyph(modelData.icon)
                            text: root.t(modelData.label)
                            onClicked: root.activate(modelData)
                        }
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
    readonly property var desktopScreen: Quickshell.screens[0] || null
    // A layer with exclusiveZone 0 is positioned inside the compositor's work area.
    readonly property real workWidth: desktopScreen ? desktopScreen.width - (vertical ? barSize : 0) : 0
    readonly property real workHeight: desktopScreen ? desktopScreen.height - (vertical ? 0 : barSize) : 0
    readonly property var placements: {
        const widgets = snapshot.widgets || []
        if (!desktopScreen || !widgets.length) return {}
        const cards = widgets.map(w => ({
            corner:w.corner, width:Math.min(w.size === "small" ? 190 : 380, workWidth),
            height:Math.min(w.size === "large" ? 380 : 190, workHeight)
        }))
        const positions = WidgetLayout.placeWidgets(cards, {x:0, y:0, width:workWidth, height:workHeight})
        const result = {}
        widgets.forEach((w, i) => {
            const card = cards[i], pos = positions[i]
            result[w.id] = Object.assign({}, w, {width:card.width, height:card.height,
                insetX:w.corner.endsWith("left") ? pos.x : workWidth - pos.x - card.width,
                insetY:w.corner.startsWith("top") ? pos.y : workHeight - pos.y - card.height})
        })
        return result
    }
    Variants {
        // Desktop cards belong to one output, avoiding duplicate cards on multi-monitor bars.
        model: button.QsWindow.window && button.QsWindow.window.screen === Quickshell.screens[0] ? root.widgetIds : []
        delegate: PanelWindow {
            required property string modelData
            readonly property var widgetData: root.placements[modelData] || {size:"small",corner:"top-right",insetX:20,insetY:20,width:190,height:190,state:null}
            screen: root.desktopScreen
            color: "transparent"
            implicitWidth: widgetData.width
            implicitHeight: widgetData.height
            anchors.top: !widgetData.corner.startsWith("bottom")
            anchors.bottom: widgetData.corner.startsWith("bottom")
            anchors.left: widgetData.corner.endsWith("left")
            anchors.right: !widgetData.corner.endsWith("left")
            margins.top: widgetData.insetY; margins.bottom: widgetData.insetY
            margins.left: widgetData.insetX; margins.right: widgetData.insetX
            WlrLayershell.namespace: "bambu-bar-widget-" + modelData
            exclusiveZone: 0
            WlrLayershell.layer: WlrLayer.Bottom
            WlrLayershell.keyboardFocus: WlrKeyboardFocus.None
            Common.WidgetCard {
                anchors.fill: parent
                kit: nativeKit
                snapshot: widgetData.state
                size: widgetData.size
                translate: root.t
                MouseArea { anchors.fill: parent; onClicked: bridge.write(JSON.stringify({action:"show",printer:widgetData.state?.serial || ""}) + "\n") }
            }
        }
    }
}
