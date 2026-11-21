import QtQuick
import QtQuick.Layouts
import Quickshell
import qs.Ui as Ui
import qs.Commons as Commons

QtObject {
    id: kit
    readonly property QtObject widgetPalette: null
    readonly property bool panelAtTop: true
    readonly property url wallpaper: "file://" + Quickshell.env("HOME") + "/.local/state/omarchy/current/background"
    readonly property bool animationsEnabled: !Commons.Style.reduceMotion
    readonly property color foreground: Commons.Color.foreground
    readonly property color muted: Qt.tint(background, Qt.rgba(foreground.r, foreground.g, foreground.b, 0.78))
    readonly property color background: Commons.Color.background
    readonly property color card: Commons.Color.popups.background
    readonly property color border: Commons.Color.popups.border
    readonly property color positive: "#57bf80"
    readonly property color warning: "#e5aa4c"
    readonly property color negative: Commons.Color.urgent
    readonly property color accent: Commons.Color.accent
    readonly property color accentInk: Qt.tint(accent, Qt.rgba(foreground.r, foreground.g, foreground.b, 0.50))
    readonly property int radius: Commons.Style.cornerRadius
    readonly property font nativeFont: Qt.font({family:Commons.Style.font.family, pixelSize:Commons.Style.font.body})
    readonly property string fontFamily: Commons.Style.font.family
    function glyph(name) {
        return ({
            door:"󰠚", "network-wireless":"󰖩", "media-flash":"󰐢", "drive-removable-media":"󰕓",
            "camera-video":"󰕧", "dialog-memory":"󰍛", "package":"󰆧", chamber:"󰆼", fan:"󰈐",
            "view-app-grid":"󰕰", printer:"󰐫", "printer-network":"󰜇", "media-optical":"󰗮",
            "application-menu":"󰍜", notifications:"󰂚", "dashboard-show":"󰕮", "settings-configure":"󰒓",
            "help-about":"󰋼", "heat-waves":"♨", fire:"󰈸", night:"󰖔", progress:"󰓅", "temperature-normal":"󰔏",
            "media-playback-pause":"󰏤", "dialog-ok":"󰄬", chronometer:"󱎫", clock:"󰥔",
            "help-contents":"󰂽", "view-refresh":"󰑐", "brightness-high":"󰃠", lightbulb:"󰌵",
            "camera-web":"󰄀", "media-playback-start":"󰐊", "media-playback-stop":"󰓛", window:"󰖲",
            "application-exit":"󰍃", globe:"󰖟", "window-close":"󰅖", "user-identity":"󰀄", "tools-report-bug":"󰃤",
            "edit-copy":"󰆏", "object-unlocked":"󰌋", love:"󰋑", starred:"󰓎", bookmarks:"󰓎", "draw-donut":"󰗮",
            "appointment-new":"󰃰", "layer-visible-on":"󰜤", "dialog-warning":"󰀦", "view-history":"󰋚",
            "view-process-system":"󰓅", "power-management":"󰖔", "preferences-desktop-widgets":"󰕮",
            "view-list-text":"󰗚", "view-preview":"󰈈", speedometer:"󰓅", "weather-clear-night":"󰖔",
            "tools-media-optical-burn":"󰈸", "top-left":"↖", "top-right":"↗", "bottom-left":"↙", "bottom-right":"↘",
            "list-add":"󰐕", "list-remove":"󰍴"
        })[name] || ""
    }
    property Component symbolControl: Component {
        Text {
            property string symbolName: ""
            property color tint
            text: kit.glyph(symbolName)
            color: tint
            font.family: kit.fontFamily; font.pixelSize: 16
            horizontalAlignment: Text.AlignHCenter; verticalAlignment: Text.AlignVCenter
        }
    }
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
    property Component linkControl: buttonControl
    property Component navigationControl: buttonControl
    property Component buttonControl: Component {
        Ui.Button {
            property var field: ({})
            property var controller
            text: field.iconOnly ? "" : field.label || ""
            Accessible.name: field.label || ""
            tooltipText: field.tooltip || (field.iconOnly ? field.label || "" : "")
            enabled: field.enabled !== false
            selected: !!field.selected
            foreground: ["deactivate-license", "forget"].includes(field.key) ? kit.negative : field.iconColor || (field.kind === "link" ? kit.accentInk : kit.foreground)
            iconText: field.pulseHeart ? "" : field.kind === "radioOption" ? (field.selected ? "󰐾" : "󰐽") : kit.glyph(field.icon)
            // Reserve the fixed heart slot outside the label so each beat leaves the text still.
            leftPadding: field.pulseHeart ? 22 : horizontalPadding
            rightPadding: field.pulseHeart ? 0 : horizontalPadding
            horizontalPadding: field.pulseHeart ? 11 : field.kind === "link" && !field.iconOnly ? 0 : Commons.Style.spacing.controlPaddingX
            verticalPadding: field.kind === "radioOption" ? 3 : Commons.Style.spacing.controlPaddingY
            leftAlign: !!field.pulseHeart || field.kind === "navigation" || field.kind === "radioOption"
            bordered: field.kind !== "link" && field.kind !== "navigation" && field.kind !== "radioOption"; focusable: true
            onClicked: controller.activate(field)
        }
    }
    property Component radioControl: buttonControl
    property Component toggleControl: Component {
        RowLayout {
            property var field: ({})
            property var controller
            Text { Layout.fillWidth: true; text: field.label || ""; font: kit.nativeFont; color: kit.foreground; wrapMode: Text.Wrap }
            Ui.ToggleSwitch {
                checked: field.value === true
                id: toggle
                trackHeight: 19
                cursorPad: 3
                // The shell draws its state tint and knob over this higher-contrast base.
                Rectangle {
                    anchors.centerIn: parent
                    width: toggle.trackWidth; height: toggle.trackHeight
                    radius: toggle.rounded ? height / 2 : 0
                    z: -1
                    color: toggle.checked ? kit.accent : kit.background
                    border.color: kit.muted
                }
                foreground: checked ? kit.background : kit.foreground
                activeFocusOnTab: true
                hasCursor: activeFocus
                Accessible.role: Accessible.CheckBox
                Accessible.name: field.label || ""
                Accessible.checked: checked
                Keys.onSpacePressed: toggled()
                onToggled: controller.activate(field, !checked)
            }
        }
    }
    property Component choiceControl: Component {
        GridLayout {
            readonly property bool stacked: field.key === "menuBarPrinter" && width < 365
            columns: stacked ? 1 : 2
            rowSpacing: 3
            property var field: ({})
            property var controller
            Text { visible: !!field.label; Layout.fillWidth: true; text: field.label || ""; font: kit.nativeFont; color: kit.foreground; wrapMode: Text.Wrap }
            Ui.Dropdown {
                id: choice
                Accessible.name: field.accessibleLabel || field.label || ""
                rowHeight: field.controlHeight || Commons.Style.spacing.controlHeight
                onPopupOpenChanged: {
                    if (controller && controller.fitDropdown) {
                        const rows = Math.min(options.length, 8)
                        const menuHeight = rows * (popupRowHeight + Commons.Style.spacing.labelGap)
                                         + Commons.Style.spacing.controlPaddingY * 2
                        controller.fitDropdown(choice, popupOpen ? menuHeight : 0)
                    }
                }
                Layout.fillWidth: !field.label || parent.stacked
                Layout.preferredWidth: Math.min(220, parent.width * 0.55)
                options: field.icon ? (field.options || []).map(option => ({
                    value:option.value, label:kit.glyph(field.icon) + "  " + option.label
                })) : field.options || []
                value: String(field.value ?? "")
                onChanged: value => controller.activate(field, value)
            }
        }
    }
    property Component inputControl: Component {
        RowLayout {
            property var field: ({})
            property var controller
            Text { Layout.fillWidth: true; wrapMode: Text.Wrap; text: field.label || ""; color: Commons.Color.foreground; font: kit.nativeFont; textFormat: Text.PlainText }
            Ui.TextField {
                Layout.fillWidth: field.key === "licenseKey"
                Layout.preferredWidth: Math.min(220, parent.width * 0.58)
                text: controller && controller.drafts[field.key] !== undefined ? controller.drafts[field.key] : field.value || ""
                password: field.kind === "secret"
                maximumLength: field.key === "accessCode" ? 8 : 256
                onTextEdited: controller.setDraft(field.key, text)
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
