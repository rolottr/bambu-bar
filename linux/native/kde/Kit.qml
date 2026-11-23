import QtQuick
import QtQuick.Controls as Controls
import QtQuick.Layouts
import org.kde.kirigami as Kirigami

QtObject {
    id: kit
    readonly property QtObject widgetPalette: Item {
        Kirigami.Theme.inherit: false
        Kirigami.Theme.colorSet: Kirigami.Theme.Complementary
        // Some hosts expose only the Window color set. Use a complete pair in that case.
        readonly property real bgLuminance: kit.luminance(Kirigami.Theme.backgroundColor) + .05
        readonly property real fgLuminance: kit.luminance(Kirigami.Theme.textColor) + .05
        readonly property bool hasContrast: Math.max(bgLuminance, fgLuminance) / Math.min(bgLuminance, fgLuminance) >= 4.5
        readonly property color surface: hasContrast ? Kirigami.Theme.backgroundColor : "#252a31"
        readonly property color ink: hasContrast ? Kirigami.Theme.textColor : "#f4f6f8"
    }
    readonly property bool panelAtTop: false
    readonly property url wallpaper: typeof backend !== "undefined" && backend ? backend.previewWallpaper : ""
    // These two meanings differ from Breeze's disc-burning and crash-report symbols.
    function drawing(name, ink) {
        const shapes = {
            "widget-warning": '<path d="M7.1 2.1a1 1 0 0 1 1.8 0l5.6 10.8a.8.8 0 0 1-.7 1.2H2.2a.8.8 0 0 1-.7-1.2Z" fill="none" stroke="currentColor" stroke-width="1.4"/><path d="M8 5v4m0 2v1" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/>',
            "widget-disconnected": '<path d="m2 2 12 12M2 6a9 9 0 0 1 2-1m3-1a10 10 0 0 1 7 2M4 9a6 6 0 0 1 4-1m-2 4a3 3 0 0 1 4 0" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/>',
            "widget-finished": '<g fill="none" stroke="currentColor" stroke-width="1.5"><circle cx="8" cy="8" r="6"/><path d="m4.5 8 2.5 2.5L11.5 6"/></g>',
            "widget-stopped": '<g fill="none" stroke="currentColor" stroke-width="1.5"><circle cx="8" cy="8" r="6"/><path d="m5.5 5.5 5 5m0-5-5 5"/></g>',
            "application-exit": '<path d="M7 2H2v12h5M10 5l3 3-3 3M6 8h7" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/>',
            door: '<path d="M3 14h10M4 14V2h8v12M9 8h1" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/>',
            chamber: '<path d="m2 4 6-2 6 2v8l-6 2-6-2zM2 4l6 2 6-2M8 6v8" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"/>',
            fan: '<circle cx="8" cy="8" r="1.4"/><path d="M7 6c-2-4 1-6 4-4 1 2-1 2-3 4Zm3 1c4-2 6 1 4 4-2 1-2-1-4-3Zm-1 3c2 4-1 6-4 4-1-2 1-2 3-4Zm-3-1c-4 2-6-1-4-4 2-1 2 1 4 3Z"/>',
            "heat-waves": '<path fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" d="M4.5 2c-3 4 3 8 0 12M8 2c-3 4 3 8 0 12M11.5 2c-3 4 3 8 0 12"/>',
            fire: '<path d="M8 .8C8.6 3.2 12.5 5.3 12.5 9.6c0 3.3-2.1 5.6-4.5 5.6s-4.5-2.3-4.5-5.3c0-2 1.1-3.4 2-4.5.2 1.5.8 2.5 1.7 2.9C6.9 5.6 7.3 2.8 8 .8Z"/>',
            "tools-report-bug": '<g fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"><rect x="4.5" y="5" width="7" height="9" rx="3.5"/><path d="M6 5V4a2 2 0 0 1 4 0v1M5.5 2l1 1m4-1-1 1M2 6l2.5 1M1.5 9h3M2 13l2.7-1.8M14 6l-2.5 1M14.5 9h-3M14 13l-2.7-1.8M8 7v4"/></g>'
        }
        if (!shapes[name]) return ""
        return "data:image/svg+xml," + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 16 16" color="' + ink + '" fill="' + ink + '">' + shapes[name] + '</svg>')
    }
    function iconName(name) {
        if (!name) return ""
        const aliases = {"printer-network":"network-server", "help-contents":"view-list-text", "preferences-desktop-widgets":"dashboard-show", configure:"settings-configure", "application-exit":"system-log-out", night:"weather-clear-night", progress:"speedometer", lightbulb:"brightness-high", "draw-donut":"media-optical"}
        return (aliases[name] || name) + "-symbolic"
    }
    // WCAG relative luminance is linear-light, not HSL lightness.
    function luminance(color) {
        function linear(v) { return v <= 0.04045 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4) }
        return 0.2126 * linear(color.r) + 0.7152 * linear(color.g) + 0.0722 * linear(color.b)
    }
    readonly property color onAccent: luminance(accent) > 0.179 ? "black" : "white"
    readonly property color accentInk: Qt.tint(accent, Qt.rgba(foreground.r, foreground.g, foreground.b, 0.68))
    component SymbolItem: Item {
        property string symbolName: ""
        property color tint: kit.foreground
        readonly property string svg: kit.drawing(symbolName, tint)
        Kirigami.Icon {
            anchors.fill: parent
            visible: !parent.svg
            source: visible ? kit.iconName(parent.symbolName) : ""
            isMask: true
            color: parent.tint
        }
        Image {
            anchors.fill: parent
            visible: !!parent.svg
            source: parent.svg
            sourceSize: Qt.size(32, 32)
        }
    }
    property Component symbolControl: Component { SymbolItem {} }
    property Component linkControl: Component {
        Kirigami.LinkButton {
            id: link
            property var field: ({})
            property var controller
            text: field.label || ""
            leftPadding: field.icon ? 22 : 0
            rightPadding: 2; topPadding: 4; bottomPadding: 4
            horizontalAlignment: Text.AlignLeft
            normalColor: kit.accentInk
            Controls.ToolTip.visible: mouseArea.containsMouse && !!field.tooltip
            Controls.ToolTip.text: field.tooltip || ""
            SymbolItem {
                anchors.left: parent.left; anchors.verticalCenter: parent.verticalCenter
                width: 16; height: 16
                symbolName: link.field.icon || ""
                tint: link.color
                visible: !!symbolName && !link.field.pulseHeart
            }
            onClicked: controller.activate(field)
        }
    }
    readonly property bool animationsEnabled: Kirigami.Units.longDuration > 0
    readonly property color foreground: Kirigami.Theme.textColor
    readonly property color muted: Qt.tint(background, Qt.rgba(foreground.r, foreground.g, foreground.b, 0.75))
    readonly property color background: Kirigami.Theme.backgroundColor
    readonly property color card: Kirigami.Theme.alternateBackgroundColor
    readonly property color border: Qt.rgba(foreground.r, foreground.g, foreground.b, 0.22)
    readonly property color positive: Kirigami.Theme.positiveTextColor
    readonly property color warning: Kirigami.Theme.neutralTextColor
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
            icon.name: kit.iconName(field.icon)
            icon.width: 18; icon.height: 18
            Kirigami.Theme.inherit: false
            Kirigami.Theme.highlightedTextColor: kit.onAccent
            highlighted: !!field.selected
            palette.highlightedText: kit.onAccent
            onClicked: controller.activate(field)
        }
    }
    property Component buttonControl: Component {
        Controls.Button {
            flat: field.kind === "link" || !!field.iconOnly
            display: field.iconOnly ? Controls.AbstractButton.IconOnly : Controls.AbstractButton.TextBesideIcon
            icon.name: kit.iconName(field.icon)
            icon.color: ["deactivate-license", "forget"].includes(field.key) ? kit.negative : field.iconColor || (field.kind === "link" ? kit.accentInk : kit.foreground)
            Accessible.name: field.label || ""
            Controls.ToolTip.visible: hovered && !!(field.iconOnly || field.tooltip)
            Controls.ToolTip.text: field.tooltip || field.label || ""
            Kirigami.Theme.inherit: false
            Kirigami.Theme.highlightedTextColor: kit.onAccent
            palette.buttonText: ["deactivate-license", "forget"].includes(field.key) ? kit.negative : kit.foreground
            Kirigami.Theme.textColor: ["deactivate-license", "forget"].includes(field.key) ? kit.negative : kit.foreground
            icon.width: 16; icon.height: 16
            checkable: field.selected !== undefined
            checked: !!field.selected
            property var field: ({})
            property var controller
            text: field.label || ""
            enabled: field.enabled !== false
            highlighted: !!field.selected
            onClicked: controller.activate(field)
        }
    }
    property Component radioControl: Component {
        Controls.RadioButton {
            property var field: ({})
            property var controller
            text: field.label || ""
            checked: !!field.selected
            onClicked: controller.activate(field)
        }
    }
    property Component toggleControl: Component {
        RowLayout {
            property var field: ({})
            property var controller
            Controls.Label { Layout.fillWidth: true; text: field.label || ""; wrapMode: Text.Wrap }
            Controls.Switch {
                id: toggle
                Accessible.name: field.label || ""
                checked: field.value === true
                onToggled: controller.activate(field, checked)
                indicator: Rectangle {
                    implicitWidth: 36; implicitHeight: 18; radius: height / 2
                    x: (toggle.width - width) / 2; y: (toggle.height - height) / 2
                    color: toggle.checked ? kit.accent : kit.background
                    border.color: toggle.activeFocus ? kit.accentInk : kit.muted
                    Rectangle {
                        width: parent.height - 6; height: width; radius: height / 2
                        y: 3; x: 3 + (parent.width - width - 6) * toggle.visualPosition
                        color: toggle.checked ? kit.onAccent : kit.muted
                        Behavior on x { enabled: kit.animationsEnabled; NumberAnimation { duration: 100 } }
                    }
                }
            }
        }
    }
    property Component choiceControl: Component {
        GridLayout {
            id: choiceRow
            property var field: ({})
            property var controller
            readonly property bool printerChoice: field.key === "menuBarPrinter"
            readonly property bool stacked: printerChoice && width < labelMetrics.advanceWidth + columnSpacing + 142
            columns: stacked ? 1 : 2
            rowSpacing: 4
            TextMetrics { id: labelMetrics; font: choiceLabel.font; text: choiceLabel.text }
            Controls.Label {
                id: choiceLabel
                visible: !!field.label; text: field.label || ""
                Layout.fillWidth: true
                wrapMode: Text.Wrap
            }
            Controls.ComboBox {
                id: combo
                displayText: field.icon ? "" : currentText
                // Breeze can paint the selected text in the background. Keep the icon
                // and label together while the native control owns the arrow and menu.
                RowLayout {
                    visible: !!field.icon
                    anchors.fill: parent
                    anchors.leftMargin: combo.mirrored ? 24 : 8
                    anchors.rightMargin: combo.mirrored ? 8 : 24
                    layoutDirection: combo.mirrored ? Qt.RightToLeft : Qt.LeftToRight
                    spacing: 6
                    SymbolItem {
                        Layout.preferredWidth: 16; Layout.preferredHeight: 16
                        symbolName: field.icon || ""; tint: combo.palette.buttonText
                    }
                    Controls.Label {
                        Layout.fillWidth: true; Layout.minimumWidth: 0
                        text: combo.currentText; color: combo.palette.buttonText; font: combo.font
                        elide: Text.ElideRight; textFormat: Text.PlainText
                    }
                }
                Accessible.description: field.icon ? currentText : ""
                Accessible.name: field.accessibleLabel || field.label || ""
                Layout.preferredHeight: field.controlHeight || implicitHeight
                Layout.fillWidth: !field.label || choiceRow.stacked
                Layout.preferredWidth: choiceRow.printerChoice && !choiceRow.stacked
                    ? Math.min(200, choiceRow.width - Math.ceil(labelMetrics.advanceWidth) - choiceRow.columnSpacing)
                    : Math.min(200, choiceRow.width * 0.50)
                model: field.options || []
                textRole: "label"; valueRole: "value"
                currentIndex: Math.max(0, (field.options || []).findIndex(o => o.value === String(field.value ?? "")))
                onActivated: controller.activate(field, currentValue)
            }
        }
    }
    property Component inputControl: Component {
        RowLayout {
            property var field: ({})
            property var controller
            Controls.Label { text: field.label || ""; Layout.fillWidth: field.key !== "licenseKey"; wrapMode: Text.Wrap }
            Controls.TextField {
                Layout.fillWidth: field.key === "licenseKey"
                Layout.preferredWidth: Math.min(220, parent.width * 0.58)
                text: controller && controller.drafts[field.key] !== undefined ? controller.drafts[field.key] : field.value || ""
                echoMode: field.kind === "secret" ? TextInput.Password : TextInput.Normal
                maximumLength: field.key === "accessCode" ? 8 : 256
                onTextEdited: controller.setDraft(field.key, text)
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
