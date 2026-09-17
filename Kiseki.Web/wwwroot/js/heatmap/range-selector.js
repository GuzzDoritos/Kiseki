import { toISODate, formatDateDisplay } from './calendar-math.js';

/**
 * Calculates the date range (start, end, label) for a given date string and mode.
 * @param {string} dateStr 
 * @param {'day'|'week'|'month'|'year'} mode
 * @returns {{ start: string, end: string, label: string }}
 */
export function getSelectedRange(dateStr, mode, dataMap) {
    const parts = dateStr.split('-').map(Number);
    const date = new Date(parts[0], parts[1] - 1, parts[2]);

    if (mode === 'year') {
        const yearStr = String(parts[0]);
        const today = new Date();
        const isCurrentYear = parts[0] === today.getFullYear();
        const endDate = isCurrentYear ? toISODate(today) : `${yearStr}-12-31`;

        return {
            start: `${yearStr}-01-01`,
            end: endDate,
            label: yearStr
        };
    }

    if (mode === 'month') {
        const monthPrefix = `${parts[0]}-${String(parts[1]).padStart(2, '0')}`;
        const first = `${monthPrefix}-01`;
        const lastDayOfMonth = new Date(parts[0], parts[1], 0).getDate();
        let end = `${monthPrefix}-${String(lastDayOfMonth).padStart(2, '0')}`;

        const todayStr = toISODate(new Date());
        const isCurrentOrFutureMonth = monthPrefix >= todayStr.slice(0, 7);

        if (dataMap && dataMap.size > 0) {
            const monthDates = Array.from(dataMap.keys())
                .filter(d => d.startsWith(`${monthPrefix}-`))
                .sort();

            // For the current ongoing month, clamp to the newest record so trailing future days aren't zeroes
            if (isCurrentOrFutureMonth && monthDates.length > 0) {
                end = monthDates[monthDates.length - 1];
            }
        }

        return {
            start: first,
            end: end,
            label: date.toLocaleDateString(undefined, { month: 'long', year: 'numeric' })
        };
    }

    if (mode === 'week') {
        const dayOfWeek = (date.getDay() + 6) % 7; // 0 is Mon, 6 is Sun
        const monday = new Date(date);
        monday.setDate(date.getDate() - dayOfWeek);
        const sunday = new Date(monday);
        sunday.setDate(monday.getDate() + 6);

        const mondayStr = toISODate(monday);
        const sundayStr = toISODate(sunday);
        let end = sundayStr;

        const todayStr = toISODate(new Date());
        const isCurrentOrFutureWeek = sundayStr >= todayStr;

        if (dataMap && dataMap.size > 0) {
            const weekDates = Array.from(dataMap.keys())
                .filter(d => d >= mondayStr && d <= sundayStr)
                .sort();

            if (isCurrentOrFutureWeek && weekDates.length > 0) {
                end = weekDates[weekDates.length - 1];
            }
        }

        return {
            start: mondayStr,
            end: end,
            label: `${formatDateDisplay(monday)} – ${formatDateDisplay(sunday)} (Week)`
        };
    }

    // Default: Day
    return {
        start: dateStr,
        end: dateStr,
        label: formatDateDisplay(date)
    };
}

/**
 * Highlights all SVG heatmap cells within the specified date range.
 * @param {string} startDate 
 * @param {string} endDate 
 */
export function highlightRange(startDate, endDate) {
    document.querySelectorAll('.heatmap-cell').forEach(cell => {
        const d = cell.getAttribute('data-date');
        if (d && d >= startDate && d <= endDate) {
            cell.classList.add('is-range-selected');
        } else {
            cell.classList.remove('is-range-selected');
        }
    });
}

/**
 * Applies the range selection to the heatmap: calculates total characters, updates info label safely,
 * highlights cells, and dispatches the 'kiseki:rangeSelected' custom event.
 * @param {string} dateStr 
 * @param {Map<string, number>} dataMap 
 * @param {string} [mode] 
 * @returns {{ start: string, end: string, label: string, charactersRead: number }}
 */
export function applySelection(dateStr, dataMap, mode) {
    if (!dateStr) return null;

    const currentMode = mode || document.querySelector('input[name="heatmapRangeMode"]:checked')?.value || 'day';
    let range;
    if (currentMode === 'all') {
        const dates = Array.from(dataMap.keys()).sort();
        range = {
            start: dates[0] ?? dateStr,
            end: dates[dates.length - 1] ?? dateStr,
            label: 'All Time'
        };
    } else {
        range = getSelectedRange(dateStr, currentMode, dataMap);
    }

    let rangeChars = 0;
    for (const [d, val] of dataMap.entries()) {
        if (d >= range.start && d <= range.end) {
            rangeChars += val;
        }
    }

    highlightRange(range.start, range.end);

    const infoEl = document.getElementById('heatmapSelectedInfo');
    if (infoEl) {
        const strong = document.createElement('strong');
        strong.textContent = range.label;

        const statSpan = document.createElement('span');
        statSpan.className = 'stat-highlight';
        statSpan.textContent = rangeChars.toLocaleString();

        infoEl.replaceChildren(
            strong,
            document.createTextNode(' • '),
            statSpan,
            document.createTextNode(' characters read')
        );
    }

    const detail = {
        startDate: range.start,
        endDate: range.end,
        mode: currentMode,
        charactersRead: rangeChars
    };

    document.dispatchEvent(new CustomEvent('kiseki:rangeSelected', { detail }));

    return {
        ...range,
        charactersRead: rangeChars
    };
}
