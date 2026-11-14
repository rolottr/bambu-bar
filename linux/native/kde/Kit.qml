import QtQuick
import QtQuick.Controls as Controls
import QtQuick.Layouts
import org.kde.kirigami as Kirigami

QtObject {
    readonly property bool animationsEnabled: Kirigami.Units.longDuration > 0
    readonly property color foreground: Kirigami.Theme.textColor
    readonly property color muted: Qt.tint(background, Qt.rgba(foreground.r, foreground.g, foreground.b, 0.75))
    readonly property color background: Kirigami.Theme.backgroundColor
    readonly property color card: Kirigami.Theme.alternateBackgroundColor
    readonly property color border: Qt.rgba(foreground.r, foreground.g, foreground.b, 0.22)
    readonly property color negative: Kirigami.Theme.negativeTextColor
    readonly property color accent: Kirigami.Theme.highlightColor
    readonly property int radius: Kirigami.Units.cornerRadius
    readonly property font nativeFont: Kirigami.Theme.defaultFont
    readonly property string fontFamily: Kirigami.Theme.defaultFont.family
    property Component progressControl: Component { Controls.ProgressBar { from: 0; to: 100 } }
    property Component navigationControl: Component {
        Controls.ItemDelegate {
            property var field: ({})
            property var controller
            text: field.label || ""
            enabled: field.enabled !== false
            highlighted: !!field.selected
            onClicked: controller.activate(field)
        }
    }
    property Component buttonControl: Component {
        Controls.Button {
            property var field: ({})
            property var controller
            text: field.label || ""
            enabled: field.enabled !== false
            highlighted: !!field.selected
            onClicked: controller.activate(field)
        }
    }
    property Component toggleControl: Component {
        Controls.Switch {
            property var field: ({})
            property var controller
            text: field.label || ""
            checked: field.value === true
            onToggled: controller.activate(field, checked)
        }
    }
    property Component choiceControl: Component {
        RowLayout {
            property var field: ({})
            property var controller
            Controls.Label { text: field.label || ""; Layout.fillWidth: true; wrapMode: Text.Wrap }
            Controls.ComboBox {
                Layout.preferredWidth: Math.min(240, parent.width * 0.6)
                model: field.options || []
                textRole: "label"; valueRole: "value"
                currentIndex: Math.max(0, (field.options || []).findIndex(o => o.value === String(field.value)))
                onActivated: controller.activate(field, currentValue)
            }
        }
    }
    property Component inputControl: Component {
        RowLayout {
            property var field: ({})
            property var controller
            Controls.Label { text: field.label || ""; Layout.preferredWidth: 140; wrapMode: Text.Wrap }
            Controls.TextField {
                Layout.fillWidth: true
                text: controller && controller.drafts[field.key] !== undefined ? controller.drafts[field.key] : field.value || ""
                echoMode: field.kind === "secret" ? TextInput.Password : TextInput.Normal
                maximumLength: field.key === "accessCode" ? 8 : 256
                onTextEdited: controller.drafts[field.key] = text
            }
        }
    }
    property Component confirmControl: Component {
        Item {
            property var controller
            property string message: ""
            Controls.Dialog {
                anchors.centerIn: parent
                title: parent.message
                modal: true; visible: true
                standardButtons: Controls.Dialog.Ok | Controls.Dialog.Cancel
                onAccepted: { controller.send(controller.confirmation, true); controller.confirmation = "" }
                onRejected: controller.confirmation = ""
            }
        }
    }
}
