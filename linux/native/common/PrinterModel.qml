import QtQuick
import QtQuick3D

Item {
    id: root
    property var printer: ({})
    property bool motion: true
    property real yaw: -24
    property real pitch: 10
    property var parts: []
    property var spools: []
    property var moving: []
    property var tinted: []
    property real tick: 0
    function color(value, fallback) { return /^[0-9a-fA-F]{8}$/.test(value || "") ? "#" + value.slice(0,6) : fallback }
    function collect(node) {
        const name = node.objectName || ""
        if (/^Part_\d+$/.test(name)) parts.push(node)
        if (/^Filament_\d+$/.test(name)) spools.push(node)
        if (name === "Toolhead") moving.push({node:node,x:node.x,z:node.z})
        for (const child of node.children || []) collect(child)
    }
    function tint(node, value) {
        if (node.materials && node.materials.length) {
            let entry = tinted.find(e => e.node === node)
            if (!entry) {
                // The GLB reuses a material across all four spools. Give each mesh its own
                // material so changing the last spool cannot recolor every spool.
                const material = Qt.createQmlObject('import QtQuick3D; PrincipledMaterial { roughness: 0.65 }', root)
                entry = {node:node, material:material}
                tinted.push(entry)
                node.materials = [material]
            }
            entry.material.baseColor = value
        }
        for (const child of node.children || []) tint(child, value)
    }
    function update() {
        const count = parts.reduce((n,p) => Math.max(n, Number(p.objectName.slice(5)) + 1), 0)
        const shown = printer.kind === "finished" ? count : Math.ceil((printer.progress || 0) * count / 100)
        for (const part of parts) {
            part.visible = Number(part.objectName.slice(5)) < shown
            tint(part, color(printer.activeColor, "#21cc6b"))
        }
        const loaded = (printer.ams || []).reduce((all,a) => all.concat(a.spools || []), [])
        for (const spool of spools) {
            const index = Number(spool.objectName.slice(9))
            if (loaded[index]) tint(spool, color(loaded[index].color, "#888888"))
        }
    }
    onPrinterChanged: update()
    View3D {
        anchors.fill: parent
        environment: SceneEnvironment { backgroundMode: SceneEnvironment.Transparent; antialiasingMode: SceneEnvironment.NoAA }
        camera: camera
        PerspectiveCamera { id: camera; z: 530; clipNear: 1; clipFar: 2000; fieldOfView: 40 }
        DirectionalLight { eulerRotation: Qt.vector3d(-35,-35,0); brightness: 2.3; ambientColor: "#a0a0a0" }
        DirectionalLight { eulerRotation: Qt.vector3d(30,130,0); brightness: 0.8 }
        Node {
            eulerRotation: Qt.vector3d(root.pitch, root.yaw, 0)
            Loader3D {
                id: model
                source: Qt.resolvedUrl("models/" + (root.printer.shape || "enclosed") + "/Printer.qml")
                onLoaded: { for (const entry of root.tinted) entry.material.destroy(); root.tinted=[];root.parts=[];root.spools=[];root.moving=[];root.collect(item);root.update() }
            }
        }
    }
    MouseArea {
        anchors.fill: parent
        property real startX: 0
        property real startY: 0
        property real startYaw: 0
        property real startPitch: 0
        onPressed: mouse => { startX=mouse.x;startY=mouse.y;startYaw=root.yaw;startPitch=root.pitch }
        onPositionChanged: mouse => { if (pressed) { root.yaw=startYaw+(mouse.x-startX)*0.5;root.pitch=Math.max(-10,Math.min(40,startPitch+(mouse.y-startY)*0.3)) } }
    }
    Timer {
        interval: 67; repeat: true
        running: root.visible && root.motion && root.printer.kind === "printing"
        onTriggered: {
            root.tick += 0.067
            for (const part of root.moving) { part.node.x=part.x+0.025*Math.sin(root.tick*2);part.node.z=part.z+0.018*Math.cos(root.tick*2) }
        }
    }
}
