import test from 'node:test';
import assert from 'node:assert/strict';
import {widgetModel} from '../gnome/widget-model.js';

test('failed maintenance notice is shown once, without repeating it in the status line', () => {
    const model = widgetModel({live: true, state: 'failed', progress: 0, caption: 'Failed', layers: '0/192', stateLine: 'Lubricate rods', detail: 'Lubricate rods', issue: {text: 'Lubricate rods'}});
    assert.equal(model.detail, '');
    assert.equal(model.issue, 'Lubricate rods');
    assert.equal(model.secondary, '');
    assert.equal(model.active, false);
});
test('offline cards do not present stale progress as live', () => {
    assert.equal(widgetModel({live: false, progress: 85}).progress, 0);
});
test('live printing retains timing and clamps bad telemetry', () => {
    const model = widgetModel({live: true, state: 'printing', progress: 120, stateLine: '1h 12m left', tint: '#FF9959'});
    assert.equal(model.detail, '1h 12m left');
    assert.equal(model.progress, 100);
    assert.equal(model.tint, '#FF9959');
    assert.equal(model.active, true);
});
