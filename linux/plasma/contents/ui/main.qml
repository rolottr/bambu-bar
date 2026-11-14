import QtQuick
import QtQuick.Layouts
import QtQuick.Controls as Controls
import org.kde.kirigami as Kirigami
import org.kde.plasma.workspace.dbus as DBus
import "common" as Common
import org.kde.plasma.plasmoid
import org.kde.plasma.core as PlasmaCore

PlasmoidItem {
    id: root
    readonly property bool inPanel: Plasmoid.formFactor !== PlasmaCore.Types.Planar
    preferredRepresentation: inPanel ? compactRepresentation : fullRepresentation
    Plasmoid.backgroundHints: PlasmaCore.Types.NoBackground
    readonly property int cardSize: Math.max(0, Math.min(2, Plasmoid.configuration.size))
    readonly property bool small: cardSize === 0
    readonly property bool large: cardSize === 2
    readonly property var states: connection.snapshot.widgetPrinters || []
    readonly property var state: (states.find(p => p.id === Plasmoid.configuration.printer) || {}).state || null
    readonly property bool active: state && state.live && ["printing", "preparing", "paused"].includes(state.state)
    readonly property color tint: state && /^#[0-9a-f]{6}$/i.test(state.tint) ? state.tint : "#21cc6b"
    readonly property color statusColor: state && state.stateColor ? state.stateColor : "#9ea8b3"
    Layout.minimumWidth: inPanel ? 24 : small ? 190 : 380
    Layout.minimumHeight: inPanel ? 24 : small ? 190 : large ? 380 : 190
    Layout.preferredWidth: inPanel ? 24 : small ? 190 : 380
    Layout.preferredHeight: inPanel ? 24 : small ? 190 : large ? 380 : 190
    Connection { id: connection }

    NativeKit { id: nativeKit }
    function panelPresence(present) {
        if (root.inPanel) DBus.SessionBus.asyncCall({service:"dev.rolottr.BambuBar.Native",path:"/dev/rolottr/BambuBar/Native",iface:"dev.rolottr.BambuBar.Native",member:"SetPanelPresent",arguments:[present]}, () => {}, () => {})
    }
    Component.onCompleted: panelPresence(true)
    Component.onDestruction: panelPresence(false)
    DBus.DBusServiceWatcher {
        busType: DBus.BusType.Session
        watchedService: "dev.rolottr.BambuBar.Native"
        onRegisteredChanged: { if (registered) root.panelPresence(true) }
    }
    onExpandedChanged: function(expanded) { if (root.inPanel) connection.setOpen(expanded) }
    DBus.SignalWatcher {
        busType: DBus.BusType.Session
        service: "dev.rolottr.BambuBar.Native"
        path: "/dev/rolottr/BambuBar/Native"
        iface: "dev.rolottr.BambuBar.Native"
        function dbusTogglePanel() { if (root.inPanel) root.expanded = !root.expanded }
    }
    compactRepresentation: Item {
        Kirigami.Icon {
            anchors.fill: parent
            source: Qt.resolvedUrl("tray-icons/dev.rolottr.BambuBar-tray-" + (connection.snapshot.panelIcon || "offline") + "-symbolic.svg")
            color: Kirigami.Theme.textColor
            isMask: true
        }
        MouseArea {
            anchors.fill: parent
            acceptedButtons: Qt.LeftButton | Qt.MiddleButton | Qt.RightButton
            onClicked: mouse => { if (mouse.button === Qt.MiddleButton) connection.openPrinter(""); else if (mouse.button === Qt.RightButton) contextMenu.popup(); else root.expanded = !root.expanded }
        }
        Controls.ToolTip.visible: hover.hovered
        Controls.ToolTip.text: connection.snapshot.title || "Bambu Bar"
        HoverHandler { id: hover }
        Controls.Menu {
            id: contextMenu
            Controls.MenuItem { text: i18n("Quick view"); onTriggered: root.expanded = true }
            Controls.MenuItem { text: i18n("Open Bambu Bar"); onTriggered: connection.openPrinter("") }
            Controls.MenuItem { text: i18n("Quit"); onTriggered: connection.invoke("quit") }
        }
    }
    Component {
        id: panelComponent
        Controls.ScrollView {
            id: popup
            clip: true
            contentWidth: availableWidth
            property string confirmation: ""
            property string confirmationLabel: ""
            function t(key) { return (connection.snapshot.labels || {})[key] || key }
            function activate(field, value) {
                if (field.note === "confirmation") { confirmation = field.key; confirmationLabel = field.label; confirm.open(); return }
                connection.invoke(field.key === "speed" ? "speed:" + value : field.key)
                if (["show", "settings", "camera"].indexOf(field.key) >= 0) root.expanded = false
            }
            ColumnLayout {
                width: popup.availableWidth
                spacing: Kirigami.Units.largeSpacing
                Controls.Label { Layout.fillWidth: true; text: connection.snapshot.title || "Bambu Bar"; font.bold: true; wrapMode: Text.Wrap; textFormat: Text.PlainText }
                Common.PrinterCard { Layout.fillWidth: true; kit: nativeKit; controller: popup; printer: connection.snapshot.printer || {}; compact: true }
                Controls.Button { text: i18n("Open Bambu Bar"); onClicked: { connection.openPrinter(""); root.expanded = false } }
                Controls.Button { text: i18n("Settings"); onClicked: { connection.invoke("settings"); root.expanded = false } }
            }
            Controls.Dialog {
                id: confirm
                anchors.centerIn: parent
                title: popup.confirmationLabel + "?"
                modal: true
                standardButtons: Controls.Dialog.Ok | Controls.Dialog.Cancel
                onAccepted: connection.invoke(popup.confirmation === "stop" ? "stop-confirmed" : popup.confirmation)
            }
        }
    }

    component Line: Text {
        color: Kirigami.Theme.textColor
        font.pixelSize: 13
        textFormat: Text.PlainText
        elide: Text.ElideRight
        Layout.fillWidth: true
    }
    fullRepresentation: Loader {
        Layout.minimumWidth: root.inPanel ? 420 : root.small ? 190 : 380
        Layout.minimumHeight: root.inPanel ? 500 : root.small ? 190 : root.large ? 380 : 190
        Layout.preferredWidth: 420
        Layout.preferredHeight: root.inPanel ? 700 : root.small ? 190 : root.large ? 380 : 190
        sourceComponent: root.inPanel ? panelComponent : widgetComponent
    }
    Component {
      id: widgetComponent
      Rectangle {
        radius: 22
        color: Kirigami.Theme.backgroundColor
        border.color: Qt.rgba(Kirigami.Theme.textColor.r, Kirigami.Theme.textColor.g, Kirigami.Theme.textColor.b, 0.2)
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
                Line { text: root.state ? root.state.name : "Bambu Bar"; color: Kirigami.Theme.disabledTextColor; font.bold: true }
                Line { visible: !root.small; text: "● " + (root.state ? root.state.caption : i18n("Offline")); color: root.statusColor; Layout.fillWidth: false }
            }
            RowLayout {
                Layout.fillWidth: true
                Layout.fillHeight: true
                Layout.minimumHeight: 0
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
                        ctx.strokeStyle = Kirigami.Theme.disabledTextColor; ctx.beginPath(); ctx.arc(31,31,26,0,2*Math.PI); ctx.stroke();
                        if (progress > 0) { ctx.strokeStyle = ringColor; ctx.beginPath(); ctx.arc(31,31,26,-Math.PI/2,-Math.PI/2+2*Math.PI*progress/100); ctx.stroke(); }
                    }
                    Text { anchors.centerIn: parent; color: Kirigami.Theme.textColor; font.bold: true; font.pixelSize: 16; text: root.active ? root.state.progress + "%" : root.state && root.state.state === "finished" ? "✓" : "●" }
                }
                Image {
                    visible: !root.small && root.state !== null
                    Layout.preferredWidth: 98
                    Layout.preferredHeight: root.large ? 116 : 88
                    source: root.state && root.state.art ? "file://" + root.state.art : ""
                    fillMode: Image.PreserveAspectFit
                    asynchronous: true
                }
                ColumnLayout {
                    Layout.fillWidth: true
                    spacing: root.large || root.small ? 5 : 3
                    Line { visible: root.small; text: root.state ? root.state.caption.toUpperCase() : i18n("Offline"); color: root.statusColor; font.bold: true; font.pixelSize: 11 }
                    Line { visible: !root.small || root.state === null; text: root.state ? root.state.headline : connection.connected ? i18n("Enable desktop widgets in Bambu Bar") : i18n("Open Bambu Bar to connect"); font.bold: true; wrapMode: Text.WordWrap; maximumLineCount: root.large || root.small ? 2 : 1 }
                    Line { visible: root.active && !root.small; text: root.state ? root.state.progress + "%" : ""; font.pixelSize: root.large ? 30 : 26; font.bold: true }
                    Rectangle {
                        visible: root.active && !root.small
                        Layout.fillWidth: true; height: 5; radius: 2; color: Kirigami.Theme.disabledTextColor
                        Rectangle { width: parent.width * Math.min(100, Math.max(0, root.state ? root.state.progress : 0)) / 100; height: 5; radius: 2; color: root.tint }
                    }
                    Line { text: root.state ? root.state.stateLine === (root.state.issue || {}).text && root.large ? "" : root.state.stateLine : ""; wrapMode: Text.WordWrap; maximumLineCount: root.large || root.small ? 2 : 1; font.bold: root.small }
                    Line { visible: !root.small; text: root.state && root.state.detail !== root.state.stateLine ? root.state.detail : ""; color: Kirigami.Theme.disabledTextColor; font.pixelSize: 11 }
                }
            }
            Line { visible: root.small && root.state !== null && !!root.state.activeSpoolLine; text: root.state ? root.state.activeSpoolLine || "" : ""; font.pixelSize: 10; color: Kirigami.Theme.disabledTextColor }
            Line { visible: root.small && root.state !== null; text: root.state ? root.state.headline : ""; font.bold: true }
            Line { visible: root.small && root.state !== null; text: root.state && root.state.detail !== root.state.stateLine ? root.state.detail : ""; font.pixelSize: 10; color: Kirigami.Theme.disabledTextColor }
            RowLayout {
                visible: !root.small && root.state !== null
                spacing: 6
                Repeater {
                    model: root.state ? root.state.temps : []
                    Rectangle {
                        required property var modelData
                        Layout.fillWidth: true; Layout.preferredHeight: root.large ? 48 : 29
                        radius: 9; color: Kirigami.Theme.alternateBackgroundColor
                        Column { anchors.centerIn: parent; spacing: 2
                            Text { anchors.horizontalCenter: parent.horizontalCenter; visible: root.large; text: modelData.label; color: Kirigami.Theme.disabledTextColor; font.pixelSize: 10 }
                            Text { anchors.horizontalCenter: parent.horizontalCenter; text: modelData.value + (modelData.target ? " → " + modelData.target : ""); color: modelData.color; font.pixelSize: 12; font.bold: true }
                        }
                    }
                }
            }
            RowLayout {
                visible: root.large && root.state !== null && root.state.spools.length > 0
                Line { text: root.state && root.state.ams ? root.state.ams.title : "AMS"; font.pixelSize: 11; color: Kirigami.Theme.disabledTextColor }
                Line { text: root.state && root.state.ams ? root.state.ams.humidity || "" : ""; horizontalAlignment: Text.AlignRight; font.pixelSize: 11; color: Kirigami.Theme.disabledTextColor }
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
                            border.width: 6; border.color: modelData.empty ? Kirigami.Theme.disabledTextColor : modelData.color
                            Text { anchors.centerIn: parent; text: modelData.amount || modelData.label; color: Kirigami.Theme.textColor; font.pixelSize: 9 }
                        }
                        Text { Layout.alignment: Qt.AlignHCenter; text: modelData.name; color: modelData.active ? Kirigami.Theme.textColor : Kirigami.Theme.disabledTextColor; font.pixelSize: 10; font.bold: modelData.active }
                    }
                }
            }
            Line {
                visible: root.large && root.state !== null
                text: root.state ? root.state.issue ? "⚠ " + root.state.issue.text : root.state.lastJob ? root.state.lastJob.name + " · " + root.state.lastJob.ago : root.state.info || "" : ""
                wrapMode: Text.WordWrap; maximumLineCount: 2; color: root.state && root.state.issue ? "#ffb83d" : Kirigami.Theme.disabledTextColor; font.pixelSize: 11
            }
        }
    }
    }
}
