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

function calcSpeed(characters, minutes) {
    if (!minutes || minutes <= 0 || !characters || characters <= 0) {
        return null;
    }
    return Math.round(characters / (minutes / 60));
}

/**
 * Converts daily totals to uPlot data based on selection mode.
 * Returns [xVals, charactersVals, speedVals].
 *
 * @param {{ date: string, value: number, timeMinutes?: number }[]} items
 * @param {string} [mode]
 * @param {string} [startDate] 'YYYY-MM-DD'
 * @param {string} [endDate] 'YYYY-MM-DD'
 * @returns {[number[], number[], (number|null)[]]}
 */
export function toChartData(items, mode, startDate, endDate) {
    const itemMap = new Map(items.map(item => [item.date, item]));

    if (mode === 'all') {
        if (items.length === 0) {
            const empty = [[1], [0], [null]];
            empty.labels = { 1: '' };
            return empty;
        }

        const monthTotals = new Map();
        for (const item of items) {
            const ym = item.date.slice(0, 7);
            const entry = monthTotals.get(ym) ?? { chars: 0, time: 0 };
            entry.chars += item.value;
            entry.time += (item.timeMinutes ?? 0);
            monthTotals.set(ym, entry);
        }

        const allYMs = Array.from(monthTotals.keys()).sort();
        const startYM = (startDate ? startDate.slice(0, 7) : allYMs[0]) || allYMs[0];
        const endYM = (endDate ? endDate.slice(0, 7) : allYMs[allYMs.length - 1]) || allYMs[allYMs.length - 1];

        const [startYear, startMonth] = startYM.split('-').map(Number);
        const [endYear, endMonth] = endYM.split('-').map(Number);

        const xVals = [];
        const yVals = [];
        const speedVals = [];
        const labels = {};

        let currentYear = startYear;
        let currentMonth = startMonth;
        let index = 1;

        const monthNames = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

        while (currentYear < endYear || (currentYear === endYear && currentMonth <= endMonth)) {
            const ymKey = `${currentYear}-${String(currentMonth).padStart(2, '0')}`;
            const entry = monthTotals.get(ymKey) ?? { chars: 0, time: 0 };
            const shortYear = String(currentYear).slice(2);
            const label = `${monthNames[currentMonth - 1]} '${shortYear}`;

            xVals.push(index);
            yVals.push(entry.chars);
            speedVals.push(calcSpeed(entry.chars, entry.time));
            labels[index] = label;

            index++;
            currentMonth++;
            if (currentMonth > 12) {
                currentMonth = 1;
                currentYear++;
            }
        }

        const result = [xVals, yVals, speedVals];
        result.labels = labels;
        return result;
    }

    if (mode === 'year') {
        const targetYear = startDate
            ? parseInt(startDate.slice(0, 4), 10)
            : (items.length > 0 ? parseInt(items[items.length - 1].date.slice(0, 4), 10) : new Date().getFullYear());

        let maxMonth = 12;
        if (endDate) {
            const [endYear, endMonth] = endDate.split('-').map(Number);
            if (endYear === targetYear && !isNaN(endMonth) && endMonth >= 1 && endMonth <= 12) {
                maxMonth = endMonth;
            }
        }

        const monthTotals = Array.from({ length: maxMonth }, () => ({ chars: 0, time: 0 }));
        for (const item of items) {
            const itemYear = parseInt(item.date.slice(0, 4), 10);
            if (itemYear !== targetYear) continue;

            const m = parseInt(item.date.substring(5, 7), 10);
            if (m >= 1 && m <= maxMonth) {
                monthTotals[m - 1].chars += item.value;
                monthTotals[m - 1].time += (item.timeMinutes ?? 0);
            }
        }
        const xVals = Array.from({ length: maxMonth }, (_, i) => i + 1);
        const yVals = monthTotals.map(m => m.chars);
        const speedVals = monthTotals.map(m => calcSpeed(m.chars, m.time));
        return [xVals, yVals, speedVals];
    }

    if (mode === 'month') {
        const start = startDate ? new Date(`${startDate}T00:00:00Z`) : new Date();
        const year = start.getUTCFullYear();
        const month = start.getUTCMonth();
        let maxDay = new Date(Date.UTC(year, month + 1, 0)).getUTCDate();
        if (endDate) {
            const [endYear, endMonth, endDay] = endDate.split('-').map(Number);
            if (endYear === year && endMonth === month + 1 && !isNaN(endDay)) {
                maxDay = Math.min(maxDay, endDay);
            }
        }

        const xVals = [];
        const yVals = [];
        const speedVals = [];
        for (let day = 1; day <= maxDay; day++) {
            const dateStr = `${year}-${String(month + 1).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
            const item = itemMap.get(dateStr);
            const chars = item ? item.value : 0;
            const time = item ? (item.timeMinutes ?? 0) : 0;

            xVals.push(day);
            yVals.push(chars);
            speedVals.push(calcSpeed(chars, time));
        }
        return [xVals, yVals, speedVals];
    }

    if (mode === 'week') {
        const start = startDate ? new Date(`${startDate}T00:00:00Z`) : new Date();
        const end = endDate ? new Date(`${endDate}T00:00:00Z`) : new Date(start.getTime() + 6 * 86400000);
        const msPerDay = 86400000;
        const dayDiff = Math.round((end.getTime() - start.getTime()) / msPerDay);
        const totalDays = Math.min(7, Math.max(1, dayDiff + 1));

        const xVals = [];
        const yVals = [];
        const speedVals = [];
        const labels = {};

        for (let i = 0; i < totalDays; i++) {
            const curr = new Date(start.getTime() + i * msPerDay);
            const dateStr = curr.toISOString().slice(0, 10);
            const item = itemMap.get(dateStr);
            const chars = item ? item.value : 0;
            const time = item ? (item.timeMinutes ?? 0) : 0;

            const dayOfMonth = curr.getUTCDate();
            xVals.push(i + 1);
            yVals.push(chars);
            speedVals.push(calcSpeed(chars, time));
            labels[i + 1] = String(dayOfMonth);
        }

        const result = [xVals, yVals, speedVals];
        result.labels = labels;
        return result;
    }

    // Default: day or raw list
    if (items.length === 0) {
        return [[1], [0], [null]];
    }

    return [
        items.map((_, i) => i + 1),
        items.map(i => i.value),
        items.map(i => calcSpeed(i.value, i.timeMinutes))
    ];
}
