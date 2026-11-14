import QtQuick
import QtQuick.Layouts
import qs.Ui as Ui
import qs.Commons as Commons

QtObject {
    readonly property bool animationsEnabled: !Commons.Style.reduceMotion
    readonly property color foreground: Commons.Color.foreground
    readonly property color muted: Qt.tint(background, Qt.rgba(foreground.r, foreground.g, foreground.b, 0.78))
    readonly property color background: Commons.Color.background
    readonly property color card: Commons.Color.popups.background
    readonly property color border: Commons.Color.popups.border
    readonly property color negative: Commons.Color.urgent
    readonly property color accent: Commons.Color.accent
    readonly property int radius: Commons.Style.cornerRadius
    readonly property font nativeFont: Qt.font({family:Commons.Style.font.family, pixelSize:Commons.Style.font.body})
    readonly property string fontFamily: Commons.Style.font.family
    // Omarchy has no progress-control component. A noninteractive status meter
    // uses its palette and corner token, while interactive controls below are Ui.*.
    property Component progressControl: Component {
        Item {
            property real value: 0
            implicitHeight: 6
            Rectangle { anchors.fill: parent; radius: Commons.Style.cornerRadius; color: Qt.rgba(Commons.Color.foreground.r, Commons.Color.foreground.g, Commons.Color.foreground.b, 0.16) }
            Rectangle { height: parent.height; width: parent.width * Math.max(0, Math.min(100, parent.value)) / 100; radius: Commons.Style.cornerRadius; color: Commons.Color.accent }
        }
    }
    property Component navigationControl: buttonControl
    property Component buttonControl: Component {
        Ui.Button {
            property var field: ({})
            property var controller
            text: field.label || ""
            enabled: field.enabled !== false
            selected: !!field.selected
            bordered: true; focusable: true
            onClicked: controller.activate(field)
        }
    }
    property Component toggleControl: Component {
        Ui.Toggle {
            property var field: ({})
            property var controller
            label: field.label || ""
            checked: field.value === true
            onClicked: controller.activate(field, !checked)
        }
    }
    property Component choiceControl: Component {
        Ui.Dropdown {
            property var field: ({})
            property var controller
            label: field.label || ""
            options: field.options || []
            value: String(field.value || "")
            onChanged: value => controller.activate(field, value)
        }
    }
    property Component inputControl: Component {
        ColumnLayout {
            property var field: ({})
            property var controller
            Text { text: field.label || ""; color: Commons.Color.foreground; font.family: Commons.Style.font.family; textFormat: Text.PlainText }
            Ui.TextField {
                Layout.fillWidth: true
                text: controller && controller.drafts[field.key] !== undefined ? controller.drafts[field.key] : field.value || ""
                password: field.kind === "secret"
                maximumLength: field.key === "accessCode" ? 8 : 256
                onTextEdited: controller.drafts[field.key] = text
            }
        }
    }
    property Component confirmControl: Component {
        Ui.ConfirmDialog {
            property var controller
            opened: true
            selectedIndex: 0
            focus: true
            Component.onCompleted: forceActiveFocus()
            Keys.onPressed: event => { event.accepted = handleKey(event) }
            onConfirmed: { controller.send(controller.confirmation, true); controller.confirmation = "" }
            onCanceled: controller.confirmation = ""
        }
    }
}
