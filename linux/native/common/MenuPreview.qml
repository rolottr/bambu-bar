import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import QtQuick.Effects

ColumnLayout {
    id: preview
    required property QtObject kit
    required property var controller
    readonly property var options: controller.sections.reduce((fields, section) => fields.concat(section.fields), [])
    function setting(key) { return options.find(field => field.key === key)?.value }
    readonly property var configuredPrinter: (controller.desktop.fleet || []).find(printer => printer.serial === setting("menuBarPrinter")) || controller.desktop.printer || {}
    readonly property string panelText: ({percent:"50%", remaining:"1h 12m", both:"50% · 1h 12m"})[setting("menuBarStyle")] || ""
    readonly property string panelTitle: (configuredPrinter.name || "Bambu Bar") + " · " + controller.t("Printing") + " · 50%"
    readonly property var sampleSpools: ["61C680FF","F330F9FF","222222FF","FFFFFFFF"].map((color, i) => ({
        slot:"A" + (i + 1), name:"PLA", color:color, remaining:[76, 48, 99, 25][i] + "%"
    }))
    readonly property var sample: ({
        id: configuredPrinter.id || configuredPrinter.serial, name:configuredPrinter.name || "Bambu Bar", art:configuredPrinter.art || "",
        state:controller.t("Printing"), job:"Articulated Dragon", progress:50, detail:"1h 12m", online:true,
        temperatures:setting("panelShowsTemperatures") ? [{name:controller.t("Nozzle"),value:"220°"}, {name:controller.t("Bed"),value:"55°"}].concat(setting("panelShowsChamber") ? [{name:controller.t("Chamber"),value:"30°"}] : []) : [],
        fans:setting("panelShowsFans") ? controller.t("Fans") + "   70% · 40% · 50%" : "",
        ams:setting("panelShowsFilament") ? [{title:"AMS", spools:sampleSpools}] : [],
        showControls:setting("showPrinterActions"), pause:true, stop:true, light:true,
        canSpeed:setting("showPrinterActions"), speed:2, speeds:[{id:2,title:controller.t("Standard")}]
    })
    property string highlight: ""
    Connections {
        target: preview.controller
        function onRequest(message) {
            const region = ({menuBarStyle:"icon", menuBarPrinter:"header", panelShowsTemperatures:"temperatures",
                             panelShowsChamber:"temperatures", panelShowsFans:"fans", panelShowsFilament:"filament",
                             showPrinterActions:"actions"})[message.action]
            if (region) { preview.highlight = region; clearHighlight.restart() }
        }
    }
    Timer { id: clearHighlight; interval: 1600; onTriggered: preview.highlight = "" }
    spacing: 8
    PreviewBackdrop {
        id: backdrop
        Layout.fillWidth: true
        implicitHeight: panel.implicitHeight + bar.height + 26
        kit: preview.kit
        Rectangle {
            id: panel
            anchors.left: parent.left; anchors.right: parent.right; anchors.top: parent.top
            anchors.leftMargin: 10; anchors.rightMargin: 10
            anchors.topMargin: preview.kit.panelAtTop ? bar.height + 10 : 10
            implicitHeight: samplePanel.implicitHeight + 20
            color: preview.kit.background
            radius: preview.kit.radius
            QuickView {
                id: samplePanel
                anchors.fill: parent; anchors.margins: 10
                kit: preview.kit; highlight: preview.highlight
                // No preview interaction can invoke the printer or change an app preference.
                controller: ({t:key => preview.controller.t(key), activate:() => {}})
                snapshot: ({title:preview.panelTitle, printer:preview.sample, printers:preview.controller.desktop.printers || [], supporter:preview.controller.supporter})
            }
            // Leave the native controls at full contrast and keep mouse presses inside the example.
            MouseArea { anchors.fill: parent; acceptedButtons: Qt.AllButtons }
        }
        Rectangle {
            id: bar
            y: preview.kit.panelAtTop ? 0 : parent.height - height
            width: parent.width; height: 32
            color: preview.kit.card
            border.width: preview.highlight === "icon" ? 2 : 0; border.color: preview.kit.accent
            RowLayout {
                anchors.fill: parent; anchors.leftMargin: 10; anchors.rightMargin: 10; spacing: 6
                Label { Layout.fillWidth: true; text: preview.panelTitle; color: preview.kit.muted; elide: Text.ElideRight; font.pixelSize: 11 }
                Image {
                    Layout.preferredWidth: 18; Layout.preferredHeight: 18
                    source: Qt.resolvedUrl("tray-printing-half.svg")
                    layer.enabled: true
                    layer.effect: MultiEffect { brightness: 1; colorization: 1; colorizationColor: preview.kit.foreground }
                }
                Label { text: preview.panelText; color: preview.kit.foreground; visible: !!text }
                Rectangle { implicitWidth: 1; implicitHeight: 14; color: preview.kit.border }
                Label { text: Qt.formatTime(new Date(), Qt.locale().timeFormat(Locale.ShortFormat)); color: preview.kit.foreground }
            }
            HoverHandler { id: hover }
            ToolTip.visible: hover.hovered
            ToolTip.text: preview.panelTitle
        }
    }
    Label {
        Layout.fillWidth: true; wrapMode: Text.Wrap; horizontalAlignment: Text.AlignHCenter
        color: preview.kit.muted; text: preview.controller.t("Sample print. Changes show here as you make them.")
    }
}
