import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import QtQuick.Window

ColumnLayout {
    id: root
    required property QtObject kit
    required property var controller
    property var printer: ({})
    property bool compact: false
    property bool filamentOnly: false
    property var viewport: null
    spacing: 14
    function t(key) { return controller && controller.t ? controller.t(key) : key }
    function text(value) { return value === undefined || value === null ? "" : String(value) }
    Rectangle {
        Layout.fillWidth: true
        implicitHeight: hero.implicitHeight + 28
        visible: !root.filamentOnly
        color: root.kit.card
        border.color: root.kit.border
        radius: root.kit.radius
        RowLayout {
            id: hero
            anchors.fill: parent
            anchors.margins: 14
            spacing: 18
            Loader {
                id: modelLoader
                Layout.preferredWidth: 210
                Layout.preferredHeight: 230
                visible: !root.compact
                active: visible && !!root.printer.id && (!root.Window.window || (root.Window.window.visible && root.Window.window.visibility !== Window.Minimized)) && (!root.viewport || (root.viewport.contentItem.contentY >= 0 && modelLoader.mapToItem(root.viewport,0,0).y + height > 0 && modelLoader.mapToItem(root.viewport,0,0).y < root.viewport.height))
                source: active ? "PrinterModel.qml" : ""
                onLoaded: { item.printer = Qt.binding(() => root.printer); item.motion = Qt.binding(() => root.kit.animationsEnabled) }
            }
            Image {
                Layout.preferredWidth: root.compact ? 100 : 160
                Layout.preferredHeight: root.compact ? 140 : 200
                source: root.printer.art ? "file://" + root.printer.art : ""
                fillMode: Image.PreserveAspectFit
                cache: false
                asynchronous: true
                visible: root.compact && source !== ""
            }
            ColumnLayout {
                Layout.fillWidth: true
                spacing: 8
                Label { Layout.fillWidth: true; text: root.text(root.printer.state); color: root.printer.kind === "failed" ? root.kit.negative : root.kit.accent; font.family: root.kit.fontFamily; textFormat: Text.PlainText }
                Label { Layout.fillWidth: true; text: root.text(root.printer.online === false ? root.printer.name : root.printer.job || root.printer.name); color: root.kit.foreground; font.family: root.kit.fontFamily; font.pixelSize: root.compact ? 17 : 23; font.bold: true; wrapMode: Text.Wrap; textFormat: Text.PlainText }
                Label { Layout.fillWidth: true; text: root.text(root.printer.stage); color: root.kit.muted; wrapMode: Text.Wrap; textFormat: Text.PlainText }
                Label { visible: root.printer.online !== false; text: root.text(root.printer.progress || 0) + "%"; color: root.kit.foreground; font.pixelSize: root.compact ? 25 : 36 }
                Progress { kit: root.kit; visible: root.printer.online !== false; Layout.fillWidth: true; value: root.printer.progress || 0 }
                Label { Layout.fillWidth: true; text: root.text(root.printer.detail); color: root.kit.muted; wrapMode: Text.Wrap; textFormat: Text.PlainText }
            }
        }
    }
    Label { Layout.fillWidth: true; visible: !!root.printer.issue && !root.filamentOnly; text: root.text(root.printer.issue); wrapMode: Text.Wrap; color: root.kit.foreground; textFormat: Text.PlainText }
    Flow {
        Layout.fillWidth: true; spacing: 8
        visible: !!root.printer.issue && !root.filamentOnly && !root.compact
        Repeater {
            model: [{key:"copy-issue",label:root.t("Copy code")},{key:"issue-wiki",label:root.t("Wiki")},{key:"dismiss-issue",label:root.t("Dismiss")}]
            Field { required property var modelData; kit: root.kit; controller: root.controller; field: Object.assign({kind:"button"},modelData) }
        }
    }
    Flow {
        Layout.fillWidth: true; spacing: 8; visible: !root.filamentOnly
        Repeater {
            model: [
                {key: "refresh", label: root.t("Refresh"), shown: true},
                {key: "light", label: root.t(root.printer.lightOn ? "Turn light off" : "Light"), shown: root.printer.light},
                {key: "camera", label: root.t("Camera"), shown: root.printer.camera},
                {key: root.printer.paused ? "resume" : "pause", label: root.t(root.printer.paused ? "Resume" : "Pause"), shown: root.printer.showControls, enabled: root.printer.pause},
                {key: "stop", label: root.t("Stop"), note: "confirmation", shown: root.printer.showControls, enabled: root.printer.stop}]
            Field { required property var modelData; visible: !!modelData.shown; field: Object.assign({kind: "button"}, modelData); controller: root.controller; kit: root.kit }
        }
        Field { visible: !!root.printer.canSpeed; field: ({key: "speed", label: root.t("Speed"), kind: "choice", value: String(root.printer.speed || 2), options: (root.printer.speeds || []).map(s => ({value: String(s.id), label: s.title}))}); controller: root.controller; kit: root.kit; width: 130 }
    }
    Label { Layout.fillWidth: true; visible: !!root.printer.locked && !!root.printer.showControls; text: "Enable Developer Mode on the printer to use controls"; wrapMode: Text.Wrap; color: root.kit.muted }
    Flow {
        Layout.fillWidth: true
        spacing: 8
        visible: !root.filamentOnly
        Repeater {
            model: root.printer.temperatures || []
            Rectangle {
                required property var modelData
                width: Math.max(90, (root.width - 24) / Math.max(1, (root.printer.temperatures || []).length))
                height: 65
                color: root.kit.card; border.color: root.kit.border; radius: root.kit.radius
                Column {
                    anchors.centerIn: parent; spacing: 4
                    Label { anchors.horizontalCenter: parent.horizontalCenter; text: modelData.name; color: root.kit.muted; textFormat: Text.PlainText }
                    Label { anchors.horizontalCenter: parent.horizontalCenter; text: modelData.value; font.pixelSize: 20; color: root.kit.foreground; textFormat: Text.PlainText }
                }
            }
        }
    }
    Label { visible: !!root.printer.fans && !root.filamentOnly; text: root.text(root.printer.fans); color: root.kit.muted; textFormat: Text.PlainText }
    Repeater {
        model: root.filamentOnly && root.printer.online === false ? [] : (root.printer.ams || []).concat(root.printer.external ? [{title: "External spool", spools: [root.printer.external]}] : [])
        ColumnLayout {
            required property var modelData
            Layout.fillWidth: true
            Label { Layout.fillWidth: true; text: modelData.title + (modelData.humidity !== undefined && modelData.humidity !== null ? " · " + modelData.humidity + "%" : "") + (modelData.temperature ? " · " + modelData.temperature + "°" : ""); color: root.kit.foreground; font.bold: true; textFormat: Text.PlainText }
            Flow {
                Layout.fillWidth: true; spacing: 8
                Repeater {
                    model: modelData.spools || []
                    Rectangle {
                        required property var modelData
                        width: root.filamentOnly ? (root.width - 8) / 2 : root.compact ? 82 : Math.max(125, (root.width - 24) / 4)
                        height: root.filamentOnly ? spoolContents.implicitHeight + 24 : root.compact ? 126 : 140
                        color: root.kit.card; border.color: root.kit.border; radius: root.kit.radius
                        Column {
                            id: spoolContents
                            anchors.left: parent.left; anchors.right: parent.right; anchors.top: parent.top; anchors.margins: 9; spacing: 5
                            Rectangle { anchors.horizontalCenter: parent.horizontalCenter; width: 30; height: 30; radius: 15; color: /^[0-9a-fA-F]{8}$/.test(modelData.color || "") ? "#" + modelData.color.slice(0, 6) : modelData.color || "transparent"; border.width: 3; border.color: root.kit.border }
                            Label { width: parent.width; text: root.text(modelData.slot); color: root.kit.foreground; horizontalAlignment: Text.AlignHCenter; textFormat: Text.PlainText }
                            Label { width: parent.width; text: root.text(modelData.name); color: root.kit.foreground; horizontalAlignment: Text.AlignHCenter; elide: Text.ElideRight; textFormat: Text.PlainText }
                            Label { width: parent.width; text: root.text(modelData.remaining); color: root.kit.muted; horizontalAlignment: Text.AlignHCenter; elide: Text.ElideRight; textFormat: Text.PlainText }
                            Label { width: parent.width; visible: root.filamentOnly; text: [modelData.brand, modelData.title].filter(Boolean).join(" · "); color: root.kit.foreground; wrapMode: Text.Wrap; horizontalAlignment: Text.AlignHCenter; textFormat: Text.PlainText }
                            Label { width: parent.width; visible: root.filamentOnly; text: [modelData.colorName, modelData.loaded ? "#" + (modelData.color || "").slice(0,6) : ""].filter(Boolean).join(" · "); color: root.kit.muted; wrapMode: Text.Wrap; horizontalAlignment: Text.AlignHCenter; textFormat: Text.PlainText }
                            Label { width: parent.width; visible: root.filamentOnly; text: root.text(modelData.remainingDetail); color: root.kit.muted; horizontalAlignment: Text.AlignHCenter; textFormat: Text.PlainText }
                            Progress { kit: root.kit; width: parent.width; visible: root.filamentOnly && modelData.percent !== null && modelData.percent !== undefined; value: modelData.percent || 0 }
                        }
                    }
                }
            }
        }
    }

}
