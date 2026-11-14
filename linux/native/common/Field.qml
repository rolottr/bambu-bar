import QtQuick
import QtQuick.Layouts

Loader {
    id: root
    required property var field
    required property var controller
    required property QtObject kit
    sourceComponent: !kit ? null : field.kind === "navigation" ? kit.navigationControl : field.kind === "toggle" ? kit.toggleControl : field.kind === "choice" ? kit.choiceControl : field.kind === "input" || field.kind === "secret" ? kit.inputControl : kit.buttonControl
    onLoaded: {
        item.field = Qt.binding(() => root.field)
        item.controller = root.controller
    }
}
