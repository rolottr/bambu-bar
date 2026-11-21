import QtQuick

Item {
    id: symbol
    required property QtObject kit
    required property string name
    property color tint: kit.muted
    implicitWidth: 16
    implicitHeight: 16
    Loader {
        anchors.fill: parent
        sourceComponent: symbol.kit.symbolControl
        onLoaded: {
            item.symbolName = Qt.binding(() => symbol.name)
            item.tint = Qt.binding(() => symbol.tint)
        }
    }
}
