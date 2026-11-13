// Pure placement logic, shared by Shell and the layout regression checks.
export function placeWidgets(items, area) {
    const stacks = new Map();
    return items.map(item => {
        const corner = item.corner ?? 'top-right';
        const stack = stacks.get(corner) ?? {offset: 0, column: 0};
        if (stack.offset > 0 && stack.offset + item.height > area.height - 40) {
            stack.offset = 0;
            stack.column++;
        }
        const right = corner.endsWith('right');
        const bottom = corner.startsWith('bottom');
        const x = right ? area.x + area.width - item.width - 20 - stack.column * 350 : area.x + 20 + stack.column * 350;
        const y = bottom ? area.y + area.height - item.height - 20 - stack.offset : area.y + 20 + stack.offset;
        stack.offset += item.height + 16;
        stacks.set(corner, stack);
        return {x: Math.max(area.x, Math.min(x, area.x + area.width - item.width)),
            y: Math.max(area.y, Math.min(y, area.y + area.height - item.height))};
    });
}
