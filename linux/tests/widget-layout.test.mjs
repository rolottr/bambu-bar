import test from 'node:test';
import assert from 'node:assert/strict';
import {placeWidgets} from '../gnome/widget-layout.js';
const area = {x: 100, y: 30, width: 1920, height: 1000};
test('all four anchors respect a monitor work area with a nonzero origin', () => {
    for (const corner of ['top-left', 'top-right', 'bottom-left', 'bottom-right']) {
        const [p] = placeWidgets([{width: 330, height: 300, corner}], area);
        assert.equal(p.x, corner.endsWith('right') ? 1670 : 120);
        assert.equal(p.y, corner.startsWith('bottom') ? 710 : 50);
    }
});
test('multiple large widgets wrap to a new column instead of overlapping', () => {
    const positions = placeWidgets(Array.from({length: 6}, () => ({width:330,height:360,corner:'top-right'})), area);
    for (let i=0; i<positions.length; i++) for (let j=i+1; j<positions.length; j++) {
        const a=positions[i], b=positions[j];
        assert.ok(Math.abs(a.x-b.x)>=330 || Math.abs(a.y-b.y)>=360);
    }
});
test('a display resize clamps cards inside the new work area', () => {
    const [p] = placeWidgets([{width:330,height:360,corner:'bottom-right'}], {x:0,y:24,width:800,height:500});
    assert.ok(p.x>=0 && p.x+330<=800 && p.y>=24 && p.y+360<=524);
});
