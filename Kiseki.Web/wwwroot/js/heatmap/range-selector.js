import { toISODate, formatDateDisplay } from './calendar-math.js';

/**
 * Calculates the date range (start, end, label) for a given date string and mode.
 * @param {string} dateStr 
 * @param {'day'|'week'|'month'|'year'} mode
 * @returns {{ start: string, end: string, label: string }}
 */
export function getSelectedRange(dateStr, mode) {
    const parts = dateStr.split('-').map(Number);
    const date = new Date(parts[0], parts[1] - 1, parts[2]);

    if (mode === 'year') {
        return {
            start: `${parts[0]}-01-01`,
            end: `${parts[0]}-12-31`,
            label: String(parts[0])
        };
    }

    if (mode === 'month') {
        const first = new Date(date.getFullYear(), date.getMonth(), 1);
        const last = new Date(date.getFullYear(), date.getMonth() + 1, 0);
        return {
            start: toISODate(first),
            end: toISODate(last),
            label: date.toLocaleDateString(undefined, { month: 'long', year: 'numeric' })
        };
    }

    if (mode === 'week') {
        const dayOfWeek = (date.getDay() + 6) % 7; // 0 is Mon, 6 is Sun
        const monday = new Date(date);
        monday.setDate(date.getDate() - dayOfWeek);
        const sunday = new Date(monday);
        sunday.setDate(monday.getDate() + 6);

        return {
            start: toISODate(monday),
            end: toISODate(sunday),
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
        range = getSelectedRange(dateStr, currentMode);
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
