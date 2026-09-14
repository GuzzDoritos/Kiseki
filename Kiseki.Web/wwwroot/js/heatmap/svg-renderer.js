import {
    MONTH_NAMES,
    WEEKDAY_NAMES,
    CELL_SIZE,
    STEP,
    LEFT_MARGIN,
    TOP_MARGIN,
    toISODate,
    getLevel
} from './calendar-math.js';

const SVG_NS = 'http://www.w3.org/2000/svg';

/**
 * Creates an SVG heatmap element representing a single year.
 * @param {number} year 
 * @param {Map<string, number>} dataMap 
 * @param {boolean} [showYearLabel=false] 
 * @returns {SVGSVGElement}
 */
export function createYearSvg(year, dataMap, showYearLabel = false) {
    const svg = document.createElementNS(SVG_NS, 'svg');
    const jan1 = new Date(year, 0, 1);
    const dec31 = new Date(year, 11, 31);
    const startDayOfWeek = (jan1.getDay() + 6) % 7; // 0 = Mon, 6 = Sun
    const startDate = new Date(year, 0, 1 - startDayOfWeek);

    const totalWeeks = Math.ceil((Math.round((dec31 - startDate) / 86400000) + 1) / 7);
    const svgWidth = LEFT_MARGIN + totalWeeks * STEP + 16;
    const svgHeight = TOP_MARGIN + 7 * STEP + 8;

    svg.setAttribute('width', String(svgWidth));
    svg.setAttribute('height', String(svgHeight));
    svg.setAttribute('class', 'heatmap-year-svg');
    svg.setAttribute('role', 'img');
    svg.setAttribute('aria-label', `Activity heatmap for ${year}`);

    // Year Title if in multi-year view
    if (showYearLabel) {
        const yearText = document.createElementNS(SVG_NS, 'text');
        yearText.setAttribute('x', '0');
        yearText.setAttribute('y', '14');
        yearText.setAttribute('class', 'heatmap-year-title');
        yearText.textContent = String(year);
        svg.appendChild(yearText);
    }

    // Weekday labels (Mon, Wed, Fri)
    const weekdayIndices = [0, 2, 4];
    weekdayIndices.forEach((wIdx, i) => {
        const text = document.createElementNS(SVG_NS, 'text');
        text.setAttribute('x', '0');
        text.setAttribute('y', String(TOP_MARGIN + wIdx * STEP + 10));
        text.setAttribute('class', 'heatmap-weekday-text');
        text.textContent = WEEKDAY_NAMES[i];
        svg.appendChild(text);
    });

    // Month labels
    let lastMonthCol = -10;
    for (let m = 0; m < 12; m++) {
        const firstOfMonth = new Date(year, m, 1);
        const diffDays = Math.round((firstOfMonth - startDate) / 86400000);
        const col = Math.floor(diffDays / 7);

        // Avoid overlapping labels
        if (col > lastMonthCol + 2 && col < totalWeeks - 1) {
            const text = document.createElementNS(SVG_NS, 'text');
            text.setAttribute('x', String(LEFT_MARGIN + col * STEP));
            text.setAttribute('y', '15');
            text.setAttribute('class', 'heatmap-month-text');
            text.textContent = MONTH_NAMES[m];
            svg.appendChild(text);
            lastMonthCol = col;
        }
    }

    // Day cells
    let curr = new Date(startDate);
    while (curr <= dec31 || (curr.getDay() + 6) % 7 !== 0) {
        if (curr > dec31 && (curr.getDay() + 6) % 7 === 0) break;

        const diffDays = Math.round((curr - startDate) / 86400000);
        const col = Math.floor(diffDays / 7);
        const row = (curr.getDay() + 6) % 7;

        if (curr.getFullYear() === year) {
            const dateStr = toISODate(curr);
            const count = dataMap.get(dateStr) || 0;
            const level = getLevel(count);

            const rect = document.createElementNS(SVG_NS, 'rect');
            rect.setAttribute('x', String(LEFT_MARGIN + col * STEP));
            rect.setAttribute('y', String(TOP_MARGIN + row * STEP));
            rect.setAttribute('width', String(CELL_SIZE));
            rect.setAttribute('height', String(CELL_SIZE));
            rect.setAttribute('rx', '2');
            rect.setAttribute('ry', '2');
            rect.setAttribute('class', 'heatmap-cell');
            rect.setAttribute('data-date', dateStr);
            rect.setAttribute('data-count', String(count));
            rect.setAttribute('data-level', String(level));

            svg.appendChild(rect);
        }

        curr.setDate(curr.getDate() + 1);
    }

    return svg;
}

/**
 * Renders the heatmap SVG(s) inside the container based on the selected period.
 * @param {HTMLElement} container 
 * @param {HTMLElement|null} scrollContainer 
 * @param {string} period 
 * @param {number[]} availableYears 
 * @param {Map<string, number>} dataMap 
 */
export function renderHeatmap(container, scrollContainer, period, availableYears, dataMap) {
    if (!container) return;

    // Securely clear existing children without using innerHTML
    container.replaceChildren();

    if (period === 'all') {
        const yearsToRender = availableYears.slice().sort((a, b) => a - b);
        if (yearsToRender.length === 0) {
            yearsToRender.push(new Date().getFullYear());
        }

        const multiYearWrapper = document.createElement('div');
        multiYearWrapper.className = 'heatmap-multi-year';

        yearsToRender.forEach(yr => {
            const yearBlock = document.createElement('div');
            yearBlock.className = 'heatmap-year-block';
            const svg = createYearSvg(yr, dataMap, true);
            yearBlock.appendChild(svg);
            multiYearWrapper.appendChild(yearBlock);
        });

        container.appendChild(multiYearWrapper);

        // Auto-scroll to far right so user immediately sees recent reading
        if (scrollContainer) {
            requestAnimationFrame(() => {
                scrollContainer.scrollLeft = scrollContainer.scrollWidth;
            });
        }
    } else {
        const year = parseInt(period, 10) || new Date().getFullYear();
        const svg = createYearSvg(year, dataMap, false);
        container.appendChild(svg);
    }
}
