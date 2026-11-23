import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

ColumnLayout {
    id: root
    required property QtObject kit
    required property var groups
    spacing: 8

    Label {
        text: root.groups[0]?.title || ""
        font.bold: true; font.pixelSize: 16; color: root.kit.foreground
    }
    GridLayout {
        Layout.fillWidth: true
        columns: root.width >= 590 ? 2 : 1
        columnSpacing: 10; rowSpacing: 10
        Repeater {
            model: root.groups.length
            Pane {
                id: card
                required property int index
                readonly property var fields: root.groups[index]?.fields || []
                Layout.fillWidth: true; Layout.preferredWidth: 1; Layout.minimumWidth: 0
                Layout.fillHeight: true
                padding: 13
                background: Rectangle { color: Qt.tint(root.kit.background, Qt.rgba(root.kit.foreground.r, root.kit.foreground.g, root.kit.foreground.b, 0.065)); radius: Math.max(6, root.kit.radius) }
                contentItem: Item {
                    implicitHeight: rows.implicitHeight
                    ColumnLayout {
                        id: rows
                        width: parent.width
                        spacing: 9
                        Repeater {
                            model: card.fields.length
                            RowLayout {
                                required property int index
                                readonly property var field: card.fields[index] || {}
                                Layout.fillWidth: true; spacing: 12
                                Symbol { kit: root.kit; name: field.icon || "" }
                                Label {
                                    text: field.label || ""; color: root.kit.muted
                                    Layout.maximumWidth: 128
                                    wrapMode: Text.Wrap; textFormat: Text.PlainText
                                }
                                Label {
                                    Layout.fillWidth: true; Layout.minimumWidth: 0
                                    horizontalAlignment: Text.AlignRight
                                    text: field.value || "—"; color: root.kit.foreground; font.weight: Font.DemiBold
                                    wrapMode: Text.Wrap; textFormat: Text.PlainText
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
