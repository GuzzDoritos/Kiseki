import { renderHeatmap } from './heatmap/svg-renderer.js';
import { applySelection } from './heatmap/range-selector.js';
import { initHeatmapTooltip } from './heatmap/tooltip.js';

/**
 * Initializes the immersion activity heatmap.
 */
export function initHeatmap() {
    let dataMap = new Map();
    let availableYears = [];
    let lastSelectedDate = null;

    const dataElement = document.getElementById('heatmap-data');
    if (dataElement) {
        try {
            const rawData = JSON.parse(dataElement.textContent || '[]');
            dataMap = new Map(rawData.map(item => [item.date, item.value]));
        } catch {
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

    const container = document.getElementById('heatmap-svg-container');
    const scrollContainer = document.getElementById('heatmap-scroll');
    const tooltip = document.getElementById('heatmap-tooltip');

    function render(period) {
        if (!container) return;
        renderHeatmap(container, scrollContainer, period, availableYears, dataMap);

        if (lastSelectedDate) {
            applySelection(lastSelectedDate, dataMap);
        }
    }

    const initialPeriod = yearSelect ? yearSelect.value : String(new Date().getFullYear());
    render(initialPeriod);

    if (container && tooltip) {
        initHeatmapTooltip(container, tooltip, scrollContainer);
    }

    if (yearSelect) {
        yearSelect.addEventListener('change', (e) => {
            render(e.target.value);
        });
    }

    if (container) {
        container.addEventListener('click', (e) => {
            const cell = e.target.closest('.heatmap-cell');
            if (!cell) return;
            const dateStr = cell.getAttribute('data-date');
            if (dateStr) {
                lastSelectedDate = dateStr;
                applySelection(dateStr, dataMap);
            }
        });
    }

    document.querySelectorAll('input[name="heatmapRangeMode"]').forEach(radio => {
        radio.addEventListener('change', () => {
            if (lastSelectedDate) {
                applySelection(lastSelectedDate, dataMap);
            }
        });
    });
}

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initHeatmap);
} else {
    initHeatmap();
}
