import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

Control {
    id: root
    required property QtObject kit
    font: kit.nativeFont
    padding: 0
    background: Rectangle { color: root.kit.background }
    property string page: "Printer"
    property var pages: []
    property var sections: []
    function isMachineGroup(section) { return page === "Printer" && !!section.fields[0]?.icon }
    readonly property var machineGroups: sections.filter(isMachineGroup)
    readonly property var formSections: sections.filter(section => !isMachineGroup(section) && !(page === "Printer" && section.layout === "history"))
    property var desktop: ({})
    property var drafts: ({})
    property string subtitle: ""
    property bool supporter: false
    property string error: ""
    property bool connected: false
    property bool confirmStop: true
    property string cameraImage: ""
    property string cameraStatus: "Connecting…"
    property bool cameraRetry: false
    property string confirmation: ""
    property string confirmationLabel: ""
    property string capturePath: ""
    signal request(var message)
    signal showRequested()
    signal cameraRequested()
    signal cameraClosed()
    signal importRequested()
    signal quitRequested()
    signal hideRequested()
    signal appearanceRequested(int mode, var palette)
    function t(key) { return (desktop.labels || {})[key] || key }
    function setDraft(key, value) { drafts = Object.assign({}, drafts, {[key]:value}) }
    function send(action, value) {
        const fields = Object.assign({}, drafts)
        if (action !== "save-address") delete fields.replacementCode
        request({action: action, value: value === undefined ? "" : value, fields: fields})
    }
    function endAddressEdit() {
        const next = Object.assign({}, drafts)
        delete next.editingConnection; delete next.existingHost; delete next.replacementCode
        drafts = next
    }
    function activate(field, value) {
        if (field.key === "edit-address") { setDraft("editingConnection", true); return }
        if (field.key === "cancel-address") { endAddressEdit(); return }
        if (field.key === "save-address" && drafts.existingHost === undefined) {
            const address = sections.flatMap(section => section.fields).find(f => f.key === "existingHost")
            setDraft("existingHost", address?.value || "")
        }
        if (field.key === "revert-name") { setDraft("name", field.value); return }
        if (field.key === "import-file") { importRequested(); return }
        if (field.key === "camera") { cameraRequested(); return }
        if (field.key === "quit") { send("quit"); quitRequested(); return }
        if (field.note === "confirmation" && (field.key !== "stop" || confirmStop)) { confirmation = field.key; confirmationLabel = field.label; return }
        if (field.key === "speed") { send("speed:" + value); return }
        if (field.key === "widgetSize" || field.key === "widgetCorner" || field.key === "widgetPrinter") { drafts = Object.assign({}, drafts, {[field.key]:value}); return }
        if (field.key === "select" && value === "add-printer") { send("navigate:Setup"); return }
        if (field.key === "select") {
            drafts = {}
            if (page === "Setup") { send("select", value); send("navigate:Printer"); return }
        }
        send(field.key, value)
        if (field.key === "connect" || field.key === "activate-license") { drafts = Object.assign({}, drafts, {accessCode:"", licenseKey:""}) }
    }
    function accept(raw) {
        let message
        try { message = typeof raw === "string" ? JSON.parse(raw) : raw } catch (e) { return }
        if (message.type === "quit") { quitRequested(); return }
        if (message.type === "hide") { hideRequested(); return }
        if (message.type === "clipboard") { clipboard.text = message.text; clipboard.selectAll(); clipboard.copy(); clipboard.text = ""; return }
        if (message.type === "capture") {
            capturePath = message.path
            showRequested()
            capture.attempts = 0
            capture.restart()
            return
        }
        if (message.type === "prefill") { drafts = Object.assign({}, drafts, message.fields); return }
        if (message.type === "connection-saved" && message.serial === desktop.printer?.id) { endAddressEdit(); return }
        if (message.type === "error") { error = message.message; return }
        if (message.type === "camera") { cameraImage = message.image; return }
        if (message.type === "camera-status") { cameraStatus = message.message; cameraRetry = !!message.retry; return }
        if (message.type === "close-camera") { cameraClosed(); return }
        if (message.type === "show-camera") { cameraRequested(); return }
        if (message.type === "show") { showRequested(); send("navigate:" + message.page); return }
        if (message.type === "disconnected") { connected = false; cameraImage = ""; return }
        if (message.type !== "state") return
        connected = true
        confirmStop = message.confirmStop !== false
        appearanceRequested(message.appearance === undefined ? 2 : message.appearance, message.palette)
        subtitle = message.subtitle || ""
        supporter = !!message.supporter
        if (page !== message.page) { page = message.page; scroll.contentItem.contentY = 0; drafts = {} }
        if (desktop.printer?.id !== message.desktop?.printer?.id) drafts = {}
        desktop = message.desktop || {}
        if (JSON.stringify(pages) !== JSON.stringify(message.pages)) pages = message.pages || []
        if (JSON.stringify(sections) !== JSON.stringify(message.sections)) sections = message.sections || []
        error = message.error || ""
    }
    // Clipboard ownership belongs to the focused native window on Wayland.
    TextEdit { id: clipboard; visible: false }
    Timer {
        id: capture
        interval: 100
        property int attempts: 0
        onTriggered: {
            if (!root.grabToImage(result => root.send("capture-result", result.saveToFile(root.capturePath)))) {
                if (++attempts < 20) restart()
                else root.send("capture-result", false)
            }
        }
    }
    RowLayout {
        anchors.fill: parent; spacing: 0
        Rectangle {
            Layout.preferredWidth: root.width < 800 ? 174 : 208
            Layout.fillHeight: true
            color: root.kit.card
            ColumnLayout {
                anchors.fill: parent; anchors.margins: 12; spacing: 8
                Field { Layout.fillWidth: true; visible: (root.desktop.printers || []).length > 1; field: ({key: "select", kind: "choice", label: "", value: root.desktop.printer ? root.desktop.printer.id : "", options: (root.desktop.printers || []).map(p => ({value:p.id,label:p.name})).concat([{value:"add-printer",label:root.t("Add Printer…")}])}); controller: root; kit: root.kit }
                Label { visible: (root.desktop.printers || []).length > 1; Layout.fillWidth: true; text: root.desktop.printer ? root.desktop.printer.state : "Connecting…"; color: root.kit.muted; elide: Text.ElideRight; textFormat: Text.PlainText }
                ScrollView {
                    id: navigationScroll
                    Layout.fillWidth: true; Layout.fillHeight: true; clip: true
                    ColumnLayout {
                        width: navigationScroll.availableWidth; spacing: 4
                        Repeater {
                            model: root.pages
                            Field { required property var modelData; Layout.fillWidth: true; field: ({kind:"navigation", pageId:modelData.id, key:"navigate:"+modelData.id, label:modelData.title, selected:(root.page === "Setup" ? "Printer" : root.page) === modelData.id}); kit: root.kit; controller: root }
                        }
                    }
                }
                Label { visible: (root.desktop.printers || []).length < 2; Layout.fillWidth: true; text: root.desktop.printer ? root.desktop.printer.name : "Bambu Bar"; color: root.kit.foreground; font.bold: true; elide: Text.ElideRight; textFormat: Text.PlainText }
                Label { visible: (root.desktop.printers || []).length < 2; Layout.fillWidth: true; text: root.desktop.printer ? root.desktop.printer.state : "Connecting…"; color: root.kit.muted; elide: Text.ElideRight; textFormat: Text.PlainText }
            }
        }
        ColumnLayout {
            Layout.fillWidth: true; Layout.fillHeight: true; spacing: 0
            ScrollView {
                id: scroll
                ScrollBar.vertical.policy: ScrollBar.AlwaysOn
                Layout.fillWidth: true; Layout.fillHeight: true; clip: true
                contentWidth: availableWidth
                ColumnLayout {
                    width: scroll.availableWidth
                    spacing: 18
                    Item { height: 3 }
                    RowLayout {
                        Layout.fillWidth: true; Layout.leftMargin: 22; Layout.rightMargin: 22
                        Image {
                            visible: root.page === "About"
                            Layout.preferredWidth: 96; Layout.preferredHeight: 96; Layout.rightMargin: 12
                            source: visible ? Qt.resolvedUrl("AppIcon.png") : ""; fillMode: Image.PreserveAspectFit
                        }
                        ColumnLayout {
                            Layout.fillWidth: true
                            Label {
                                Layout.fillWidth: true
                                text: root.page === "Setup" || (root.page === "Printer" && root.connected && !root.desktop.printer) ? root.t("Add Printer…") : root.page === "About" ? "Bambu Bar" : root.page === "Printer" && root.desktop.printer ? root.desktop.printer.name : root.pages.find(p => p.id === root.page)?.title || root.page
                                elide: Text.ElideRight; color: root.kit.foreground; font.pixelSize: 28; font.bold: true; textFormat: Text.PlainText
                            }
                            Label { visible: root.page === "About"; text: "NINA LABS, LLC"; color: root.kit.muted }
                            Label { visible: root.page === "About"; text: root.t("Version") + " " + (root.sections[0]?.fields[0]?.value || ""); color: root.kit.muted }
                            Label { visible: root.page === "About"; text: root.t("Your Bambu Lab printer, one click away in the tray."); color: root.kit.muted; Layout.fillWidth: true; wrapMode: Text.Wrap }
                        }
                        Field {
                            visible: root.page === "All Printers" || (!!root.desktop.printer && (root.page === "Setup" || (root.page === "Printer" && (root.desktop.printers || []).length < 2)))
                            kit: root.kit
                            field: ({kind:"button", key:root.page === "Setup" ? "navigate:Printer" : "navigate:Setup", icon:root.page === "Setup" ? "" : "list-add", label:root.t(root.page === "Setup" ? "Cancel" : "Add Printer…")})
                            controller: root
                        }
                        Field { visible: root.page === "Printer" && !!root.desktop.printer; kit: root.kit; controller: root; field: ({kind:"button", key:"refresh", label:root.t("Refresh")}) }
                        Field { Layout.alignment: Qt.AlignTop | Qt.AlignRight; kit: root.kit; controller: root; field: ({kind:"link", key:root.supporter ? "navigate:About" : "support-window", icon:root.supporter ? "starred" : "love", label:root.t(root.supporter ? "Hero Supporter" : "Support this project")}) }
                    }
                    Label {
                        Layout.fillWidth: true; Layout.leftMargin: 22; Layout.rightMargin: 22
                        visible: !!root.subtitle; text: root.subtitle; color: root.kit.muted; wrapMode: Text.Wrap; textFormat: Text.PlainText
                    }
                    Loader {
                        Layout.fillWidth: true; Layout.leftMargin: 22; Layout.rightMargin: 22
                        active: root.page === "Widgets"; visible: active
                        sourceComponent: WidgetShowcase { kit: root.kit; controller: root }
                    }
                    Label { Layout.fillWidth: true; Layout.leftMargin: 22; text: "Connecting to printer service…"; visible: !root.connected; color: root.kit.muted }
                    PrinterCard { Layout.fillWidth: true; Layout.leftMargin: 22; Layout.rightMargin: 22; visible: (root.page === "Printer" || root.page === "Filament") && !!root.desktop.printer; kit: root.kit; controller: root; printer: root.desktop.printer || {}; filamentOnly: root.page === "Filament"; viewport: scroll }
                    GridLayout {
                        id: fleetGrid
                        Layout.fillWidth: true; Layout.leftMargin: 22; Layout.rightMargin: 22
                        visible: root.page === "All Printers"
                        readonly property real available: Math.max(1, scroll.availableWidth - 44)
                        columns: Math.max(1, Math.floor((available + 14) / 294))
                        columnSpacing: 14; rowSpacing: 14
                        Repeater {
                            model: fleetGrid.visible ? (root.desktop.fleet || []).length : 0
                            ItemDelegate {
                                required property int index
                                readonly property var modelData: (root.desktop.fleet || [])[index] || {}
                                Layout.fillWidth: true
                                Layout.preferredWidth: (fleetGrid.available - 14 * (fleetGrid.columns - 1)) / fleetGrid.columns
                                Layout.fillHeight: true
                                padding: 12
                                Accessible.name: modelData.name + " · " + modelData.caption
                                onClicked: { root.activate({key:"select"}, modelData.serial); root.send("navigate:Printer") }
                                background: Rectangle { radius: root.kit.radius; color: root.kit.card; border.color: parent.hovered || parent.activeFocus ? root.kit.accent : root.kit.border }
                                contentItem: ColumnLayout {
                                    RowLayout {
                                        Image { Layout.preferredWidth: 64; Layout.preferredHeight: 88; source: modelData.art ? "file://" + modelData.art : ""; fillMode: Image.PreserveAspectFit }
                                        ColumnLayout {
                                            Layout.fillWidth: true
                                            Label { Layout.fillWidth: true; text: modelData.name || ""; font.bold: true; color: root.kit.foreground; elide: Text.ElideRight; textFormat: Text.PlainText }
                                            Label { Layout.fillWidth: true; text: modelData.headline && modelData.headline !== modelData.caption ? modelData.caption + " · " + modelData.headline : modelData.caption; color: root.kit.foreground; wrapMode: Text.Wrap; maximumLineCount: 2; elide: Text.ElideRight; textFormat: Text.PlainText }
                                            Progress { kit: root.kit; Layout.fillWidth: true; value: modelData.progress || 0 }
                                            Label { Layout.fillWidth: true; text: modelData.stateLine || ""; color: root.kit.muted; wrapMode: Text.Wrap; maximumLineCount: 2; elide: Text.ElideRight; textFormat: Text.PlainText }
                                        }
                                    }
                                    Rectangle { Layout.fillWidth: true; implicitHeight: 1; color: root.kit.border }
                                    Label {
                                        Layout.fillWidth: true; color: root.kit.muted; elide: Text.ElideMiddle; textFormat: Text.PlainText
                                        text: (root.desktop.printers || []).find(p => p.id === modelData.serial)?.addressLine || ""
                                    }
                                }
                            }
                        }
                    }
                    Loader {
                        Layout.fillWidth: true; Layout.leftMargin: 22; Layout.rightMargin: 22
                        active: root.machineGroups.length > 0; visible: active
                        sourceComponent: MachineDetails { kit: root.kit; groups: root.machineGroups }
                    }
                    GridLayout {
                        id: settingsGrid
                        Layout.fillWidth: true; Layout.leftMargin: 22; Layout.rightMargin: 22
                        columns: root.page === "Menu Bar" && width >= 690 ? 2 : 1
                        columnSpacing: 18
                        rowSpacing: 20
                        GridLayout {
                            Layout.fillWidth: true
                            Layout.minimumWidth: 0
                            Layout.alignment: Qt.AlignTop
                            columns: root.page === "About" && scroll.availableWidth >= 650 ? 2 : 1
                            columnSpacing: 24; rowSpacing: 22
                            Repeater {
                                model: root.formSections.length
                                SettingsSection {
                                    required property int index
                                    Layout.fillHeight: root.page === "About" && index < 2
                                    Layout.columnSpan: index < 2 ? 1 : parent.columns
                                    Layout.alignment: Qt.AlignTop | Qt.AlignLeft
                                    modelData: root.formSections[index] || {title:"",fields:[]}
                                    controller: root
                                    kit: root.kit
                                }
                            }
                        }
                        Item {
                            id: previewFrame
                            visible: root.page === "Menu Bar"
                            Layout.alignment: Qt.AlignTop | Qt.AlignHCenter
                            Layout.preferredWidth: Math.min(440, settingsGrid.columns === 2 ? settingsGrid.width - 280 - settingsGrid.columnSpacing : settingsGrid.width)
                            implicitHeight: previewLoader.implicitHeight * previewLoader.scale
                            Loader {
                                id: previewLoader
                                active: previewFrame.visible
                                width: parent.width
                                // Fit the read-only sample in a short side-by-side window.
                                // Below this scale, keep the text legible and let the page scroll.
                                scale: settingsGrid.columns === 2 && implicitHeight > 0
                                    ? Math.max(0.85, Math.min(1, (scroll.availableHeight - settingsGrid.y - 8) / implicitHeight)) : 1
                                transformOrigin: Item.Top
                                sourceComponent: MenuPreview { kit: root.kit; controller: root }
                            }
                        }
                    }
                    Item { height: 12 }
                }
            }
            Label { Layout.fillWidth: true; Layout.margins: 14; visible: root.error !== ""; text: root.error; color: root.kit.foreground; wrapMode: Text.Wrap; textFormat: Text.PlainText }
        }
    }
    Loader {
        anchors.fill: parent; active: root.confirmation !== ""; sourceComponent: root.kit.confirmControl
        onLoaded: { item.controller = root; item.message = root.confirmationLabel + "?" }
    }
    Shortcut { sequence: "Ctrl+Q"; onActivated: root.activate({key: "quit"}) }
    Shortcut { sequence: "Ctrl+R"; onActivated: root.send("refresh") }
    Shortcut { sequence: "Ctrl+,"; onActivated: root.send("navigate:Settings") }
}
