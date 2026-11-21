import QtQuick
import QtQuick.Layouts

Rectangle {
    id: root
    required property QtObject kit
    required property var snapshot
    required property string size
    property bool connected: true
    property var translate: key => key
    readonly property bool small: size === "small"
    readonly property bool medium: size === "medium"
    readonly property bool large: size === "large"
    readonly property bool active: !!snapshot && snapshot.live && ["printing", "preparing", "paused"].includes(snapshot.state)
    readonly property color tint: snapshot?.tint || kit.accent
    readonly property color statusColor: readable(snapshot?.stateColor || kit.muted, kit.card)
    readonly property color track: Qt.tint(kit.card, Qt.rgba(kit.foreground.r, kit.foreground.g, kit.foreground.b, 0.20))

    // Telemetry colors come from the printer palette. Preserve their hue when
    // possible, blending toward desktop text until the value is legible.
    function luminance(color: color): real {
        function linear(v) { return v <= 0.04045 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4) }
        return 0.2126 * linear(color.r) + 0.7152 * linear(color.g) + 0.0722 * linear(color.b)
    }
    function readable(ink: color, surface: color): color {
        const background = luminance(surface) + 0.05
        let color = ink
        for (let i = 0; i < 12; ++i) {
            const foreground = luminance(color) + 0.05
            if (Math.max(foreground / background, background / foreground) >= 4.5) return color
            color = Qt.tint(color, Qt.rgba(kit.foreground.r, kit.foreground.g, kit.foreground.b, 0.18))
        }
        return kit.foreground
    }
    implicitWidth: small ? 190 : 380
    implicitHeight: large ? 380 : 190
    radius: kit.widgetPalette ? 20 : kit.radius
    color: kit.widgetPalette ? "transparent" : kit.card
    border.width: kit.widgetPalette ? 0 : 1
    border.color: Qt.rgba(kit.foreground.r, kit.foreground.g, kit.foreground.b, 0.14)
    clip: true
    component Line: Text {
        color: root.kit.foreground
        font.family: root.kit.fontFamily
        font.pixelSize: 13
        textFormat: Text.PlainText
        elide: Text.ElideRight
        Layout.fillWidth: true
    }
    Loader {
        anchors.fill: parent
        active: !!root.kit.widgetPalette
        sourceComponent: PlasmaWidgetFace { card: root }
    }
    Loader {
        anchors.fill: parent
        active: !root.kit.widgetPalette
        sourceComponent: Item {
            ColumnLayout {
                anchors.fill: parent
                anchors.margins: root.large ? 16 : 12
                anchors.topMargin: root.medium ? 8 : anchors.leftMargin
                anchors.bottomMargin: root.medium ? 8 : anchors.leftMargin
                spacing: 8
                RowLayout {
                    Line { text: root.snapshot ? root.snapshot.name : "Bambu Bar"; color: root.kit.muted; font.bold: true }
                    RowLayout {
                        visible: !root.small
                        spacing: 5
                        Rectangle { width: 8; height: 8; radius: 4; color: root.statusColor }
                        Line { text: root.snapshot ? root.snapshot.caption : root.translate("Offline"); color: root.kit.foreground; Layout.fillWidth: false }
                    }
                }
                RowLayout {
                    Layout.fillWidth: true
                    Layout.fillHeight: true
                    Layout.minimumHeight: 0
                    spacing: root.small ? 7 : 12
                    Canvas {
                        visible: root.small && root.snapshot !== null
                        Layout.preferredWidth: 62
                        Layout.preferredHeight: 62
                        property real progress: root.active ? Math.min(100, Math.max(0, root.snapshot.progress)) : 0
                        property color ringColor: root.tint
                        property color trackColor: root.track
                        onTrackColorChanged: requestPaint()
                        onProgressChanged: requestPaint()
                        onRingColorChanged: requestPaint()
                        onVisibleChanged: requestPaint()
                        onPaint: {
                            const ctx = getContext("2d"); ctx.reset(); ctx.lineWidth = 6;
                            ctx.strokeStyle = trackColor; ctx.beginPath(); ctx.arc(31,31,26,0,2*Math.PI); ctx.stroke();
                            if (progress > 0) { ctx.strokeStyle = ringColor; ctx.beginPath(); ctx.arc(31,31,26,-Math.PI/2,-Math.PI/2+2*Math.PI*progress/100); ctx.stroke(); }
                        }
                        Text { anchors.centerIn: parent; color: root.kit.foreground; font.bold: true; font.pixelSize: 16; text: root.active ? root.snapshot.progress + "%" : root.snapshot && root.snapshot.state === "finished" ? "✓" : "●" }
                    }
                    Image {
                        visible: !root.small && root.snapshot !== null
                        Layout.preferredWidth: 98
                        Layout.preferredHeight: root.large ? 116 : 88
                        source: root.snapshot && root.snapshot.art ? "file://" + root.snapshot.art : ""
                        fillMode: Image.PreserveAspectFit
                        asynchronous: true
                    }
                    ColumnLayout {
                        Layout.fillWidth: true
                        spacing: root.medium ? 2 : 5
                        Line { visible: root.small; text: root.snapshot ? root.snapshot.caption.toUpperCase() : root.translate("Offline"); color: root.kit.foreground; font.bold: true; font.pixelSize: 11 }
                        Line { visible: !root.small || root.snapshot === null; text: root.snapshot ? root.snapshot.headline : root.connected ? root.translate("Enable desktop widgets in Bambu Bar") : root.translate("Open Bambu Bar to connect"); font.bold: true; wrapMode: Text.WordWrap; maximumLineCount: root.large || root.small ? 2 : 1 }
                        GridLayout {
                            visible: root.active && !root.small
                            Layout.fillWidth: true
                            columns: root.medium ? 2 : 1
                            rowSpacing: 4; columnSpacing: 10
                            Line {
                                Layout.fillWidth: false
                                text: root.snapshot ? root.snapshot.progress + "%" : ""
                                font.pixelSize: root.large ? 30 : 22; font.bold: true
                            }
                            Rectangle {
                                Layout.fillWidth: true; Layout.preferredHeight: 5; radius: 2; color: root.track
                                Rectangle { width: parent.width * Math.min(100, Math.max(0, root.snapshot ? root.snapshot.progress : 0)) / 100; height: 5; radius: 2; color: root.tint }
                            }
                        }
                        Line { visible: !!text; text: root.snapshot && (root.small || root.snapshot.headline !== root.snapshot.stateLine) && !(root.large && root.snapshot.stateLine === root.snapshot.issue?.text) ? root.snapshot.stateLine : ""; wrapMode: Text.WordWrap; maximumLineCount: root.large || root.small ? 2 : 1; font.bold: root.small }
                        Line { visible: !root.small && !!text; text: root.snapshot && root.snapshot.detail !== root.snapshot.stateLine ? root.snapshot.detail : ""; color: root.kit.muted; font.pixelSize: 11 }
                    }
                }
                Line { visible: root.small && root.snapshot !== null && !!root.snapshot.activeSpoolLine; text: root.snapshot ? root.snapshot.activeSpoolLine || "" : ""; font.pixelSize: 11; color: root.kit.muted }
                Line { visible: root.small && root.snapshot !== null; text: root.snapshot ? root.snapshot.headline : ""; font.bold: true }
                Line { visible: root.small && root.snapshot !== null; text: root.snapshot && root.snapshot.detail !== root.snapshot.stateLine ? root.snapshot.detail : ""; font.pixelSize: 11; color: root.kit.muted }
                RowLayout {
                    visible: !root.small && root.snapshot !== null
                    Layout.topMargin: root.medium ? 4 : 0
                    spacing: 6
                    Repeater {
                        model: root.snapshot ? root.snapshot.temps : []
                        Rectangle {
                            required property var modelData
                            Layout.fillWidth: true; Layout.preferredWidth: 1
                            Layout.minimumHeight: temperature.implicitHeight + (root.large ? 12 : 6)
                            radius: 9; color: root.kit.background
                            Column { id: temperature; anchors.centerIn: parent; spacing: 2
                                Text { anchors.horizontalCenter: parent.horizontalCenter; text: modelData.label; color: root.kit.muted; font.pixelSize: 11 }
                                Text { anchors.horizontalCenter: parent.horizontalCenter; text: modelData.value + (modelData.target && modelData.target !== modelData.value ? " → " + modelData.target : ""); color: root.readable(modelData.color || root.kit.foreground, root.kit.background); font.pixelSize: 12; font.bold: true }
                            }
                        }
                    }
                }
                RowLayout {
                    visible: root.large && root.snapshot !== null && (root.snapshot.spools || []).length > 0
                    Line { text: root.snapshot && root.snapshot.ams ? root.snapshot.ams.title : "AMS"; font.pixelSize: 11; color: root.kit.muted }
                    Line { text: root.snapshot && root.snapshot.ams ? root.snapshot.ams.humidity || "" : ""; horizontalAlignment: Text.AlignRight; font.pixelSize: 11; color: root.kit.muted }
                }
                RowLayout {
                    visible: root.large && root.snapshot !== null
                    spacing: 8
                    Repeater {
                        model: root.snapshot ? root.snapshot.spools : []
                        ColumnLayout {
                            required property var modelData
                            Layout.fillWidth: true
                            spacing: 3
                            Rectangle {
                                Layout.alignment: Qt.AlignHCenter
                                width: 50; height: 50; radius: 25; color: "transparent"
                                border.width: 4; border.color: modelData.empty ? root.kit.muted : modelData.color
                                Text { anchors.centerIn: parent; text: modelData.amount || modelData.label; color: root.kit.foreground; font.pixelSize: 11 }
                            }
                            Text { Layout.alignment: Qt.AlignHCenter; text: modelData.name; color: modelData.active ? root.kit.foreground : root.kit.muted; font.pixelSize: 11; font.bold: modelData.active }
                        }
                    }
                }
                Line {
                    visible: root.large && root.snapshot !== null
                    text: root.snapshot ? root.snapshot.issue ? "⚠ " + root.snapshot.issue.text : root.snapshot.lastJob ? root.snapshot.lastJob.name + " · " + root.snapshot.lastJob.ago : root.snapshot.info || "" : ""
                    wrapMode: Text.WordWrap; maximumLineCount: 2; color: root.snapshot && root.snapshot.issue ? root.readable("#ffb83d", root.kit.card) : root.kit.muted; font.pixelSize: 11
                }
            }
        }
    }
}
