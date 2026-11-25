import QtQuick
import QtQuick.Layouts

Loader {
    id: root
    required property var field
    required property var controller
    required property QtObject kit
    readonly property var actionIcons: ({
        refresh:"view-refresh", light:"brightness-high", camera:"camera-web", pause:"media-playback-pause",
        resume:"media-playback-start", stop:"media-playback-stop", panel:"window", show:"window",
        license:"object-unlocked", "add-widget":"list-add", settings:"settings-configure", quit:"application-exit", website:"globe", releases:"help-contents",
        "manage-purchase":"user-identity", "report-issue":"tools-report-bug", "copy-version":"edit-copy",
        "deactivate-license":"list-remove", "activate-license":"object-unlocked", "navigate:About":"bookmarks", support:"love",
        "support-menu":"love", "support-window":"love", "support-panel":"love",
        "activity-clear":"edit-clear-history", "export-activity":"document-save"
    })
    readonly property var pageIcons: ({
        "All Printers":"view-app-grid", Printer:"printer", Filament:"media-optical", "Menu Bar":"application-menu",
        Notifications:"notifications", Widgets:"dashboard-show",
        Settings:"settings-configure", Activity:"view-history", About:"help-about"
    })
    readonly property string iconName: field?.icon || (field?.kind === "navigation" ? pageIcons[field?.pageId] : actionIcons[field?.key]) || ""
    readonly property bool pulseHeart: field?.key === "support-panel" || field?.key === "support-window"
    Loader {
        active: root.pulseHeart
        anchors.left: parent.left
        anchors.leftMargin: 1
        anchors.verticalCenter: parent.verticalCenter
        z: 1
        sourceComponent: Component {
            Symbol {
                id: heart
                name: "love"; kit: root.kit; tint: root.kit.accentInk
                SequentialAnimation on scale {
                    running: heart.visible && heart.Window.window?.visible === true && root.kit.animationsEnabled
                    loops: Animation.Infinite
                    onRunningChanged: if (!running) heart.scale = 1
                    PauseAnimation { duration: 2560 }
                    NumberAnimation { from: 1; to: 1.22; duration: 110; easing.type: Easing.InOutSine }
                    NumberAnimation { to: 1; duration: 130; easing.type: Easing.InOutSine }
                    NumberAnimation { to: 1.14; duration: 110; easing.type: Easing.InOutSine }
                    NumberAnimation { to: 1; duration: 290; easing.type: Easing.InOutSine }
                }
            }
        }
    }
    enabled: field?.enabled !== false
    opacity: enabled ? 1 : 0.58
    sourceComponent: {
        if (!kit || !field) return null
        switch (field.kind) {
        case "link": return field.iconOnly ? kit.buttonControl : kit.linkControl
        case "navigation": return kit.navigationControl
        case "toggle": return kit.toggleControl
        case "radio": return radioGroup
        case "choice": return field.key === "widgetCorner" ? cornerPicker : kit.choiceControl
        case "input":
        case "secret": return kit.inputControl
        default: return kit.buttonControl
        }
    }
    Component {
        id: cornerPicker
        RowLayout {
            id: picker
            property var field: ({})
            property var controller
            Text { Layout.fillWidth: true; text: picker.field.label || ""; font: root.kit.nativeFont; color: root.kit.foreground; wrapMode: Text.Wrap }
            GridLayout {
                Layout.fillWidth: false
                Layout.preferredWidth: Math.max(220, implicitWidth)
                columns: 2
                columnSpacing: 4; rowSpacing: 4
                Repeater {
                    model: picker.field.options || []
                    Loader {
                        required property var modelData
                        Layout.fillWidth: true
                        sourceComponent: root.kit.buttonControl
                        onLoaded: {
                            item.field = Qt.binding(() => ({kind:"button", label:modelData.label, selected:picker.field.value === modelData.value, icon:modelData.value}))
                            item.controller = {activate: () => picker.controller.activate(picker.field, modelData.value)}
                        }
                    }
                }
            }
        }
    }
    Component {
        id: radioGroup
        ColumnLayout {
            property var field: ({})
            property var controller
            spacing: 2
            Text { Layout.fillWidth: true; text: root.field.label; font: root.kit.nativeFont; color: root.kit.foreground; wrapMode: Text.Wrap }
            Repeater {
                model: root.field.options || []
                Loader {
                    required property var modelData
                    sourceComponent: root.kit.radioControl
                    onLoaded: {
                        item.field = Qt.binding(() => ({label:modelData.label, selected:modelData.value === root.field.value, kind:"radioOption"}))
                        item.controller = {activate: () => root.controller.activate(root.field, modelData.value)}
                    }
                }
            }
        }
    }
    onLoaded: {
        item.field = Qt.binding(() => root.iconName ? Object.assign({}, root.field, {icon:root.iconName, pulseHeart:root.pulseHeart}) : root.field)
        item.controller = Qt.binding(() => root.controller)
    }
}
