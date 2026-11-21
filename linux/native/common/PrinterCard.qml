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
    readonly property bool overview: !compact && !filamentOnly
    spacing: compact ? 8 : 14
    readonly property color surface: Qt.tint(kit.background, Qt.rgba(kit.foreground.r, kit.foreground.g, kit.foreground.b, 0.065))
    function t(key) { return controller && controller.t ? controller.t(key) : key }
    function text(value) { return value === undefined || value === null ? "" : String(value) }
    function region(key) {
        const item = ({temperatures:temperatureTiles, fans:fanLine, filament:units.itemAt(0), actions:actions})[key]
        return item && item.visible && item.height > 0 ? item : null
    }
    Flow {
        Layout.fillWidth: true; spacing: 8; visible: !root.filamentOnly && !root.compact
        Repeater {
            model: [
                {key: "refresh", label: root.t("Refresh"), shown: root.compact},
                {key: "light", label: root.t(root.printer.lightOn ? "Turn light off" : "Light"), shown: root.printer.light},
                {key: "camera", label: root.t("Camera"), shown: root.printer.camera}]
            Field { required property var modelData; visible: !!modelData.shown; field: Object.assign({kind: "button"}, modelData); controller: root.controller; kit: root.kit }
        }
    }
    Rectangle {
        Layout.fillWidth: true
        implicitHeight: hero.implicitHeight + (root.compact ? 20 : 28)
        visible: !root.filamentOnly
        color: root.compact ? root.surface : root.kit.card
        border.width: root.compact ? 0 : 1
        border.color: root.kit.border
        radius: Math.max(root.kit.radius, 8)
        RowLayout {
            id: hero
            anchors.fill: parent
            anchors.margins: root.compact ? 10 : 14
            spacing: root.compact ? 12 : 18
            Loader {
                id: modelLoader
                Layout.preferredWidth: 210
                Layout.preferredHeight: 230
                visible: !root.compact
                active: root.visible && !root.filamentOnly && visible && !!root.printer.id && (!root.Window.window || (root.Window.window.visible && root.Window.window.visibility !== Window.Minimized)) && (!root.viewport || (root.viewport.contentItem.contentY >= 0 && modelLoader.mapToItem(root.viewport,0,0).y + height > 0 && modelLoader.mapToItem(root.viewport,0,0).y < root.viewport.height))
                source: active ? "PrinterModel.qml" : ""
                onLoaded: { item.printer = Qt.binding(() => root.printer); item.motion = Qt.binding(() => root.kit.animationsEnabled) }
            }
            Image {
                Layout.preferredWidth: root.compact ? 78 : 160
                Layout.preferredHeight: root.compact ? 88 : 200
                source: root.printer.art ? "file://" + root.printer.art : ""
                fillMode: Image.PreserveAspectFit
                cache: false
                asynchronous: true
                visible: root.compact && source !== ""
            }
            ColumnLayout {
                Layout.fillWidth: true
                spacing: 8
                RowLayout {
                    Layout.fillWidth: true
                    Label { Layout.fillWidth: true; text: root.text(root.printer.state); color: root.printer.kind === "failed" ? root.kit.negative : root.kit.accentInk; font.family: root.kit.fontFamily; elide: Text.ElideRight; textFormat: Text.PlainText }
                    Field {
                        visible: root.compact && root.printer.light === true
                        kit: root.kit; controller: root.controller
                        field: ({kind:"link", iconOnly:true, key:"light", icon:"lightbulb", label:root.t(root.printer.lightOn ? "Turn light off" : "Turn light on")})
                    }
                }
                Label { Layout.fillWidth: true; text: root.text(root.printer.online === false ? root.printer.name : root.printer.job || root.printer.name); color: root.kit.foreground; font.family: root.kit.fontFamily; font.pixelSize: root.compact ? 17 : 23; font.bold: true; elide: Text.ElideRight; maximumLineCount: root.compact ? 1 : 2; wrapMode: root.compact ? Text.NoWrap : Text.Wrap; textFormat: Text.PlainText }
                Label { visible: !!root.printer.stage; Layout.fillWidth: true; text: root.text(root.printer.stage); color: root.kit.muted; wrapMode: Text.Wrap; textFormat: Text.PlainText }
                GridLayout {
                    visible: root.printer.online !== false
                    Layout.fillWidth: true; columns: root.compact ? 2 : 1; columnSpacing: 10
                    Label { text: root.text(root.printer.progress || 0) + "%"; color: root.kit.foreground; font.pixelSize: root.compact ? 22 : 36 }
                    Progress { kit: root.kit; Layout.fillWidth: true; value: root.printer.progress || 0 }
                }
                Label { visible: !!root.printer.detail && root.printer.detail !== root.printer.state; Layout.fillWidth: true; text: root.text(root.printer.detail); color: root.kit.muted; wrapMode: Text.Wrap; textFormat: Text.PlainText }
            }
        }
    }
    PrintControls { visible: !root.compact && !root.filamentOnly && !!(root.printer.showControls || root.printer.canSpeed) }
    Rectangle {
        id: alert
        Layout.fillWidth: true; visible: !!root.printer.issue && !root.filamentOnly
        implicitHeight: alertBody.implicitHeight + 16
        color: Qt.tint(root.kit.background, Qt.rgba(root.kit.negative.r, root.kit.negative.g, root.kit.negative.b, 0.09))
        radius: 8
        ColumnLayout {
            id: alertBody
            anchors.left: parent.left; anchors.right: parent.right; anchors.verticalCenter: parent.verticalCenter
            anchors.margins: 10; spacing: 2
            RowLayout {
                Layout.fillWidth: true; spacing: 6
                Symbol { kit: root.kit; name: "dialog-warning"; tint: root.kit.negative; Layout.alignment: Qt.AlignTop }
                Label { Layout.fillWidth: true; text: root.text(root.printer.issue); wrapMode: Text.Wrap; color: root.kit.foreground; textFormat: Text.PlainText }
            }
            RowLayout {
                Layout.fillWidth: true; spacing: 4
                // TextEdit uses the current desktop's clipboard, also when hosted in the shell.
                TextEdit {
                    id: issueCode
                    Layout.fillWidth: true; readOnly: true
                    text: root.printer.issueId || ""; color: root.kit.muted; font: root.kit.nativeFont
                    wrapMode: TextEdit.WrapAnywhere; textFormat: TextEdit.PlainText
                    onTextChanged: copied.stop()
                }
                Timer { id: copied; interval: 1500 }
                Field {
                    visible: !!issueCode.text; kit: root.kit
                    field: ({kind:"link", iconOnly:true, icon:copied.running ? "dialog-ok" : "edit-copy", label:root.t("Copy code")})
                    controller: ({activate:() => { issueCode.selectAll(); issueCode.copy(); issueCode.deselect(); copied.restart() }})
                }
                Field {
                    visible: !!root.printer.issueWiki; kit: root.kit
                    field: ({kind:"link", iconOnly:true, icon:"globe", label:root.t("Wiki")})
                    controller: ({activate:() => Qt.openUrlExternally(root.printer.issueWiki)})
                }
                Field {
                    visible: root.overview && !!issueCode.text
                    kit: root.kit
                    field: ({kind:"link", key:"ignore-issue", label:root.t("Ignore")})
                    controller: ({activate: () => root.controller.send("ignore-issue", issueCode.text)})
                }
                Field {
                    kit: root.kit; controller: root.controller
                    field: ({kind:"link", iconOnly:true, key:"dismiss-issue", icon:"window-close", label:root.t("Dismiss")})
                }
            }
        }
    }
    Label { Layout.fillWidth: true; visible: !!root.printer.locked && !!root.printer.showControls; text: "Enable Developer Mode on the printer to use controls"; wrapMode: Text.Wrap; color: root.kit.muted }
    Label {
        visible: root.overview && !!root.printer.online && !!root.printer.temperatures?.length
        text: root.printer.metricsTitle || root.t("Temperatures & airflow")
        font.bold: true; font.pixelSize: 16; color: root.kit.foreground
    }
    GridLayout {
        Layout.fillWidth: true; columns: root.width >= 560 || root.compact ? 2 : 1
        visible: !root.filamentOnly && root.printer.online !== false && !!root.printer.temperatures?.length
        columnSpacing: 10; rowSpacing: 8
        RowLayout {
            id: temperatureTiles
            Layout.fillWidth: true; Layout.fillHeight: true; spacing: 6
            visible: !root.filamentOnly && root.printer.online !== false && !!root.printer.temperatures?.length
            Repeater {
                model: root.printer.temperatures || []
                Rectangle {
                    required property var modelData
                    Layout.fillWidth: true; Layout.preferredWidth: 1; Layout.minimumWidth: 0
                    Layout.fillHeight: true
                    implicitHeight: tempColumn.implicitHeight + 12
                    color: root.surface; radius: 8
                    ColumnLayout {
                        id: tempColumn
                        anchors.centerIn: parent; width: parent.width - 8; spacing: 2
                        RowLayout {
                            Layout.fillWidth: false; Layout.alignment: Qt.AlignHCenter
                            Layout.maximumWidth: tempColumn.width; spacing: 4
                            Symbol { kit: root.kit; name: modelData.name === root.t("Bed") ? "heat-waves" : modelData.name === root.t("Chamber") ? "temperature-normal" : "fire" }
                            Label { Layout.minimumWidth: 0; text: modelData.name; elide: Text.ElideRight; color: root.kit.muted; textFormat: Text.PlainText }
                        }
                        Label {
                            Layout.fillWidth: true; Layout.minimumWidth: 0
                            horizontalAlignment: Text.AlignHCenter
                            text: modelData.value; font.pixelSize: root.compact ? 17 : 20
                            fontSizeMode: Text.HorizontalFit; minimumPixelSize: 11
                            color: root.kit.foreground; textFormat: Text.PlainText
                        }
                    }
                }
            }
        }
        Pane {
            visible: root.overview && !!root.printer.fanReadings?.length
            Layout.fillWidth: parent.columns === 1; Layout.fillHeight: true
            Layout.preferredWidth: 165; padding: 10
            background: Rectangle { radius: 8; color: root.surface }
            contentItem: ColumnLayout {
                spacing: 3
                RowLayout {
                    Layout.alignment: Qt.AlignHCenter; Layout.fillWidth: false; spacing: 4
                    Symbol { kit: root.kit; name: "fan" }
                    Label { text: root.t("Fans"); color: root.kit.muted }
                }
                Repeater {
                    model: root.overview ? root.printer.fanReadings || [] : []
                    RowLayout {
                        required property var modelData
                        Layout.fillWidth: true; spacing: 6
                        Label { Layout.preferredWidth: 54; text: modelData.name; color: root.kit.muted; elide: Text.ElideRight; font.pixelSize: 11 }
                        Rectangle {
                            Layout.fillWidth: true; implicitHeight: 4; radius: 2
                            color: Qt.rgba(root.kit.foreground.r, root.kit.foreground.g, root.kit.foreground.b, 0.15)
                            Rectangle {
                                height: parent.height; radius: parent.radius
                                width: parent.width * Math.max(0, Math.min(100, modelData.value)) / 100
                                color: root.kit.accent
                            }
                        }
                        Label { Layout.preferredWidth: 34; text: modelData.value + "%"; horizontalAlignment: Text.AlignRight; font.pixelSize: 11; color: root.kit.foreground }
                    }
                }
            }
        }
    }
    Label { id: fanLine; Layout.fillWidth: true; visible: !!root.printer.fans && !root.filamentOnly && !(root.overview && root.printer.fanReadings); text: root.text(root.printer.fans); color: root.kit.muted; textFormat: Text.PlainText }
    Label {
        visible: root.overview && units.count > 0; text: root.t("Filament")
        font.bold: true; font.pixelSize: 16; color: root.kit.foreground
    }
    Pane {
        visible: units.count > 0
        Layout.fillWidth: true; padding: root.overview ? 12 : 0
        background: Rectangle { visible: root.overview; color: root.surface; radius: 8 }
        contentItem: ColumnLayout {
            spacing: 9
            Repeater {
                id: units
                model: root.filamentOnly && root.printer.online === false ? [] : (root.printer.ams || []).concat(root.printer.external ? [{title: "External spool", spools: [root.printer.external]}] : [])
                ColumnLayout {
                    required property var modelData
                    Layout.fillWidth: true
                    RowLayout {
                        Layout.fillWidth: true
                        Label { Layout.fillWidth: true; text: modelData.title; color: root.kit.foreground; font.bold: true; font.pixelSize: 16; textFormat: Text.PlainText }
                        Label {
                            text: [modelData.humidity != null ? modelData.humidity + "% " + root.t("humidity") : "",
                                   modelData.temperature != null ? modelData.temperature + "°" : ""].filter(Boolean).join("   ·   ")
                            color: root.kit.muted
                        }
                    }
                    GridLayout {
                        Layout.fillWidth: true
                        visible: root.filamentOnly
                        columns: root.width >= 660 ? 4 : 2
                        columnSpacing: 12
                        rowSpacing: 12
                        Repeater {
                            model: root.filamentOnly ? modelData.spools || [] : []
                            Rectangle {
                                required property var modelData
                                Layout.preferredWidth: (root.width - (parent.columns - 1) * parent.columnSpacing) / parent.columns
                                Layout.fillHeight: true
                                implicitHeight: details.implicitHeight + 28
                                color: root.kit.card
                                border.color: modelData.active ? root.kit.accent : "transparent"
                                radius: root.kit.radius
                                ColumnLayout {
                                    id: details
                                    anchors.fill: parent
                                    anchors.margins: 14
                                    spacing: 5
                                    RowLayout {
                                        Layout.fillWidth: true
                                        Rectangle {
                                            implicitWidth: 58; implicitHeight: 58; radius: 29
                                            color: modelData.loaded && modelData.color ? "#" + modelData.color.slice(0, 6) : "transparent"
                                            border.color: root.kit.border
                                            Rectangle {
                                                anchors.centerIn: parent
                                                width: modelData.weight ? 34 : 20; height: width; radius: width / 2
                                                color: root.kit.card; border.color: root.kit.border
                                                Label {
                                                    anchors.centerIn: parent; visible: !!modelData.weight
                                                    font.pixelSize: 10; color: root.kit.foreground
                                                    text: modelData.weight >= 1000 ? modelData.weight / 1000 + " kg" : modelData.weight + " g"
                                                }
                                            }
                                        }
                                        Item { Layout.fillWidth: true }
                                        ColumnLayout {
                                            Layout.alignment: Qt.AlignTop
                                            Label { Layout.alignment: Qt.AlignRight; text: modelData.slot; color: root.kit.foreground; font.bold: true }
                                            Label { visible: !!modelData.active; text: root.t("In use"); color: root.kit.accent }
                                        }
                                    }
                                    Label { Layout.fillWidth: true; Layout.topMargin: 8; text: modelData.loaded ? modelData.title : root.t("Empty slot"); color: root.kit.foreground; font.bold: true; wrapMode: Text.Wrap; textFormat: Text.PlainText }
                                    Label { Layout.fillWidth: true; text: modelData.loaded ? [modelData.brand, modelData.colorName || (modelData.color ? "#" + modelData.color.slice(0,6) : "")].filter(Boolean).join(" · ") : ""; color: root.kit.muted; wrapMode: Text.Wrap; textFormat: Text.PlainText }
                                    Item { Layout.fillHeight: true }
                                    RowLayout {
                                        Layout.fillWidth: true; Layout.topMargin: 8
                                        Label { Layout.fillWidth: true; text: root.t("Remaining"); color: root.kit.muted }
                                        Label { text: modelData.percent != null ? root.text(modelData.remaining) : "—"; color: root.kit.foreground }
                                    }
                                    RowLayout {
                                        Layout.fillWidth: true; opacity: modelData.percent == null ? 0 : 1
                                        Progress { Layout.fillWidth: true; kit: root.kit; value: modelData.percent || 0 }
                                        Label { text: (modelData.percent || 0) + "%"; color: root.kit.muted }
                                    }
                                }
                            }
                        }
                    }
                    RowLayout {
                        visible: !root.filamentOnly; Layout.fillWidth: true; spacing: 6
                        FontMetrics { id: spoolMetrics; font: root.kit.nativeFont }
                        Repeater {
                            model: root.filamentOnly ? [] : modelData.spools || []
                            ColumnLayout {
                                id: spool
                                required property var modelData
                                Layout.fillWidth: true; Layout.preferredWidth: 1; Layout.minimumWidth: 0
                                Layout.alignment: Qt.AlignTop
                                spacing: 2
                                Rectangle {
                                    Layout.alignment: Qt.AlignHCenter
                                    implicitWidth: 27; implicitHeight: 27; radius: width / 2
                                    readonly property string hex: spool.modelData.color || ""
                                    color: spool.modelData.loaded === false ? "transparent" : !hex ? root.surface : /^[0-9a-fA-F]{6,8}$/.test(hex) ? "#" + hex.slice(0,6) : hex
                                    border.width: 1; border.color: root.kit.muted
                                    Rectangle { visible: spool.modelData.loaded !== false; anchors.centerIn: parent; width: 10; height: width; radius: width/2; color: root.kit.background; border.color: root.kit.border }
                                    Rectangle { visible: !!spool.modelData.active; anchors.top: parent.top; anchors.right: parent.right; width: 8; height: width; radius: width/2; color: root.kit.accent; border.color: root.kit.background }
                                }
                                Label {
                                    Layout.fillWidth: true; Layout.preferredHeight: Math.ceil(spoolMetrics.height)
                                    horizontalAlignment: Text.AlignHCenter; verticalAlignment: Text.AlignVCenter; font.bold: true
                                    text: [modelData.slot, modelData.name].filter(Boolean).join(" · ")
                                    color: spool.modelData.loaded === false ? root.kit.muted : root.kit.foreground; elide: Text.ElideRight; textFormat: Text.PlainText
                                }
                                Label {
                                    Layout.fillWidth: true; Layout.preferredHeight: Math.ceil(spoolMetrics.height)
                                    horizontalAlignment: Text.AlignHCenter; verticalAlignment: Text.AlignVCenter
                                    text: root.text(modelData.remaining) || "—"; color: root.kit.muted
                                    elide: Text.ElideRight; textFormat: Text.PlainText
                                }
                            }
                        }
                    }
                }
            }
        }
    }
    PrintControls { id: actions; visible: root.compact && !root.filamentOnly && !!(root.printer.showControls || root.printer.canSpeed) }

    component PrintControls: RowLayout {
        Layout.fillWidth: true; spacing: 8
        Field {
            id: pauseButton
            visible: !!root.printer.showControls; Layout.fillWidth: true; Layout.preferredWidth: 1
            kit: root.kit; controller: root.controller
            field: ({kind:"button", key:root.printer.paused ? "resume" : "pause",
                     label:root.t(root.printer.paused ? "Resume" : "Pause"), enabled:root.printer.pause})
        }
        Field {
            visible: !!root.printer.showControls; Layout.fillWidth: true; Layout.preferredWidth: 1
            kit: root.kit; controller: root.controller
            field: ({kind:"button", key:"stop", label:root.t("Stop"), note:"confirmation", enabled:root.printer.stop})
        }
        Field {
            visible: !!root.printer.canSpeed
            Layout.fillWidth: true; Layout.preferredWidth: 1
            controller: root.controller; kit: root.kit
            field: ({key:"speed", icon:"speedometer", label:"", accessibleLabel:root.t("Speed"), kind:"choice",
                     controlHeight:pauseButton.visible ? pauseButton.implicitHeight : 0,
                     value:String(root.printer.speed || 2),
                     options:(root.printer.speeds || []).map(s => ({value:String(s.id), label:s.title}))})
        }
    }

}
