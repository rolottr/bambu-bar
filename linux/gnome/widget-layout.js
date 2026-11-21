// Positions are relative to the usable monitor area. Reserve each requested
// corner before stacking additional cards so opposite stacks never grow through each other.
export function placeWidgets(items, area) {
    const placed = [];
    const result = [];
    const seen = new Set();
    const first = [], extra = [];
    items.forEach((item, index) => {
        (seen.has(item.corner) ? extra : first).push(index);
        seen.add(item.corner);
    });

    for (const index of first.concat(extra)) {
        const {width, height, corner = 'top-right'} = items[index];
        const right = corner.endsWith('right'), bottom = corner.startsWith('bottom');
        const maxX = Math.max(0, area.width - width), maxY = Math.max(0, area.height - height);
        const padX = Math.min(20, maxX / 2), padY = Math.min(20, maxY / 2);
        const anchorX = right ? maxX - padX : padX, anchorY = bottom ? maxY - padY : padY;
        const xs = [anchorX], ys = [anchorY];
        for (const p of placed) {
            xs.push(p.x - width - 20, p.x + p.width + 20);
            ys.push(p.y - height - 16, p.y + p.height + 16);
        }
        xs.sort((a, b) => right ? b - a : a - b);
        ys.sort((a, b) => bottom ? b - a : a - b);
        let best, leastOverlap = Infinity, bestDistance = Infinity;
        for (const x of xs) {
            if (x < padX || x > maxX - padX) continue;
            for (const y of ys) {
                if (y < padY || y > maxY - padY) continue;
                let overlap = 0;
                for (const p of placed) {
                    const dx = Math.min(x + width + 20, p.x + p.width + 20) - Math.max(x, p.x);
                    const dy = Math.min(y + height + 16, p.y + p.height + 16) - Math.max(y, p.y);
                    overlap += Math.max(0, dx) * Math.max(0, dy);
                }
                // Stack on the anchored edge first. If it is full, choose the
                // nearest free rectangle to this corner across the other columns.
                const distance = Math.abs(anchorY - y) + Math.abs(anchorX - x)
                    + (x === anchorX ? 0 : area.width + area.height);
                if (overlap < leastOverlap || (overlap === leastOverlap && distance < bestDistance)) {
                    best = {x, y, width, height};
                    leastOverlap = overlap;
                    bestDistance = distance;
                }
                if (overlap === 0) break;
            }
            if (leastOverlap === 0 && x === anchorX) break;
        }
        placed.push(best);
        result[index] = {x: area.x + best.x, y: area.y + best.y};
    }
    return result;
}
