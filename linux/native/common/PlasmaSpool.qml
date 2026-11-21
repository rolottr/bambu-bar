import QtQuick
import QtQuick.Controls as Controls

Canvas {
    id: reel
    required property var spool
    required property bool active
    required property color ink
    required property color accent
    required property color surface
    implicitWidth: 40; implicitHeight: 40
    onSpoolChanged: requestPaint()
    onActiveChanged: requestPaint()
    onInkChanged: requestPaint()
    onAccentChanged: requestPaint()
    onSurfaceChanged: requestPaint()
    onPaint: {
        const c = getContext("2d")
        c.reset()
        const center = width / 2
        const start = -Math.PI / 2
        function circle(r) { c.beginPath(); c.arc(center, center, r, 0, 2*Math.PI) }
        circle(center - 1.5)
        c.lineWidth = 2.5; c.strokeStyle = Qt.alpha(ink,.15); c.stroke()
        if (spool.empty) {
            circle(center - 6)
            c.setLineDash([2,3]); c.lineWidth = 1; c.strokeStyle = Qt.alpha(ink,.34); c.stroke()
            return
        }
        const amount = spool.fraction ?? (active ? 1 : 0)
        if (amount > 0) {
            c.beginPath(); c.arc(center, center, center - 1.5, start, start + 2*Math.PI*Math.min(1,amount))
            c.lineCap = "round"; c.strokeStyle = active ? accent : Qt.alpha(ink,.7); c.stroke()
        }
        const colors = spool.colors?.length ? spool.colors : [spool.color || ink]
        const fill = c.createLinearGradient(6,6,width-6,height-6)
        colors.forEach((color,i) => fill.addColorStop(i/Math.max(1,colors.length-1),color))
        circle(center-5); c.fillStyle = fill; c.fill()
        const gloss = c.createLinearGradient(6,6,width-6,height-6)
        gloss.addColorStop(0,"#50ffffff"); gloss.addColorStop(.5,"transparent"); gloss.addColorStop(1,"#50000000")
        c.fillStyle = gloss; c.fill()
        for (const r of [center-7,center-10]) {
            circle(r); c.lineWidth = .8; c.strokeStyle = Qt.rgba(0,0,0,.22); c.stroke()
        }
        circle(4); c.fillStyle = surface; c.fill(); c.strokeStyle = Qt.rgba(1,1,1,.45); c.stroke()
    }
    HoverHandler { id: hover }
    Controls.ToolTip.visible: hover.hovered && !!spool.tooltip
    Controls.ToolTip.text: spool.tooltip || ""
}
