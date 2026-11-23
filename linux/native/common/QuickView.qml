import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

Item {
    id: root
    required property QtObject kit
    required property var controller
    required property var snapshot
    property string highlight: ""
    readonly property int contentInset: 6
    implicitHeight: body.implicitHeight + 2 * contentInset
    implicitWidth: body.implicitWidth + 2 * contentInset
    function t(key) { return controller.t(key) }
    ColumnLayout {
        id: body
        anchors.top: parent.top; anchors.left: parent.left; anchors.right: parent.right
        anchors.margins: root.contentInset
        spacing: 8
        RowLayout {
            id: header
            Layout.fillWidth: true
            Label {
                Layout.fillWidth: true; Layout.minimumWidth: 0
                text: root.snapshot.title || "Bambu Bar"; font.bold: true; color: root.kit.foreground
                elide: Text.ElideRight; textFormat: Text.PlainText
            }
            Field {
                kit: root.kit; controller: root.controller
                field: ({kind:"link", iconOnly:true, key:"refresh", icon:"view-refresh", label:root.t("Refresh")})
            }
            Field {
                visible: !!root.snapshot.printer?.camera
                kit: root.kit; controller: root.controller
                field: ({kind:"link", iconOnly:true, key:"camera", icon:"camera-web", label:root.t("Camera")})
            }
        }
        Field {
            Layout.fillWidth: true; visible: (root.snapshot.printers || []).length > 1
            kit: root.kit; controller: root.controller
            field: ({kind:"choice", key:"select", label:root.t("Printer"), value:root.snapshot.printer?.id || "",
                     options:(root.snapshot.printers || []).map(p => ({value:p.id,label:p.name}))})
        }
        PrinterCard {
            id: card
            Layout.fillWidth: true; kit: root.kit; controller: root.controller
            printer: root.snapshot.printer || {}; compact: true
        }
        RowLayout {
            Layout.fillWidth: true
            visible: !root.snapshot.supporter
            Field { kit: root.kit; controller: root.controller; field: ({kind:"link", key:"support-panel", label:root.t("Support this project")}) }
            Item { Layout.fillWidth: true }
            Field { kit: root.kit; controller: root.controller; field: ({kind:"link", key:"license", label:root.t("Manage License…")}) }
        }
        Rectangle { Layout.fillWidth: true; implicitHeight: 1; color: root.kit.border }
        RowLayout {
            Layout.fillWidth: true; spacing: 14
            Field { kit: root.kit; controller: root.controller; field: ({kind:"link", key:"show", label:root.t("Open Bambu Bar")}) }
            Item { Layout.fillWidth: true }
            Field { kit: root.kit; controller: root.controller; field: ({kind:"link", key:"settings", label:root.t("Settings")}) }
            Field { kit: root.kit; controller: root.controller; field: ({kind:"link", key:"quit", label:root.t("Quit")}) }
        }
    }
    Rectangle {
        id: outline
        readonly property Item target: !root.highlight || root.highlight === "icon" ? null : root.highlight === "header" ? header : card.region(root.highlight) || body
        property rect bounds
        function updateBounds() {
            bounds = target ? target.mapToItem(root, 0, 0, target.width, target.height) : Qt.rect(0,0,0,0)
        }
        onTargetChanged: if (target) Qt.callLater(updateBounds)
        // Settings can add or remove rows after the asynchronous backend reply.
        FrameAnimation { running: !!outline.target && root.visible; onTriggered: outline.updateBounds() }
        x: bounds.x - 3; y: bounds.y - 3; width: bounds.width + 6; height: bounds.height + 6
        radius: root.kit.radius + 2; color: "transparent"; border.color: root.kit.accent; border.width: 2
        opacity: target ? 1 : 0
        Behavior on opacity { enabled: root.kit.animationsEnabled; NumberAnimation { duration: 150 } }
    }
}
