import { toChartData } from './reading-volume/data.js';

const rangeLabels = {
    day: 'Daily',
    week: 'Weekly',
    weekly: 'Weekly',
    month: 'Monthly',
    monthly: 'Monthly',
    year: 'Yearly',
    yearly: 'Yearly',
    all: 'All Time'
};

export function initReadingVolumeChart() {
    const container = document.getElementById('reading-volume-chart');
    const title = document.getElementById('reading-volume-title');
    const dataElement = document.getElementById('heatmap-data');

    if (!container || !title || !dataElement || !window.uPlot) return;

    let rawData;
    try {
        rawData = JSON.parse(dataElement.textContent || '[]');
    } catch {
        rawData = [];
    }

    const MONTH_NAMES_SHORT = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
    const MONTH_NAMES_FULL = [
        'January', 'February', 'March', 'April', 'May', 'June',
        'July', 'August', 'September', 'October', 'November', 'December'
    ];
    const WEEKDAY_NAMES_SHORT = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];

    let currentMode = 'year';
    let currentLabels = null;
    let currentStartDate = null;
    let currentYear = new Date().getFullYear();

    const yearSelect = document.getElementById('heatmapYearSelect');
    if (yearSelect && yearSelect.value && yearSelect.value !== 'all') {
        currentYear = parseInt(yearSelect.value, 10) || currentYear;
        yearSelect.addEventListener('change', (e) => {
            if (e.target.value && e.target.value !== 'all') {
                currentYear = parseInt(e.target.value, 10) || currentYear;
            }
        });
    }

    function formatCursorDate(val) {
        if (currentMode === 'all') {
            return currentLabels ? (currentLabels[val] ?? '--') : '--';
        }

        if (currentMode === 'year') {
            if (val >= 1 && val <= 12) {
                return `${MONTH_NAMES_FULL[val - 1]} ${currentYear}`;
            }
            return '--';
        }

        if (currentMode === 'month') {
            if (currentStartDate) {
                const start = new Date(`${currentStartDate}T00:00:00Z`);
                const m = start.getUTCMonth();
                const y = start.getUTCFullYear();
                return `${MONTH_NAMES_SHORT[m]} ${val}, ${y}`;
            }
            return `Day ${val}`;
        }

        if (currentMode === 'week') {
            if (currentStartDate && val >= 1 && val <= 7) {
                const start = new Date(`${currentStartDate}T00:00:00Z`);
                start.setUTCDate(start.getUTCDate() + (val - 1));
                const dayName = WEEKDAY_NAMES_SHORT[val - 1];
                const m = MONTH_NAMES_SHORT[start.getUTCMonth()];
                const d = start.getUTCDate();
                return `${dayName}, ${m} ${d}`;
            }
            return `Day ${val}`;
        }

        if (currentMode === 'day' && currentStartDate) {
            const d = new Date(`${currentStartDate}T00:00:00Z`);
            return `${MONTH_NAMES_SHORT[d.getUTCMonth()]} ${d.getUTCDate()}, ${d.getUTCFullYear()}`;
        }

        return String(val);
    }

    const options = {
        width: 800,
        height: 300,
        scales: {
            x: {
                time: false,
                auto: false
            },
            y: {
                range: (u, dataMin, dataMax) => [
                    0,
                    Math.max(1, dataMax ?? 0)
                ]
            },
            speed: {
                range: (u, dataMin, dataMax) => [
                    0,
                    Math.max(100, dataMax ?? 0)
                ]
            }
        },
        axes: [
            {
                stroke: '#888',
                grid: { stroke: '#222' },
                splits: (u, axisIdx, min, max) => {
                    if (currentMode === 'all') {
                        const total = Math.floor(max) - Math.ceil(min) + 1;
                        const step = total > 24 ? 3 : total > 12 ? 2 : 1;
                        const ticks = [];
                        for (let i = 1; i <= max; i += step) {
                            ticks.push(i);
                        }
                        return ticks;
                    }

                    if (currentMode === 'month') {
                        const ticks = [];
                        for (let day = 1; day <= max; day += 7) {
                            ticks.push(day);
                        }
                        return ticks;
                    }

                    const ticks = [];
                    const start = Math.ceil(min);
                    const end = Math.floor(max);
                    for (let i = start; i <= end; i++) {
                        ticks.push(i);
                    }
                    return ticks;
                },
                values: (u, vals) => {
                    if (currentMode === 'all' && currentLabels) {
                        return vals.map(v => currentLabels[Math.round(v)] ?? '');
                    }
                    return vals.map(v => Math.round(v));
                }
            },
            {
                scale: 'y',
                side: 3,
                stroke: '#888',
                grid: { stroke: '#222' },
                values: (u, values) => values.map(value => value >= 1000 ? `${value / 1000}k` : value)
            },
            {
                scale: 'speed',
                side: 1,
                stroke: '#e5a00d',
                grid: { show: false },
                values: (u, values) => values.map(value => value >= 1000 ? `${Math.round(value / 1000)}k/h` : `${Math.round(value)}/h`)
            }
        ],
        series: [
            {
                label: 'Month',
                value: (u, rawVal) => {
                    if (rawVal == null) return '--';
                    return formatCursorDate(Math.round(rawVal));
                }
            },
            {
                label: 'Characters',
                scale: 'y',
                stroke: '#ff3b30',
                width: 2,
                value: (u, rawVal) => (rawVal != null ? rawVal.toLocaleString() : '--')
            },
            {
                label: 'Speed',
                scale: 'speed',
                stroke: '#e5a00d',
                width: 2,
                spanGaps: true,
                value: (u, rawVal) => (rawVal != null ? `${Math.round(rawVal).toLocaleString()} ch/h` : '--')
            }
        ]
    };

    const initialData = toChartData(rawData, 'year');
    const chart = new window.uPlot(options, initialData, container);

    document.addEventListener('kiseki:rangeSelected', event => {
        const { startDate, endDate, mode } = event.detail;
        currentMode = mode;
        currentStartDate = startDate;
        if (startDate) {
            currentYear = parseInt(startDate.substring(0, 4), 10) || currentYear;
        }

        const selectedData = rawData.filter(item => item.date >= startDate && item.date <= endDate);
        const chartData = toChartData(selectedData, mode, startDate, endDate);
        currentLabels = chartData.labels || null;

        title.textContent = `${rangeLabels[mode] ?? 'Selected Range'} Reading Volume`;

        const labelEl = container.querySelector('.u-legend .u-series:first-child .u-label');
        if (labelEl) {
            labelEl.textContent = (mode === 'year' || mode === 'all') ? 'Month' : 'Date';
        }

        let xMin = 1;
        let xMax = chartData[0].length || 1;

        if (mode === 'all') {
            xMin = 1;
            xMax = chartData[0].length || 1;
        } else if (mode === 'year') {
            xMin = 1;
            xMax = 12;
        } else if (mode === 'month') {
            xMin = 1;
            xMax = chartData[0].length;
        } else if (mode === 'week') {
            xMin = 1;
            xMax = 7;
        }

        chart.batch(() => {
            chart.setData(chartData, false);
            chart.setScale('x', {
                min: xMin,
                max: xMax
            });
        });
    });
}

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initReadingVolumeChart);
} else {
    initReadingVolumeChart();
}
