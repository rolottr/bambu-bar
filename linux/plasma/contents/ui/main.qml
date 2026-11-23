import QtQuick
import QtQuick.Layouts
import QtQuick.Window
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
    toolTipMainText: connection.snapshot.title || "Bambu Bar"
    toolTipSubText: ""
    readonly property int cardSize: Math.max(0, Math.min(2, Plasmoid.configuration.size))
    readonly property bool small: cardSize === 0
    readonly property bool large: cardSize === 2
    readonly property var states: connection.snapshot.widgetPrinters || []
    readonly property var widgetSnapshot: (states.find(p => p.id === Plasmoid.configuration.printer) || {}).state || null
    readonly property string panelLabel: Plasmoid.formFactor === PlasmaCore.Types.Horizontal ? connection.snapshot.panelText || "" : ""
    TextMetrics { id: labelMetrics; text: root.panelLabel; font: Kirigami.Theme.defaultFont }
    readonly property int panelWidth: 24 + (panelLabel ? Math.ceil(labelMetrics.advanceWidth) + Kirigami.Units.smallSpacing : 0)
    Layout.minimumWidth: inPanel ? panelWidth : small ? 190 : 380
    Layout.minimumHeight: inPanel ? 24 : small ? 190 : large ? 380 : 190
    Layout.preferredWidth: inPanel ? panelWidth : small ? 190 : 380
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
        implicitWidth: root.panelWidth
        implicitHeight: 24
        Kirigami.Icon {
            id: trayIcon
            anchors.left: parent.left; anchors.verticalCenter: parent.verticalCenter
            width: Math.min(parent.width, parent.height, 24); height: width
            source: Qt.resolvedUrl("tray-icons/dev.rolottr.BambuBar-tray-" + (connection.snapshot.panelIcon || "offline") + "-symbolic.svg")
            color: Kirigami.Theme.textColor
            isMask: true
        }
        Controls.Label {
            anchors.left: trayIcon.right; anchors.leftMargin: Kirigami.Units.smallSpacing
            anchors.verticalCenter: parent.verticalCenter
            text: root.panelLabel
        }
        MouseArea {
            anchors.fill: parent
            acceptedButtons: Qt.LeftButton | Qt.MiddleButton | Qt.RightButton
            onClicked: mouse => { if (mouse.button === Qt.MiddleButton) connection.openPrinter(""); else if (mouse.button === Qt.RightButton) contextMenu.popup(); else root.expanded = !root.expanded }
        }
        Controls.Menu {
            id: contextMenu
            // Let KWin place the menu outside the narrow panel window.
            popupType: Controls.Popup.Window
            Controls.MenuItem { text: i18n("Quick view"); onTriggered: root.expanded = true }
            Controls.MenuItem { text: i18n("Open Bambu Bar"); onTriggered: connection.openPrinter("") }
            Controls.MenuItem { text: i18n("Filament"); onTriggered: connection.invoke("filament") }
            Controls.MenuItem { text: i18n("Widgets"); onTriggered: connection.invoke("widgets") }
            Controls.MenuItem { text: i18n("Settings"); onTriggered: connection.invoke("settings") }
            Controls.MenuSeparator {}
            Controls.MenuItem { icon.name: "love-symbolic"; text: i18n("Support this project"); onTriggered: connection.invoke("support-menu") }
            Controls.MenuItem { icon.name: "object-unlocked-symbolic"; text: i18n("Manage License…"); onTriggered: connection.invoke("license") }
            Controls.MenuItem { text: i18n("Quit"); onTriggered: connection.invoke("quit") }
        }
    }
    Component {
        id: panelComponent
        Controls.ScrollView {
            id: popup
            clip: true
            contentWidth: availableWidth
            background: Rectangle { color: nativeKit.background; radius: nativeKit.radius }
            readonly property int naturalHeight: Math.ceil(details.implicitHeight + topPadding + bottomPadding)
            padding: Kirigami.Units.smallSpacing * 2
            rightPadding: padding + effectiveScrollBarWidth
            property string confirmation: ""
            property string confirmationLabel: ""
            function t(key) { return (connection.snapshot.labels || {})[key] || key }
            function activate(field, value) {
                if (field.note === "confirmation") { confirmation = field.key; confirmationLabel = field.label; confirm.open(); return }
                if (field.key === "select") { connection.invoke("select", value); return }
                connection.invoke(field.key === "speed" ? "speed:" + value : field.key)
                if (["show", "settings", "license", "support-panel", "camera"].indexOf(field.key) >= 0) root.expanded = false
            }
            Common.QuickView {
                id: details
                width: popup.availableWidth; height: implicitHeight
                kit: nativeKit; controller: popup; snapshot: connection.snapshot
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

    fullRepresentation: Loader {
        // Plasma restores its last popup size, even when the printer now has more AMS or controls.
        // Keep one full quick view visible when the current output has room.
        readonly property real fittedPanelHeight: Math.min(item?.naturalHeight || 500, Screen.height * 0.85)
        Layout.minimumWidth: root.inPanel ? 420 : root.small ? 190 : 380
        Layout.minimumHeight: root.inPanel ? fittedPanelHeight : root.small ? 190 : root.large ? 380 : 190
        Layout.maximumHeight: root.inPanel ? fittedPanelHeight : Infinity
        Layout.preferredWidth: 420
        Layout.preferredHeight: root.inPanel ? fittedPanelHeight : root.small ? 190 : root.large ? 380 : 190
        sourceComponent: root.inPanel ? panelComponent : widgetComponent
    }
    Component {
        id: widgetComponent
        Common.WidgetCard {
            kit: nativeKit
            snapshot: root.widgetSnapshot
            size: root.small ? "small" : root.large ? "large" : "medium"
            connected: connection.connected
            translate: key => i18n(key)
            MouseArea {
                anchors.fill: parent
                cursorShape: Qt.PointingHandCursor
                onClicked: connection.openPrinter(root.widgetSnapshot?.serial || "")
                Accessible.role: Accessible.Button
                Accessible.name: root.widgetSnapshot?.name || "Bambu Bar"
            }
        }
    }
}
