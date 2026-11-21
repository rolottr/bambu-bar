import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import QtQuick.Effects
import QtQuick.Window

GridLayout {
    id: showcase
    required property QtObject kit
    required property var controller
    property string size: "small"
    property string scene: "printing"
    columns: width >= 680 ? 2 : 1
    columnSpacing: 20
    rowSpacing: 14

    function t(key) { return controller.t(key) }
    readonly property var scenes: [
        {id:"printing", title:t("Printing"), percent:50, line:"1h 12m " + t("left"), detail:"144/284"},
        {id:"preparing", title:t("Heating"), percent:0, line:t("Auto bed leveling"), detail:"0/284"},
        {id:"paused", title:t("Paused"), percent:64, line:t("Filament ran out"), detail:"182/284"},
        {id:"finished", title:t("Finished"), percent:100, line:t("Ready to remove"), detail:"284/284"},
        {id:"idle", title:t("Ready"), percent:0, line:t("Ready to print"), detail:""}
    ]
    readonly property var selected: scenes.find(item => item.id === scene)
    readonly property var current: (controller.desktop.fleet || []).find(p => p.serial === controller.desktop.printer?.id)
    property int tick: 4
    readonly property int printPercent: Math.min(96, 18 + Math.round(tick * 78 / 8))
    readonly property int remaining: Math.max(1, Math.round(148 * (1 - printPercent / 100)))
    readonly property var sampleTemps: {
        const values = scene === "idle" ? [26,24,23] : scene === "preparing" ? [95 + tick * 14,30 + tick * 3,28] :
                       scene === "finished" ? [120 - tick * 7,52 - tick * 2,30] : [220 + tick % 2,55,34]
        const defaults = [{label:t("Nozzle"),color:"#bb531a"}, {label:t("Bed"),color:"#b98016"}, {label:t("Chamber"),color:"#738cc1"}]
        return (current?.temps?.length ? current.temps : defaults).map((temp,i) => Object.assign({}, temp, {
            value:(values[i] ?? values[0]) + "°",
            target: scene !== "idle" && scene !== "finished" && i < 2 ? [220,55][i] + "°" : ""
        }))
    }
    readonly property var sample: ({
        name:current?.name || "Bambu Bar", art:current?.art || "", state:scene, live:true,
        caption:selected.title,
        stateLine:scene === "printing" ? remaining + "m " + t("left") : selected.line,
        progress:scene === "printing" ? printPercent : selected.percent,
        detail:scene === "printing" ? t("Done") + " " + Qt.formatTime(new Date(Date.now() + remaining * 60000), Qt.locale().timeFormat(Locale.ShortFormat)) + " · " + Math.round(284 * printPercent / 100) + "/284" : selected.detail,
        headline:scene === "idle" ? t("Ready to print") : "Articulated Dragon",
        activeSpoolLine:scene === "printing" || scene === "paused" ? "PLA Matte · A1" : "",
        temps:sampleTemps, spools:current?.spools || [], ams:current?.ams,
        stateColor:sceneColors[sceneIndex],
        issue: scene === "paused" ? {text:t("Filament ran out")} : null,
        lastJob: scene === "finished" || scene === "idle" ? {name:"Articulated Dragon", ago:scene === "idle" ? "2h" : t("Just now")} : null
    })

    readonly property var sizes: ["small","medium","large"]
    readonly property var sceneIcons: ["printer","fire","media-playback-pause","dialog-ok","night"]
    readonly property var sceneColors: [kit.accent, "#ed8738", "#e74f69", "#32a76e", kit.muted]
    readonly property int sceneIndex: scenes.findIndex(s => s.id === scene)
    readonly property bool keyboardInside: {
        const focused = Window.window?.activeFocusItem
        if (!focused || !focused.visualFocus) return false
        for (let item = focused; item; item = item.parent)
            if (item === showcase) return true
        return false
    }
    HoverHandler { id: tourHover }
    Timer {
        id: tour
        interval: 400
        repeat: true
        running: showcase.visible && !!showcase.Window.window?.visible && showcase.kit.animationsEnabled && !tourHover.hovered && !showcase.keyboardInside
        onTriggered: {
            if (showcase.tick < 8) { ++showcase.tick; return }
            const next = (showcase.sceneIndex + 1) % showcase.scenes.length
            if (next === 0) showcase.size = showcase.sizes[(showcase.sizes.indexOf(showcase.size) + 1) % showcase.sizes.length]
            showcase.scene = showcase.scenes[next].id
        }
    }
    onSceneChanged: {
        tick = kit.animationsEnabled ? 0 : 4
        if (kit.animationsEnabled) fadeIn.restart()
    }
    NumberAnimation { id: fadeIn; target: widget; property: "opacity"; from: 0.45; to: 1; duration: 220 }
    PreviewBackdrop {
        id: stage
        kit: showcase.kit
        Layout.fillWidth: true
        Layout.minimumWidth: Math.min(showcase.width, 380)
        Layout.preferredWidth: 462
        Layout.alignment: Qt.AlignTop
        readonly property real cardScale: Math.min((width - 24) / widget.implicitWidth,
                                                   showcase.size === "small" ? 1.3 : showcase.size === "medium" ? 1.1 : 1)
        implicitHeight: widget.implicitHeight * cardScale + 48 + sceneBar.height
        Behavior on implicitHeight { enabled: showcase.kit.animationsEnabled; NumberAnimation { duration: 250; easing.type: Easing.OutCubic } }
        Rectangle {
            anchors.centerIn: widget
            width: widget.width * stage.cardScale; height: widget.height * stage.cardScale
            radius: widget.radius * stage.cardScale
            color: "#40000000"
            layer.enabled: true
            layer.effect: MultiEffect { shadowEnabled: true; shadowOpacity: 0.55; shadowVerticalOffset: 4; shadowBlur: 0.6 }
        }
        WidgetCard {
            id: widget
            anchors.horizontalCenter: parent.horizontalCenter
            anchors.verticalCenter: parent.verticalCenter
            anchors.verticalCenterOffset: -sceneBar.height / 2
            scale: stage.cardScale
            kit: showcase.kit
            size: showcase.size
            snapshot: showcase.sample
            translate: showcase.t
        }
        Rectangle {
            id: sceneBar
            width: parent.width; height: 50
            anchors.bottom: parent.bottom
            color: showcase.kit.card
            Rectangle {
                readonly property real cellWidth: (sceneBar.width - 8) / showcase.scenes.length
                x: 4 + showcase.sceneIndex * cellWidth
                y: 4; width: cellWidth; height: parent.height - 8
                radius: Math.max(2, showcase.kit.radius)
                color: Qt.tint(showcase.kit.card, Qt.alpha(showcase.selectedColor, 0.12))
                border.color: showcase.selectedColor
                Behavior on x { enabled: showcase.kit.animationsEnabled; NumberAnimation { duration: 220; easing.type: Easing.OutCubic } }
            }
            Row {
                anchors.fill: parent; anchors.margins: 4
                Repeater {
                    model: showcase.scenes
                    ToolButton {
                        id: sceneButton
                        required property var modelData
                        required property int index
                        width: parent.width / showcase.scenes.length; height: parent.height
                        checkable: true; checked: showcase.sceneIndex === index
                        text: modelData.title
                        Accessible.name: text
                        background: Rectangle {
                            radius: showcase.kit.radius
                            color: sceneButton.hovered ? Qt.alpha(showcase.kit.foreground, 0.07) : "transparent"
                            border.width: sceneButton.visualFocus ? 2 : 0
                            border.color: showcase.kit.foreground
                        }
                        contentItem: ColumnLayout {
                            spacing: 0
                            Symbol { Layout.alignment: Qt.AlignHCenter; kit: showcase.kit; name: showcase.sceneIcons[sceneButton.index]; tint: sceneButton.checked ? showcase.selectedColor : showcase.kit.muted }
                            Label {
                                Layout.fillWidth: true; horizontalAlignment: Text.AlignHCenter
                                text: sceneButton.text; font.family: showcase.kit.fontFamily; font.pixelSize: 12
                                font.bold: sceneButton.checked; color: showcase.kit.foreground
                                elide: Text.ElideRight
                            }
                        }
                        onClicked: { showcase.scene = modelData.id; if (tour.running) tour.restart() }
                    }
                }
            }
        }
    }
    readonly property color selectedColor: widget.readable(sceneColors[sceneIndex], kit.card)
    ColumnLayout {
        Layout.alignment: Qt.AlignTop
        Layout.fillWidth: true; Layout.preferredWidth: 270
        spacing: 10
        Rectangle {
            Layout.fillWidth: true
            implicitHeight: sizeRow.implicitHeight + 6
            radius: showcase.kit.radius
            color: showcase.kit.card
            border.color: showcase.kit.muted
            RowLayout {
                id: sizeRow
                anchors.left: parent.left; anchors.right: parent.right; anchors.margins: 3; anchors.verticalCenter: parent.verticalCenter
                spacing: 0
                Repeater {
                    model: showcase.sizes
                    Field {
                        required property string modelData
                        Layout.fillWidth: true; Layout.preferredWidth: 1
                        kit: showcase.kit
                        field: ({kind:"segment", selected:modelData === showcase.size, label:showcase.t(modelData[0].toUpperCase() + modelData.slice(1))})
                        controller: ({activate: () => { showcase.size = modelData; if (tour.running) tour.restart() }})
                    }
                }
            }
        }
        Frame {
            Layout.fillWidth: true; padding: 14
            background: Rectangle { color: showcase.kit.card; radius: showcase.kit.radius }
            contentItem: ColumnLayout {
                spacing: 12
                Label { text: showcase.t(showcase.size[0].toUpperCase()+showcase.size.slice(1)); color: showcase.kit.foreground; font.pixelSize: 18; font.bold: true }
                Label {
                    Layout.fillWidth: true; wrapMode: Text.Wrap; color: showcase.kit.muted
                    text: showcase.t(showcase.size === "small" ? "Follow progress and time left at a glance. The status line changes when the printer heats, pauses or finishes." : showcase.size === "large" ? "See the job, temperatures and every loaded spool together. Printer issues and the last completed print appear below the job." : "See the printer, current job, progress and layers together, with temperatures along the bottom.")
                }
                Flow {
                    Layout.fillWidth: true
                    spacing: 5
                    Repeater {
                        model: [
                            ["progress","Progress"], ["chronometer","Time left"], ["appointment-new","Finish time"],
                            ["layer-visible-on","Layers"], ["media-playback-pause","Stage & pause reason"],
                            ["temperature-normal","Temperatures"], ["draw-donut","AMS spools"],
                            ["printer","Printer drawing"], ["dialog-warning","Errors"], ["view-history","Last print"]
                        ]
                        Rectangle {
                            required property var modelData
                            required property int index
                            readonly property bool included: showcase.size === "large" || (showcase.size === "small" ? index !== 7 && index !== 8 : index !== 8 && index !== 9)
                            width: chip.implicitWidth + 14; height: chip.implicitHeight + 6; radius: height / 2
                            color: Qt.tint(showcase.kit.card, Qt.alpha(included ? showcase.kit.accent : showcase.kit.foreground, included ? 0.14 : 0.055))
                            Accessible.role: Accessible.StaticText
                            Accessible.name: showcase.t(modelData[1]) + " · " + showcase.t(included ? "Showing" : "Hidden")
                            Row {
                                id: chip
                                anchors.centerIn: parent; spacing: 4
                                Symbol { anchors.verticalCenter: parent.verticalCenter; width: 14; height: 14; kit: showcase.kit; name: modelData[0]; tint: parent.parent.included ? widget.readable(showcase.kit.accent, parent.parent.color) : showcase.kit.muted }
                                Label { anchors.verticalCenter: parent.verticalCenter; text: showcase.t(modelData[1]); font.pixelSize: 12; color: parent.parent.included ? showcase.kit.foreground : showcase.kit.muted }
                            }
                        }
                    }
                }
            }
        }
    }
}
