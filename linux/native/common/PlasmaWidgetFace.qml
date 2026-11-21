import QtQuick
import QtQuick.Layouts
import QtQuick.Controls as Controls

Rectangle {
    id: face
    required property Item card
    readonly property var telemetry: card.snapshot || ({})
    readonly property var colors: card.kit.widgetPalette
    readonly property color ink: colors.ink
    readonly property color secondary: Qt.tint(colors.surface, Qt.alpha(ink, .73))
    readonly property color rule: Qt.alpha(ink, .15)
    readonly property color accent: telemetry.live ? legible(telemetry.tint || telemetry.stateColor || card.kit.accent) : secondary
    readonly property color stateInk: legible(telemetry.stateColor || secondary)
    readonly property bool small: card.small
    readonly property bool large: card.large
    readonly property bool runningJob: card.active
    readonly property real fraction: Math.max(0, Math.min(1, (telemetry.progress || 0) / 100))
    readonly property string headline: telemetry.headline || (card.connected ? card.translate("Enable desktop widgets in Bambu Bar") : card.translate("Open Bambu Bar to connect"))
    readonly property bool incident: large && !!telemetry.live && telemetry.state === "failed" && !!telemetry.issue?.text
    readonly property bool connectionNote: large && !telemetry.live && !!telemetry.info
    readonly property string detail: telemetry.stateLine !== headline && (!large || telemetry.stateLine !== footerText) ? telemetry.stateLine || "" : ""
    readonly property string footerText: telemetry.issue?.text || (telemetry.lastJob ? [telemetry.lastJob.name, telemetry.lastJob.ago].filter(Boolean).join(" · ") : telemetry.info || "")
    readonly property bool hasTemps: !!telemetry.live && !!telemetry.temps?.length
    readonly property bool hasAms: large && !!telemetry.live && !!telemetry.spools?.length
    function legible(raw: color): color {
        let color = raw
        const bg = card.luminance(colors.surface) + .05
        for (let i = 0; i < 12; ++i) {
            const fg = card.luminance(color) + .05
            if (Math.max(fg, bg) / Math.min(fg, bg) >= 4.5) return color
            color = Qt.tint(color, Qt.alpha(ink, .2))
        }
        return ink
    }
    function sensorIcon(kind) {
        return ({bed:"heat-waves", chamber:"chamber", fan:"fan", nozzle:"fire"})[kind] || "temperature-normal"
    }
    radius: card.radius
    border.color: Qt.alpha(ink, .15)
    gradient: Gradient {
        GradientStop { position: 0; color: Qt.tint(face.colors.surface, Qt.alpha(face.accent, .09)) }
        GradientStop { position: .58; color: face.colors.surface }
        GradientStop { position: 1; color: Qt.tint(face.colors.surface, Qt.rgba(0,0,0,.12)) }
    }
    component Label: Text {
        Layout.fillWidth: true; Layout.minimumWidth: 0
        color: face.ink; font.family: face.card.kit.fontFamily; font.pixelSize: 13
        textFormat: Text.PlainText; elide: Text.ElideRight
    }
    component Glyph: Symbol {
        kit: face.card.kit; tint: face.secondary
        Layout.preferredWidth: 14; Layout.preferredHeight: 14
    }
    component Divider: Rectangle { Layout.fillWidth: true; implicitHeight: 1; color: face.rule }
    component Art: Image {
        source: face.telemetry.art ? "file://" + face.telemetry.art : ""
        fillMode: Image.PreserveAspectFit; asynchronous: true; mipmap: true
    }

    component Identity: RowLayout {
            Layout.fillWidth: true; spacing: 6
            Label { text: face.telemetry.name || "Bambu Bar"; font.weight: Font.DemiBold; color: face.secondary; font.pixelSize: face.small ? 12 : 13 }
            Rectangle {
                visible: !!face.telemetry.live && !face.small && !!face.telemetry.caption
                implicitWidth: statusText.implicitWidth + 25; implicitHeight: 19
                Layout.maximumWidth: face.width * .40
                radius: height / 2; color: Qt.alpha(face.stateInk,.13)
                RowLayout {
                    id: status; anchors.centerIn: parent; spacing: 5
                    Rectangle { implicitWidth: 6; implicitHeight: 6; radius: 3; color: face.stateInk }
                    Label { id: statusText; text: face.telemetry.caption || ""; font.pixelSize: 11; font.weight: Font.DemiBold; color: face.stateInk; Layout.maximumWidth: face.width * .40 - 25 }
                }
            }
        }

    ColumnLayout {
        anchors.fill: parent; anchors.margins: face.small ? 14 : face.large ? 16 : 12
        spacing: face.small ? 5 : face.large ? 7 : 6
        Identity { visible: face.small }
        RowLayout {
            Layout.fillWidth: true; Layout.fillHeight: true; Layout.minimumHeight: 0
            spacing: face.small ? 9 : 14
            Item {
                Layout.preferredWidth: face.small ? 58 : Math.min(face.width * (face.connectionNote ? .38 : .29), 160)
                Layout.fillHeight: true
                Canvas {
                    anchors.fill: parent
                    visible: !face.small && !!face.telemetry.live && !!face.telemetry.art
                    property color light: face.accent
                    onLightChanged: requestPaint()
                    onWidthChanged: requestPaint()
                    onHeightChanged: requestPaint()
                    onPaint: {
                        if (width < 1 || height < 1) return
                        const c = getContext("2d")
                        c.reset()
                        const r = Math.min(width, height) * .54
                        const glow = c.createRadialGradient(width/2,height/2,0,width/2,height/2,r)
                        glow.addColorStop(0, Qt.rgba(.90,.94,1,.23))
                        glow.addColorStop(.63, Qt.alpha(light,.08)); glow.addColorStop(1, "transparent")
                        c.fillStyle = glow; c.fillRect(0,0,width,height)
                    }
                }
                Art {
                    anchors.fill: parent
                    visible: !dial.visible
                }
                Glyph {
                    visible: !dial.visible && !face.telemetry.art
                    anchors.centerIn: parent; width: face.small ? 30 : 44; height: width
                    name: "printer"
                }
                Canvas {
                    id: dial
                    anchors.centerIn: parent; width: 58; height: 58
                    visible: face.small && face.runningJob
                    property real progress: face.fraction
                    property color stroke: face.accent
                    property color track: face.rule
                    onProgressChanged: requestPaint()
                    onStrokeChanged: requestPaint()
                    onTrackChanged: requestPaint()
                    onVisibleChanged: requestPaint()
                    onPaint: {
                        const c = getContext("2d"); c.reset(); c.lineWidth = 5; c.lineCap = "round"
                        c.strokeStyle = track; c.beginPath(); c.arc(29,29,25,0,Math.PI*2); c.stroke()
                        if (progress > 0) { c.strokeStyle = stroke; c.beginPath(); c.arc(29,29,25,-Math.PI/2,2*Math.PI*progress-Math.PI/2); c.stroke() }
                    }
                    Label { anchors.centerIn: parent; text: (face.telemetry.progress || 0) + "%"; font.pixelSize: 17; font.weight: Font.Bold }
                }
            }
            ColumnLayout {
                Layout.fillWidth: true; Layout.minimumWidth: 0; spacing: face.small ? 4 : 2
                Identity { visible: !face.small }
                Label { visible: face.small && !!face.telemetry.live && !!text; text: face.telemetry.caption || ""; color: face.stateInk; font.weight: Font.DemiBold; font.pixelSize: 12 }
                Label {
                    visible: !face.small; text: face.headline; font.pixelSize: face.large ? 20 : 17; font.weight: Font.DemiBold
                    wrapMode: Text.Wrap; maximumLineCount: face.large && !face.hasAms ? 2 : 1
                }
                RowLayout {
                    visible: !face.small && face.runningJob; Layout.fillWidth: true
                    Label { text: (face.telemetry.progress || 0) + "%"; font.pixelSize: face.large ? 32 : 26; font.weight: Font.Bold }
                    Label { Layout.fillWidth: false; text: face.telemetry.layers || ""; color: face.secondary; font.pixelSize: 11 }
                }
                Rectangle {
                    visible: !face.small && face.runningJob
                    Layout.fillWidth: true; implicitHeight: 5; radius: 3; color: face.rule
                    Rectangle { width: parent.width * face.fraction; height: parent.height; radius: 3; color: face.accent }
                }
                RowLayout {
                    Layout.fillWidth: true; spacing: 5
                    visible: !!face.detail || !!finish.text
                    Label { visible: !!text; text: face.detail; wrapMode: face.small || face.large || !face.runningJob ? Text.Wrap : Text.NoWrap; maximumLineCount: 2; font.pixelSize: 13; font.weight: face.small ? Font.DemiBold : Font.Normal }
                    Label {
                        id: finish; visible: !!text
                        Layout.fillWidth: false; Layout.maximumWidth: face.width * .24
                        text: !face.small && face.telemetry.finish ? (face.telemetry.strings?.done || (face.card.translate("Done") + " {0}")).replace("{0}",face.telemetry.finish) : ""
                        color: face.secondary; font.pixelSize: 11
                    }
                }
                Label { visible: !face.small && !!face.telemetry.live && !face.incident && !face.telemetry.layers && !face.telemetry.finish && !!text; text: face.telemetry.detail !== face.detail ? face.telemetry.detail || "" : ""; color: face.secondary; font.pixelSize: 11 }
            }
        }
        ColumnLayout {
            visible: face.small; Layout.fillWidth: true; spacing: 3
            RowLayout {
                visible: !!face.telemetry.activeSpoolLine
                spacing: 5
                Rectangle {
                    readonly property string filament: (face.telemetry.spools || []).find(s => s.active && !s.empty)?.color || ""
                    visible: !!filament; color: filament || "transparent"; implicitWidth: 8; implicitHeight: 8; radius: 4; border.color: face.rule
                }
                Label { text: face.telemetry.activeSpoolLine || ""; color: face.secondary; font.pixelSize: 11 }
            }
            Label { text: face.headline; font.weight: Font.DemiBold; font.pixelSize: 15; wrapMode: Text.Wrap; maximumLineCount: 2 }
            Label { text: face.telemetry.detail || ""; visible: !!text && text !== face.detail; color: face.secondary; font.pixelSize: 11 }
        }
        Rectangle {
            visible: face.incident || face.connectionNote
            Layout.fillWidth: true; implicitHeight: message.implicitHeight + 18; radius: 10
            color: face.incident ? Qt.alpha(face.stateInk,.10) : Qt.alpha(face.ink,.07)
            RowLayout {
                id: message; anchors.verticalCenter: parent.verticalCenter
                anchors.left: parent.left; anchors.right: parent.right; anchors.margins: 10; spacing: 8
                Glyph {
                    Layout.alignment: Qt.AlignTop; Layout.topMargin: 2
                    name: face.incident ? "widget-warning" : face.telemetry.link === "unauthorized" ? "object-unlocked" : "widget-disconnected"
                    tint: face.incident ? face.stateInk : face.secondary
                }
                Label {
                    text: face.incident ? face.telemetry.issue.text : face.telemetry.info || ""
                    font.pixelSize: 12; wrapMode: Text.Wrap
                    // Reserve the AMS and temperature rows before allowing a longer incident summary.
                    maximumLineCount: face.incident && face.hasAms ? Math.min(6, 3 + Math.floor(Math.max(0, face.height - 380) / 18)) : 6
                    Controls.ToolTip.visible: messageHover.hovered && truncated
                    Controls.ToolTip.text: text
                    HoverHandler { id: messageHover }
                }
            }
        }
        Rectangle {
            visible: !face.small && face.hasTemps
            Layout.fillWidth: true; implicitHeight: face.large ? 51 : 30; radius: 10
            color: Qt.alpha(face.ink,.065)
            RowLayout {
                anchors.fill: parent; anchors.leftMargin: 7; anchors.rightMargin: 7; spacing: 2
                Repeater {
                    model: face.telemetry.temps || []
                    Item {
                        id: sensor
                        required property var modelData
                        Layout.fillWidth: true; Layout.preferredWidth: 1; Layout.fillHeight: true
                        readonly property string reading: modelData.value + (modelData.target && Math.abs(parseFloat(modelData.target) - parseFloat(modelData.value)) > 2 ? " → " + modelData.target : "")
                        ColumnLayout {
                            anchors.centerIn: parent; width: parent.width; spacing: 1
                            RowLayout {
                                Layout.alignment: Qt.AlignHCenter; spacing: 4
                                Glyph { name: face.sensorIcon(sensor.modelData.kind); tint: face.legible(sensor.modelData.color || face.secondary); Layout.preferredWidth: 12; Layout.preferredHeight: 12 }
                                Label { visible: face.large; text: sensor.modelData.label; font.pixelSize: 11; color: face.secondary; Layout.fillWidth: false }
                                Label { visible: !face.large; text: sensor.reading; font.pixelSize: 12; font.weight: Font.DemiBold; Layout.fillWidth: false }
                            }
                            Label { visible: face.large; text: sensor.reading; horizontalAlignment: Text.AlignHCenter; font.pixelSize: 17; font.weight: Font.DemiBold }
                        }
                        Controls.ToolTip.visible: sensorHover.hovered
                        Controls.ToolTip.text: modelData.label + " · " + reading
                        HoverHandler { id: sensorHover }
                    }
                }
            }
        }
        ColumnLayout {
            visible: face.hasAms; Layout.fillWidth: true; spacing: 7
            RowLayout {
                Layout.fillWidth: true
                Label { text: face.telemetry.ams?.title || "AMS"; font.weight: Font.DemiBold }
                Label { text: face.telemetry.ams?.humidity || ""; color: face.secondary; horizontalAlignment: Text.AlignRight; font.pixelSize: 12 }
            }
            RowLayout {
                Layout.fillWidth: true; spacing: 4
                Repeater {
                    model: (face.telemetry.spools || []).slice(0,4)
                    ColumnLayout {
                        id: slot
                        required property var modelData
                        Layout.fillWidth: true; Layout.preferredWidth: 1; spacing: 2
                        PlasmaSpool {
                            Layout.alignment: Qt.AlignHCenter
                            spool: slot.modelData; active: face.runningJob && !!slot.modelData.active
                            ink: face.ink; accent: face.accent; surface: face.colors.surface
                        }
                        Label { horizontalAlignment: Text.AlignHCenter; text: [slot.modelData.label, slot.modelData.name].filter(Boolean).join(" · "); font.pixelSize: 11; font.weight: Font.DemiBold }
                        Label { horizontalAlignment: Text.AlignHCenter; text: slot.modelData.empty ? "" : slot.modelData.amount || ""; font.pixelSize: 11; color: face.secondary }
                    }
                }
            }
        }
        Divider { visible: face.large && !face.incident && !face.connectionNote && !!face.footerText }
        RowLayout {
            visible: face.large && !face.incident && !face.connectionNote && !!face.footerText; Layout.fillWidth: true; spacing: 7
            Glyph { name: face.telemetry.issue ? "widget-warning" : face.telemetry.lastJob?.outcome === "finished" ? "widget-finished" : face.telemetry.lastJob ? "widget-stopped" : "help-about"; tint: face.telemetry.issue ? face.stateInk : face.secondary }
            Label { text: face.footerText; color: face.secondary; font.pixelSize: 12; wrapMode: Text.Wrap; maximumLineCount: 2 }
        }
    }
}
