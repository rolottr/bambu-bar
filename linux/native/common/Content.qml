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
    property var desktop: ({})
    property var drafts: ({})
    property string error: ""
    property bool connected: false
    property bool confirmStop: true
    property string cameraImage: ""
    property string cameraStatus: "Connecting…"
    property bool cameraRetry: false
    property string confirmation: ""
    property string confirmationLabel: ""
    signal request(var message)
    signal showRequested()
    signal cameraRequested()
    signal cameraClosed()
    signal importRequested()
    signal quitRequested()
    signal hideRequested()
    signal appearanceRequested(int mode, var palette)
    function t(key) { return (desktop.labels || {})[key] || key }
    function send(action, value) { request({action: action, value: value === undefined ? "" : value, fields: drafts}) }
    function activate(field, value) {
        if (field.key === "import-file") { importRequested(); return }
        if (field.key === "camera") { cameraRequested(); return }
        if (field.key === "quit") { send("quit"); quitRequested(); return }
        if (field.note === "confirmation" && (field.key !== "stop" || confirmStop)) { confirmation = field.key; confirmationLabel = field.label; return }
        if (field.key === "speed") { send("speed:" + value); return }
        if (field.key === "widgetSize" || field.key === "widgetCorner" || field.key === "widgetPrinter") { drafts[field.key] = value; return }
        if (field.key === "select") drafts = {}
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
            grabToImage(result => send("capture-result", result.saveToFile(message.path)))
            return
        }
        if (message.type === "prefill") { drafts = Object.assign({}, drafts, message.fields); return }
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
        desktop = message.desktop || {}
        if (JSON.stringify(pages) !== JSON.stringify(message.pages)) pages = message.pages || []
        if (JSON.stringify(sections) !== JSON.stringify(message.sections)) sections = message.sections || []
        if (page !== message.page) { page = message.page; scroll.contentItem.contentY = 0; drafts = {} }
        error = message.error || ""
    }
    // Clipboard ownership belongs to the focused native window on Wayland.
    TextEdit { id: clipboard; visible: false }
    RowLayout {
        anchors.fill: parent; spacing: 0
        Rectangle {
            Layout.preferredWidth: root.width < 800 ? 175 : 205
            Layout.fillHeight: true
            color: root.kit.card
            ColumnLayout {
                anchors.fill: parent; anchors.margins: 12; spacing: 8
                Field { Layout.fillWidth: true; visible: (root.desktop.printers || []).length > 1; field: ({key: "select", kind: "choice", label: "Printer", value: root.desktop.printer ? root.desktop.printer.id : "", options: (root.desktop.printers || []).map(p => ({value:p.id,label:p.name}))}); controller: root; kit: root.kit }
                ScrollView {
                    id: navigationScroll
                    Layout.fillWidth: true; Layout.fillHeight: true; clip: true
                    ColumnLayout {
                        width: navigationScroll.availableWidth; spacing: 4
                        Repeater {
                            model: root.pages
                            Field { required property var modelData; Layout.fillWidth: true; field: ({kind:"navigation", key:"navigate:"+modelData.id, label:modelData.title, selected:root.page===modelData.id}); kit: root.kit; controller: root }
                        }
                    }
                }
                Label { Layout.fillWidth: true; text: root.desktop.printer ? root.desktop.printer.name : "Bambu Bar"; color: root.kit.foreground; font.bold: true; elide: Text.ElideRight; textFormat: Text.PlainText }
                Label { Layout.fillWidth: true; text: root.desktop.printer ? root.desktop.printer.state : "Connecting…"; color: root.kit.muted; elide: Text.ElideRight; textFormat: Text.PlainText }
                Field { Layout.fillWidth: true; field: ({kind: "button", key: "quit", label: "Quit Bambu Bar"}); controller: root; kit: root.kit }
            }
        }
        ColumnLayout {
            Layout.fillWidth: true; Layout.fillHeight: true; spacing: 0
            ScrollView {
                id: scroll
                Layout.fillWidth: true; Layout.fillHeight: true; clip: true
                contentWidth: availableWidth
                ColumnLayout {
                    width: scroll.availableWidth
                    spacing: 18
                    Item { height: 3 }
                    Label { Layout.fillWidth: true; Layout.leftMargin: 22; Layout.rightMargin: 22; text: root.pages.find(p => p.id === root.page)?.title || root.page; color: root.kit.foreground; font.family: root.kit.fontFamily; font.pixelSize: 28; font.bold: true; textFormat: Text.PlainText }
                    Label { Layout.fillWidth: true; Layout.leftMargin: 22; text: "Connecting to printer service…"; visible: !root.connected; color: root.kit.muted }
                    PrinterCard { Layout.fillWidth: true; Layout.leftMargin: 22; Layout.rightMargin: 22; visible: (root.page === "Printer" || root.page === "Filament") && !!root.desktop.printer; kit: root.kit; controller: root; printer: root.desktop.printer || {}; filamentOnly: root.page === "Filament"; viewport: scroll }
                    Repeater {
                        model: root.page === "All Printers" ? root.desktop.fleet || [] : []
                        Rectangle {
                            required property var modelData
                            Layout.fillWidth: true; Layout.leftMargin: 22; Layout.rightMargin: 22
                            implicitHeight: fleetRow.implicitHeight + 28
                            color: root.kit.card; border.color: root.kit.border; radius: root.kit.radius
                            RowLayout {
                                id: fleetRow
                                anchors.fill: parent; anchors.margins: 14
                                Image { Layout.preferredWidth: 100; Layout.preferredHeight: 130; source: modelData.art ? "file://" + modelData.art : ""; fillMode: Image.PreserveAspectFit }
                                ColumnLayout {
                                    Layout.fillWidth: true
                                    Label { text: modelData.name; font.bold: true; color: root.kit.foreground; textFormat: Text.PlainText }
                                    Label { Layout.fillWidth: true; text: modelData.caption + " · " + modelData.headline; color: root.kit.foreground; wrapMode: Text.Wrap; textFormat: Text.PlainText }
                                    Progress { kit: root.kit; Layout.fillWidth: true; value: modelData.progress }
                                    Label { text: modelData.stateLine; color: root.kit.muted; textFormat: Text.PlainText }
                                    Field { kit: root.kit; controller: ({activate: function() { root.send("select", modelData.serial); root.send("navigate:Printer") }}); field: ({kind:"button",key:"select",label:root.t("Printer")}) }
                                }
                            }
                        }
                    }
                    Repeater {
                        model: root.sections.length
                        Rectangle {
                            id: sectionDelegate
                            required property int index
                            readonly property var modelData: root.sections[index]
                            Layout.fillWidth: true; Layout.leftMargin: 22; Layout.rightMargin: 22
                            implicitHeight: section.implicitHeight + 28
                            visible: modelData.fields.length > 0
                            color: root.kit.card; border.color: root.kit.border; radius: root.kit.radius
                            ColumnLayout {
                                id: section
                                anchors.left: parent.left; anchors.right: parent.right; anchors.top: parent.top; anchors.margins: 14
                                spacing: 12
                                Label { text: modelData.title; font.bold: true; color: root.kit.foreground; textFormat: Text.PlainText }
                                Repeater {
                                    model: sectionDelegate.modelData.fields.length
                                    ColumnLayout {
                                        required property int index
                                        readonly property var modelData: sectionDelegate.modelData.fields[index]
                                        Layout.fillWidth: true
                                        RowLayout {
                                            Layout.fillWidth: true
                                            visible: modelData.kind === "text"
                                            Label { Layout.preferredWidth: 140; text: modelData.label; color: root.kit.muted; wrapMode: Text.Wrap; textFormat: Text.PlainText }
                                            Label { Layout.fillWidth: true; text: String(modelData.value || "—"); color: root.kit.foreground; wrapMode: Text.Wrap; textFormat: Text.PlainText }
                                        }
                                        Field { Layout.fillWidth: true; visible: modelData.kind !== "text"; field: modelData; kit: root.kit; controller: root }
                                    }
                                }
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
