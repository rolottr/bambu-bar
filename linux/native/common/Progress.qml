import QtQuick

Loader {
    id: root
    required property QtObject kit
    property real value: 0
    sourceComponent: kit.progressControl
    onLoaded: item.value = Qt.binding(() => root.value)
}
