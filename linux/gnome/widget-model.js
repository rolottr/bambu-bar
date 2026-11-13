// Shared display decisions, independent of Shell so state transitions can be checked locally.
export function widgetModel(state) {
    const live = state.live === true;
    const progress = live ? Math.max(0, Math.min(100, Number(state.progress) || 0)) : 0;
    const color = value => /^#[0-9a-f]{6}$/i.test(value ?? '') ? value : '#9ea8b3';
    const issue = state.issue?.text ?? '';
    const detail = state.stateLine === issue ? '' : state.stateLine;
    return {live, progress, tint: color(state.tint), color: color(state.stateColor), detail,
        issue, secondary: state.detail === state.stateLine ? '' : state.detail, active: live && ['printing', 'preparing', 'paused'].includes(state.state),
        dimensions: {small: [180, 180], medium: [380, 180], large: [380, 380]}};
}
