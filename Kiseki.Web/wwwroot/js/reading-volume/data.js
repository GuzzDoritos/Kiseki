function aggregate(items, getKey, getDate) {
    const buckets = new Map();

    for (const item of items) {
        const key = getKey(item.date);
        buckets.set(key, (buckets.get(key) ?? 0) + item.value);
    }

    return Array.from(buckets, ([key, value]) => ({
        date: getDate(key),
        value
    }));
}

/**
 * Converts daily totals to uPlot data, lowering the resolution for larger ranges.
 * Year ranges use monthly totals; all-time ranges use yearly totals.
 * @param {{ date: string, value: number }[]} items
 * @param {string} [mode]
 * @returns {[number[], number[]]}
 */
export function toChartData(items, mode) {
    let resolvedItems = items;

    if (mode === 'year' || mode === 'yearly') {
        resolvedItems = aggregate(
            items,
            date => date.slice(0, 7),
            month => `${month}-01`
        );
    } else if (mode === 'all') {
        resolvedItems = aggregate(
            items,
            date => date.slice(0, 4),
            year => `${year}-01-01`
        );
    }

    return [
        resolvedItems.map(item => new Date(`${item.date}T00:00:00Z`).getTime() / 1000),
        resolvedItems.map(item => item.value)
    ];
}
