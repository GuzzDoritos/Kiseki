(function () {
    let dataMap = new Map();
    let rawData = [];
    let availableYears = [];
    let lastSelectedDate = null;

    const MONTH_NAMES = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
    const WEEKDAY_NAMES = ['Mon', 'Wed', 'Fri'];
    const CELL_SIZE = 12;
    const CELL_GAP = 3;
    const STEP = CELL_SIZE + CELL_GAP; // 15px
    const LEFT_MARGIN = 32;
    const TOP_MARGIN = 24;

    function toISODate(d) {
        const year = d.getFullYear();
        const month = String(d.getMonth() + 1).padStart(2, '0');
        const day = String(d.getDate()).padStart(2, '0');
        return `${year}-${month}-${day}`;
    }

    function formatDateDisplay(d) {
        return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
    }

    function getLevel(count) {
        if (!count || count <= 0) return 0;
        if (count < 2500) return 1;
        if (count < 7500) return 2;
        if (count < 15000) return 3;
        return 4;
    }

    function getSelectedRange(dateStr, mode) {
        const parts = dateStr.split('-').map(Number);
        const date = new Date(parts[0], parts[1] - 1, parts[2]);

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

    function highlightRange(startDate, endDate) {
        document.querySelectorAll('.heatmap-cell').forEach(cell => {
            const d = cell.getAttribute('data-date');
            if (d && d >= startDate && d <= endDate) {
                cell.classList.add('is-range-selected');
            } else {
                cell.classList.remove('is-range-selected');
            }
        });
    }

    function applySelection(dateStr) {
        if (!dateStr) return;
        lastSelectedDate = dateStr;

        const mode = document.querySelector('input[name="heatmapRangeMode"]:checked')?.value || 'day';
        const range = getSelectedRange(dateStr, mode);

        let rangeChars = 0;
        for (const [d, val] of dataMap.entries()) {
            if (d >= range.start && d <= range.end) {
                rangeChars += val;
            }
        }

        highlightRange(range.start, range.end);

        const infoEl = document.getElementById('heatmapSelectedInfo');
        if (infoEl) {
            infoEl.innerHTML = `<strong>${range.label}</strong> • <span class="stat-highlight">${rangeChars.toLocaleString()}</span> characters read`;
        }

        document.dispatchEvent(new CustomEvent('kiseki:rangeSelected', {
            detail: {
                startDate: range.start,
                endDate: range.end,
                mode: mode,
                charactersRead: rangeChars
            }
        }));
    }

    function createYearSvg(year, showYearLabel = false) {
        const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
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
            const yearText = document.createElementNS('http://www.w3.org/2000/svg', 'text');
            yearText.setAttribute('x', '0');
            yearText.setAttribute('y', '14');
            yearText.setAttribute('class', 'heatmap-year-title');
            yearText.textContent = String(year);
            svg.appendChild(yearText);
        }

        // Weekday labels (Mon, Wed, Fri)
        const weekdayIndices = [0, 2, 4]; // Mon, Wed, Fri
        weekdayIndices.forEach((wIdx, i) => {
            const text = document.createElementNS('http://www.w3.org/2000/svg', 'text');
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
                const text = document.createElementNS('http://www.w3.org/2000/svg', 'text');
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

                const rect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
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

    function render(period) {
        const container = document.getElementById('heatmap-svg-container');
        const scrollContainer = document.getElementById('heatmap-scroll');
        if (!container) return;

        container.innerHTML = '';

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
                const svg = createYearSvg(yr, true);
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
            const svg = createYearSvg(year, false);
            container.appendChild(svg);
        }

        // Re-apply range selection if a date was selected
        if (lastSelectedDate) {
            applySelection(lastSelectedDate);
        }
    }

    function initTooltip() {
        const tooltip = document.getElementById('heatmap-tooltip');
        const container = document.getElementById('heatmap-svg-container');
        if (!tooltip || !container) return;

        let currentHoveredCell = null;

        function updateTooltipPosition(cell) {
            if (!cell) return;
            const cellRect = cell.getBoundingClientRect();
            const tooltipRect = tooltip.getBoundingClientRect();

            let x = cellRect.left + cellRect.width / 2;
            let y = cellRect.top - 8;
            let flipBelow = false;

            // If too close to viewport top, show below the cell
            if (y - tooltipRect.height < 8) {
                y = cellRect.bottom + 8;
                flipBelow = true;
            }

            // Keep horizontally within viewport (12px padding)
            const halfWidth = tooltipRect.width / 2;
            if (x - halfWidth < 12) {
                x = halfWidth + 12;
            } else if (x + halfWidth > window.innerWidth - 12) {
                x = window.innerWidth - halfWidth - 12;
            }

            tooltip.style.left = `${x}px`;
            tooltip.style.top = `${y}px`;
            tooltip.style.transform = flipBelow ? 'translate(-50%, 0)' : 'translate(-50%, -100%)';
        }

        container.addEventListener('mouseover', (e) => {
            const cell = e.target.closest('.heatmap-cell');
            if (!cell) return;
            currentHoveredCell = cell;

            const dateStr = cell.getAttribute('data-date');
            const count = parseInt(cell.getAttribute('data-count') || '0', 10);
            const parts = dateStr.split('-').map(Number);
            const d = new Date(parts[0], parts[1] - 1, parts[2]);
            const formatted = d.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric', year: 'numeric' });

            tooltip.innerHTML = `<strong>${formatted}</strong><br/>${count.toLocaleString()} characters`;
            tooltip.classList.add('is-visible');
            updateTooltipPosition(cell);
        });

        container.addEventListener('mouseout', (e) => {
            if (e.target.closest('.heatmap-cell')) {
                currentHoveredCell = null;
                tooltip.classList.remove('is-visible');
            }
        });

        const scrollContainer = document.getElementById('heatmap-scroll');
        if (scrollContainer) {
            scrollContainer.addEventListener('scroll', () => {
                if (currentHoveredCell) {
                    updateTooltipPosition(currentHoveredCell);
                } else {
                    tooltip.classList.remove('is-visible');
                }
            }, { passive: true });
        }

        window.addEventListener('scroll', () => {
            if (currentHoveredCell) {
                updateTooltipPosition(currentHoveredCell);
            } else {
                tooltip.classList.remove('is-visible');
            }
        }, { passive: true });
    }

    document.addEventListener('DOMContentLoaded', () => {
        const dataElement = document.getElementById('heatmap-data');
        if (dataElement) {
            try {
                rawData = JSON.parse(dataElement.textContent || '[]');
                dataMap = new Map(rawData.map(item => [item.date, item.value]));
            } catch {
                rawData = [];
                dataMap = new Map();
            }
        }

        const yearSelect = document.getElementById('heatmapYearSelect');
        if (yearSelect) {
            availableYears = Array.from(yearSelect.querySelectorAll('option'))
                .map(opt => parseInt(opt.value, 10))
                .filter(n => !isNaN(n));
        }

        if (availableYears.length === 0) {
            availableYears = [new Date().getFullYear()];
        }

        const initialPeriod = yearSelect ? yearSelect.value : String(new Date().getFullYear());
        render(initialPeriod);
        initTooltip();

        if (yearSelect) {
            yearSelect.addEventListener('change', (e) => {
                render(e.target.value);
            });
        }

        const container = document.getElementById('heatmap-svg-container');
        if (container) {
            container.addEventListener('click', (e) => {
                const cell = e.target.closest('.heatmap-cell');
                if (!cell) return;
                const dateStr = cell.getAttribute('data-date');
                applySelection(dateStr);
            });
        }

        document.querySelectorAll('input[name="heatmapRangeMode"]').forEach(radio => {
            radio.addEventListener('change', () => {
                if (lastSelectedDate) {
                    applySelection(lastSelectedDate);
                }
            });
        });
    });
})();
