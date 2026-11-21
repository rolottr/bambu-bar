import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

Item {
    id: sectionDelegate
    required property var modelData
    required property var controller
    required property QtObject kit
    Layout.fillWidth: true
    readonly property bool printerSettings: controller.page === "Printer" && modelData.fields.some(f => f.key === "name")
    readonly property bool grouped: ["About", "Widgets", "Menu Bar", "Printer"].includes(controller.page)
    readonly property real inset: grouped ? 14 : 0
    implicitHeight: heading.implicitHeight + 8 + section.implicitHeight + 2 * inset
    visible: modelData.fields.length > 0
    RowLayout {
        id: heading
        anchors.left: parent.left; anchors.right: parent.right
        spacing: 7
        Symbol { visible: sectionDelegate.printerSettings; kit: sectionDelegate.kit; name: "settings-configure" }
        Label {
            Layout.fillWidth: true
            text: sectionDelegate.modelData.title; font.pixelSize: 16; font.bold: true; color: sectionDelegate.kit.foreground
            elide: Text.ElideRight; textFormat: Text.PlainText
        }
    }
    Rectangle {
        anchors.top: heading.bottom; anchors.topMargin: 8; anchors.bottom: parent.bottom
        width: parent.width; visible: sectionDelegate.grouped
        color: sectionDelegate.printerSettings ? Qt.tint(sectionDelegate.kit.card, Qt.rgba(sectionDelegate.kit.foreground.r, sectionDelegate.kit.foreground.g, sectionDelegate.kit.foreground.b, 0.055)) : sectionDelegate.kit.card
        radius: Math.max(4, sectionDelegate.kit.radius)
    }
    ColumnLayout {
        id: section
        anchors.left: parent.left; anchors.right: parent.right; anchors.top: heading.bottom
        anchors.leftMargin: sectionDelegate.inset; anchors.rightMargin: sectionDelegate.inset
        anchors.topMargin: 8 + sectionDelegate.inset
        spacing: 8
        Repeater {
            model: sectionDelegate.visible ? sectionDelegate.modelData.fields.length : 0
            ColumnLayout {
                required property int index
                readonly property var modelData: sectionDelegate.modelData.fields[index] || {}
                readonly property bool editedName: modelData.key === "name" && sectionDelegate.controller.drafts.name !== undefined && sectionDelegate.controller.drafts.name !== modelData.value
                Layout.fillWidth: true
                readonly property bool editingConnection: !!sectionDelegate.controller.drafts.editingConnection
                visible: !["rename", "activate-license"].includes(modelData.key)
                         && (modelData.key !== "printerAddress" || !editingConnection)
                         && (!["existingHost", "replacementCode", "save-address"].includes(modelData.key) || editingConnection)
                Rectangle { visible: (modelData.key === "menuBarStyle" && index > 0) || ["ignoredCodes","forget"].includes(modelData.key); Layout.fillWidth: true; implicitHeight: 1; color: sectionDelegate.kit.border }
                RowLayout {
                    visible: ["step","placed","license-heading"].includes(modelData.kind)
                    Layout.fillWidth: true
                    spacing: 10
                    Label { visible: modelData.kind === "step"; text: (index + 1) + "."; color: sectionDelegate.kit.muted }
                    Symbol { kit: sectionDelegate.kit; name: modelData.kind === "step" ? modelData.key : modelData.kind === "placed" ? "printer" : "love" }
                    Label { Layout.fillWidth: true; wrapMode: Text.Wrap; color: sectionDelegate.kit.foreground; text: modelData.kind === "license-heading" ? modelData.label : String(modelData.value || ""); font.bold: modelData.kind === "license-heading" }
                    Field { visible: modelData.kind === "placed"; active: visible; field: modelData; kit: sectionDelegate.kit; controller: sectionDelegate.controller }
                }
                RowLayout {
                    visible: modelData.kind === "license-links"
                    Layout.fillWidth: true
                    Field { active: parent.visible; kit: sectionDelegate.kit; controller: sectionDelegate.controller; field: ({kind:"link", key:"support", label:modelData.label}) }
                    Item { Layout.fillWidth: true }
                    Field { active: parent.visible; kit: sectionDelegate.kit; controller: sectionDelegate.controller; field: ({kind:"link", key:"manage-purchase", label:String(modelData.value || "")}) }
                }
                RowLayout {
                    visible: modelData.kind === "license-busy" || modelData.kind === "license-error"
                    Layout.fillWidth: true; spacing: 8
                    BusyIndicator { visible: modelData.kind === "license-busy"; running: visible; Layout.preferredWidth: 20; Layout.preferredHeight: 20 }
                    Symbol { visible: modelData.kind === "license-error"; kit: sectionDelegate.kit; name: "dialog-warning"; tint: sectionDelegate.kit.negative }
                    Label { Layout.fillWidth: true; wrapMode: Text.Wrap; text: String(modelData.value || ""); color: modelData.kind === "license-error" ? sectionDelegate.kit.negative : sectionDelegate.kit.foreground; textFormat: Text.PlainText }
                }
                Label {
                    visible: modelData.kind === "paragraph"
                    Layout.fillWidth: true; text: String(modelData.value || "").replace(/\*\*/g, ""); wrapMode: Text.Wrap; color: sectionDelegate.kit.muted; textFormat: Text.PlainText
                }
                RowLayout {
                    Layout.fillWidth: true
                    visible: modelData.kind === "text"
                    Label { Layout.fillWidth: true; text: modelData.label; font: sectionDelegate.kit.nativeFont; color: sectionDelegate.printerSettings ? sectionDelegate.kit.foreground : sectionDelegate.kit.muted; wrapMode: Text.Wrap; textFormat: Text.PlainText }
                    Rectangle {
                        visible: modelData.key === "printerStatus"
                        implicitWidth: 7; implicitHeight: 7; radius: 4
                        color: sectionDelegate.controller.desktop.printer?.online ? sectionDelegate.kit.positive : sectionDelegate.kit.warning
                    }
                    Label { Layout.maximumWidth: section.width * 0.68; horizontalAlignment: Text.AlignRight; font: sectionDelegate.kit.nativeFont; text: String(modelData.value ?? "—"); color: sectionDelegate.kit.foreground; wrapMode: Text.Wrap; textFormat: Text.PlainText }
                    Field {
                        visible: modelData.key === "printerAddress"
                        active: visible; kit: sectionDelegate.kit; controller: sectionDelegate.controller
                        field: ({kind:"button", key:"edit-address", label:sectionDelegate.controller.t("Change…")})
                    }
                }
                RowLayout {
                    Layout.fillWidth: true
                    visible: !["text","paragraph","step","placed","license-heading","license-links","license-error","license-busy"].includes(modelData.kind)
                    Label { visible: modelData.key === "copy-version"; Layout.fillWidth: true; text: sectionDelegate.controller.t("For bug reports"); color: sectionDelegate.kit.muted }
                    Label {
                        visible: modelData.key === "forget"; Layout.fillWidth: true
                        text: String(modelData.value || ""); color: sectionDelegate.kit.muted; wrapMode: Text.Wrap; textFormat: Text.PlainText
                    }
                    Item { visible: ["add-widget", "test-notification"].includes(modelData.key); Layout.fillWidth: true }
                    Field {
                        active: visible
                        Layout.fillWidth: modelData.kind !== "button"
                        field: ["website","releases","manage-purchase","report-issue","support"].includes(modelData.key) ? Object.assign({}, modelData, {kind:"link"}) : modelData.kind === "choice" && sectionDelegate.controller.drafts[modelData.key] !== undefined ? Object.assign({}, modelData, {value:sectionDelegate.controller.drafts[modelData.key]}) : modelData; kit: sectionDelegate.kit; controller: sectionDelegate.controller
                    }
                    Field {
                        visible: editedName || modelData.key === "save-address"
                        active: visible
                        kit: sectionDelegate.kit; controller: sectionDelegate.controller
                        field: modelData.key === "save-address" ? {kind:"button", key:"cancel-address", label:sectionDelegate.controller.t("Cancel")} : ({kind:"button", key:"revert-name", label:sectionDelegate.controller.t("Revert"), value:modelData.value})
                    }
                    Field {
                        readonly property var nextField: sectionDelegate.modelData.fields[index + 1]
                        visible: (editedName || modelData.key === "licenseKey") && !!nextField
                        enabled: nextField?.enabled !== false && (!editedName || !!sectionDelegate.controller.drafts.name.trim()) && (modelData.key !== "licenseKey" || !!sectionDelegate.controller.drafts.licenseKey?.trim())
                        active: visible
                        field: nextField || {}; kit: sectionDelegate.kit; controller: sectionDelegate.controller
                    }
                }
            }
        }
    }
}
