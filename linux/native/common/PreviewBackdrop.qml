import QtQuick

Rectangle {
    id: backdrop
    required property QtObject kit
    radius: Math.max(8, kit.radius)
    clip: true
    gradient: Gradient {
        orientation: Gradient.Horizontal
        GradientStop { position: 0; color: Qt.darker(backdrop.kit.accent, 1.5) }
        GradientStop { position: 1; color: Qt.darker(backdrop.kit.card, 1.6) }
    }
    Image {
        anchors.fill: parent
        source: backdrop.kit.wallpaper
        sourceSize.width: 960
        asynchronous: true
        fillMode: Image.PreserveAspectCrop
    }
    Rectangle { anchors.fill: parent; color: "#380a1320" }
}
