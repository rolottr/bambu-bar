import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// The Activity page (the Mac's ActivityPage): reported capabilities, then the event list with its filters.
// The app sends at most one page of events, already filtered and redacted.
ColumnLayout {
    id: view
    required property QtObject kit
    required property var controller
    required property var activity
    spacing: 22
    readonly property var labels: activity.labels || ({})
    function tint(category) {
        return category === "job" ? kit.positive : category === "issue" ? kit.warning : category === "camera" ? kit.muted : kit.accentInk
    }
    function outcomeColor(outcome) { return outcome === "applied" ? kit.positive : outcome === "failed" ? kit.negative : kit.muted }

    ColumnLayout {
        Layout.fillWidth: true; spacing: 8
        Label { Layout.fillWidth: true; text: view.labels.capabilities || ""; font.pixelSize: 16; font.bold: true; color: view.kit.foreground; elide: Text.ElideRight; textFormat: Text.PlainText }
        Rectangle {
            Layout.fillWidth: true
            implicitHeight: capabilities.implicitHeight + 28
            radius: Math.max(4, view.kit.radius); color: view.kit.card
            ColumnLayout {
                id: capabilities
                anchors.left: parent.left; anchors.right: parent.right; anchors.top: parent.top; anchors.margins: 14
                spacing: 14
                Label { visible: (view.activity.capabilities || []).length === 0; text: view.labels.noPrinter || ""; color: view.kit.muted; textFormat: Text.PlainText }
                Repeater {
                    model: view.activity.capabilities || []
                    ColumnLayout {
                        id: printer
                        required property var modelData
                        required property int index
                        Layout.fillWidth: true; spacing: 10
                        Rectangle { visible: printer.index > 0; Layout.fillWidth: true; implicitHeight: 1; color: view.kit.border }
                        RowLayout {
                            Layout.fillWidth: true; spacing: 8
                            Rectangle { implicitWidth: 8; implicitHeight: 8; radius: 4; color: printer.modelData.online ? view.kit.positive : view.kit.muted }
                            Label { text: printer.modelData.name; font.bold: true; color: view.kit.foreground; elide: Text.ElideRight; Layout.maximumWidth: capabilities.width * 0.4; textFormat: Text.PlainText }
                            Label { text: printer.modelData.model; color: view.kit.muted; textFormat: Text.PlainText }
                            Label { visible: !!printer.modelData.serial; text: printer.modelData.serial; font.family: "monospace"; color: view.kit.muted; textFormat: Text.PlainText }
                            Item { Layout.fillWidth: true }
                            Label { text: printer.modelData.status; color: printer.modelData.online ? view.kit.positive : view.kit.muted; textFormat: Text.PlainText }
                        }
                        GridLayout {
                            Layout.fillWidth: true
                            columns: capabilities.width >= 520 ? 2 : 1
                            columnSpacing: 24; rowSpacing: 8
                            Repeater {
                                model: printer.modelData.items || []
                                RowLayout {
                                    id: item
                                    required property var modelData
                                    Layout.fillWidth: true; Layout.preferredWidth: 1; spacing: 8
                                    Symbol { kit: view.kit; name: item.modelData.on ? "dialog-ok" : "list-remove"; tint: item.modelData.on ? view.kit.positive : view.kit.muted; implicitWidth: 12; implicitHeight: 12 }
                                    Label { Layout.fillWidth: true; text: item.modelData.label; color: item.modelData.on ? view.kit.foreground : view.kit.muted; elide: Text.ElideRight; textFormat: Text.PlainText }
                                    Label { visible: !!item.modelData.detail; text: item.modelData.detail || ""; color: view.kit.muted; textFormat: Text.PlainText }
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    ColumnLayout {
        Layout.fillWidth: true; spacing: 8
        GridLayout {
            Layout.fillWidth: true
            // The filters sit beside the heading, as on the Mac, and move below it when the window is narrow.
            columns: eventsHeading.implicitWidth + 16 + filters.implicitWidth <= width ? 2 : 1
            columnSpacing: 8; rowSpacing: 6
            Label { id: eventsHeading; Layout.fillWidth: true; text: view.labels.events || ""; font.pixelSize: 16; font.bold: true; color: view.kit.foreground; elide: Text.ElideRight; textFormat: Text.PlainText }
            Row {
                id: filters
                spacing: 4
                Repeater {
                    model: view.activity.filters || []
                    Field {
                        required property var modelData
                        kit: view.kit; controller: view.controller
                        field: ({kind:"button", key:"activity-filter:" + modelData.value, label:modelData.label, selected:modelData.value === (view.activity.filter || "")})
                    }
                }
            }
        }
        Rectangle {
            Layout.fillWidth: true
            implicitHeight: events.implicitHeight + 12
            radius: Math.max(4, view.kit.radius); color: view.kit.card
            ColumnLayout {
                id: events
                anchors.left: parent.left; anchors.right: parent.right; anchors.top: parent.top; anchors.topMargin: 6
                spacing: 0
                Label {
                    visible: (view.activity.events || []).length === 0
                    Layout.fillWidth: true; Layout.margins: 10
                    text: view.labels.empty || ""; color: view.kit.muted; textFormat: Text.PlainText
                }
                Repeater {
                    model: view.activity.events || []
                    ColumnLayout {
                        id: entry
                        required property var modelData
                        required property int index
                        Layout.fillWidth: true; spacing: 0
                        Rectangle { visible: entry.index > 0; Layout.fillWidth: true; Layout.leftMargin: 40; implicitHeight: 1; color: view.kit.border }
                        RowLayout {
                            Layout.fillWidth: true; Layout.leftMargin: 14; Layout.rightMargin: 14; Layout.topMargin: 7; Layout.bottomMargin: 7
                            spacing: 10
                            Symbol { Layout.alignment: Qt.AlignTop; kit: view.kit; name: entry.modelData.icon || ""; tint: view.tint(entry.modelData.category) }
                            Label { visible: !!entry.modelData.printer; Layout.alignment: Qt.AlignTop; text: entry.modelData.printer || ""; font.bold: true; color: view.kit.muted; textFormat: Text.PlainText }
                            Label { Layout.fillWidth: true; text: entry.modelData.summary || ""; wrapMode: Text.Wrap; maximumLineCount: 2; elide: Text.ElideRight; color: view.kit.foreground; textFormat: Text.PlainText }
                            Rectangle {
                                visible: !!entry.modelData.outcome
                                readonly property color ink: view.outcomeColor(entry.modelData.outcome)
                                implicitWidth: outcome.implicitWidth + 14; implicitHeight: outcome.implicitHeight + 4; radius: height / 2
                                color: Qt.rgba(ink.r, ink.g, ink.b, 0.14)
                                Label { id: outcome; anchors.centerIn: parent; text: entry.modelData.outcomeLabel || ""; color: parent.ink; font.bold: true; textFormat: Text.PlainText }
                            }
                            Label { text: entry.modelData.time || ""; font.family: "monospace"; color: view.kit.muted; textFormat: Text.PlainText }
                        }
                    }
                }
                Field {
                    visible: !!view.activity.more; active: visible
                    Layout.alignment: Qt.AlignHCenter; Layout.topMargin: 4; Layout.bottomMargin: 6
                    kit: view.kit; controller: view.controller
                    field: ({kind:"button", key:"activity-more", label:view.labels.more || ""})
                }
            }
        }
    }
}
