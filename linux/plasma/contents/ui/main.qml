import QtQuick
import QtQuick.Layouts
import org.kde.plasma.plasmoid
import org.kde.plasma.core as PlasmaCore

PlasmoidItem {
    id: root
    preferredRepresentation: fullRepresentation
    Plasmoid.backgroundHints: PlasmaCore.Types.NoBackground
    readonly property int cardSize: Math.max(0, Math.min(2, Plasmoid.configuration.size))
    readonly property bool small: cardSize === 0
    readonly property bool large: cardSize === 2
    readonly property var states: connection.snapshot.widgetPrinters || []
    readonly property var state: (states.find(p => p.id === Plasmoid.configuration.printer) || {}).state || null
    readonly property bool active: state && state.live && ["printing", "preparing", "paused"].includes(state.state)
    readonly property color tint: state && /^#[0-9a-f]{6}$/i.test(state.tint) ? state.tint : "#21cc6b"
    readonly property color statusColor: state && state.stateColor ? state.stateColor : "#9ea8b3"
    Layout.minimumWidth: small ? 190 : 380
    Layout.minimumHeight: small ? 190 : large ? 380 : 190
    Layout.preferredWidth: small ? 190 : 380
    Layout.preferredHeight: small ? 190 : large ? 380 : 190
    Connection { id: connection }
    component Line: Text {
        color: "#f5f5f7"
        font.pixelSize: 13
        textFormat: Text.PlainText
        elide: Text.ElideRight
        Layout.fillWidth: true
    }
    fullRepresentation: Rectangle {
        radius: 22
        color: "#ed22252b"
        border.color: "#40565b63"
        clip: true
        MouseArea {
            anchors.fill: parent
            acceptedButtons: Qt.LeftButton
            preventStealing: true
            cursorShape: Qt.PointingHandCursor
            onClicked: connection.openPrinter(root.state ? root.state.serial : "")
            Accessible.role: Accessible.Button
            Accessible.name: root.state ? root.state.name : "Bambu Bar"
        }
        ColumnLayout {
            anchors.fill: parent
            anchors.margins: 16
            spacing: 8
            RowLayout {
                Line { text: root.state ? root.state.name : "Bambu Bar"; color: "#b9bec6"; font.bold: true }
                Line { visible: !root.small; text: "● " + (root.state ? root.state.caption : i18n("Offline")); color: root.statusColor; Layout.fillWidth: false }
            }
            RowLayout {
                Layout.fillWidth: true
                Layout.fillHeight: true
                spacing: 12
                Canvas {
                    visible: root.small && root.state !== null
                    Layout.preferredWidth: 62
                    Layout.preferredHeight: 62
                    property real progress: root.active ? Math.min(100, Math.max(0, root.state.progress)) : 0
                    property color ringColor: root.tint
                    onProgressChanged: requestPaint()
                    onRingColorChanged: requestPaint()
                    onVisibleChanged: requestPaint()
                    onPaint: {
                        const ctx = getContext("2d"); ctx.reset(); ctx.lineWidth = 6;
                        ctx.strokeStyle = "#50535b"; ctx.beginPath(); ctx.arc(31,31,26,0,2*Math.PI); ctx.stroke();
                        if (progress > 0) { ctx.strokeStyle = ringColor; ctx.beginPath(); ctx.arc(31,31,26,-Math.PI/2,-Math.PI/2+2*Math.PI*progress/100); ctx.stroke(); }
                    }
                    Text { anchors.centerIn: parent; color: "white"; font.bold: true; font.pixelSize: 16; text: root.active ? root.state.progress + "%" : root.state && root.state.state === "finished" ? "✓" : "●" }
                }
                Image {
                    visible: !root.small && root.state !== null
                    Layout.preferredWidth: 98
                    Layout.preferredHeight: 116
                    source: root.state && root.state.art ? "file://" + root.state.art : ""
                    fillMode: Image.PreserveAspectFit
                    asynchronous: true
                }
                ColumnLayout {
                    Layout.fillWidth: true
                    spacing: 5
                    Line { visible: root.small; text: root.state ? root.state.caption.toUpperCase() : i18n("Offline"); color: root.statusColor; font.bold: true; font.pixelSize: 11 }
                    Line { visible: !root.small || root.state === null; text: root.state ? root.state.headline : connection.connected ? i18n("Enable desktop widgets in Bambu Bar") : i18n("Open Bambu Bar to connect"); font.bold: true; wrapMode: Text.WordWrap; elide: Text.ElideNone }
                    Line { visible: root.active && !root.small; text: root.state ? root.state.progress + "%" : ""; font.pixelSize: 30; font.bold: true }
                    Rectangle {
                        visible: root.active && !root.small
                        Layout.fillWidth: true; height: 5; radius: 2; color: "#50535b"
                        Rectangle { width: parent.width * Math.min(100, Math.max(0, root.state ? root.state.progress : 0)) / 100; height: 5; radius: 2; color: root.tint }
                    }
                    Line { text: root.state ? root.state.stateLine === (root.state.issue || {}).text && root.large ? "" : root.state.stateLine : ""; wrapMode: Text.WordWrap; maximumLineCount: 2; font.bold: root.small }
                    Line { visible: !root.small; text: root.state && root.state.detail !== root.state.stateLine ? root.state.detail : ""; color: "#b9bec6"; font.pixelSize: 11 }
                }
            }
            Line { visible: root.small && root.state !== null && !!root.state.activeSpoolLine; text: root.state ? root.state.activeSpoolLine || "" : ""; font.pixelSize: 10; color: "#b9bec6" }
            Line { visible: root.small && root.state !== null; text: root.state ? root.state.headline : ""; font.bold: true }
            Line { visible: root.small && root.state !== null; text: root.state && root.state.detail !== root.state.stateLine ? root.state.detail : ""; font.pixelSize: 10; color: "#b9bec6" }
            RowLayout {
                visible: !root.small && root.state !== null
                spacing: 6
                Repeater {
                    model: root.state ? root.state.temps : []
                    Rectangle {
                        required property var modelData
                        Layout.fillWidth: true; Layout.preferredHeight: root.large ? 48 : 29
                        radius: 9; color: "#33363d"
                        Column { anchors.centerIn: parent; spacing: 2
                            Text { anchors.horizontalCenter: parent.horizontalCenter; visible: root.large; text: modelData.label; color: "#b9bec6"; font.pixelSize: 10 }
                            Text { anchors.horizontalCenter: parent.horizontalCenter; text: modelData.value + (modelData.target ? " → " + modelData.target : ""); color: modelData.color; font.pixelSize: 12; font.bold: true }
                        }
                    }
                }
            }
            RowLayout {
                visible: root.large && root.state !== null && root.state.spools.length > 0
                Line { text: root.state && root.state.ams ? root.state.ams.title : "AMS"; font.pixelSize: 11; color: "#b9bec6" }
                Line { text: root.state && root.state.ams ? root.state.ams.humidity || "" : ""; horizontalAlignment: Text.AlignRight; font.pixelSize: 11; color: "#b9bec6" }
            }
            RowLayout {
                visible: root.large && root.state !== null
                spacing: 8
                Repeater {
                    model: root.state ? root.state.spools : []
                    ColumnLayout {
                        required property var modelData
                        Layout.fillWidth: true
                        spacing: 3
                        Rectangle {
                            Layout.alignment: Qt.AlignHCenter
                            width: 44; height: 44; radius: 22; color: "transparent"
                            border.width: 6; border.color: modelData.empty ? "#50535b" : modelData.color
                            Text { anchors.centerIn: parent; text: modelData.amount || modelData.label; color: "white"; font.pixelSize: 9 }
                        }
                        Text { Layout.alignment: Qt.AlignHCenter; text: modelData.name; color: modelData.active ? "white" : "#b9bec6"; font.pixelSize: 10; font.bold: modelData.active }
                    }
                }
            }
            Line {
                visible: root.large && root.state !== null
                text: root.state ? root.state.issue ? "⚠ " + root.state.issue.text : root.state.lastJob ? root.state.lastJob.name + " · " + root.state.lastJob.ago : root.state.info || "" : ""
                wrapMode: Text.WordWrap; maximumLineCount: 2; color: root.state && root.state.issue ? "#ffb83d" : "#b9bec6"; font.pixelSize: 11
            }
        }
    }
}
