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

    const options = {
        width: 800,
        height: 300,
        axes: [
            {
                stroke: '#888',
                grid: { stroke: '#222' }
            },
            {
                stroke: '#888',
                grid: { stroke: '#222' },
                values: (u, values) => values.map(value => value >= 1000 ? `${value / 1000}k` : value)
            }
        ],
        series: [
            {},
            {
                label: 'Characters',
                stroke: '#ff3b30',
                width: 2
            }
        ]
    };

    const chart = new window.uPlot(options, toChartData(rawData), container);

    document.addEventListener('kiseki:rangeSelected', event => {
        const { startDate, endDate, mode } = event.detail;
        const selectedData = rawData.filter(item => item.date >= startDate && item.date <= endDate);
        let rangeStart = new Date(`${startDate}T00:00:00Z`);
        let rangeEnd = new Date(`${endDate}T00:00:00Z`);

        if (mode === 'all') {
            rangeStart = new Date(Date.UTC(rangeStart.getUTCFullYear(), 0, 1));
            rangeEnd = new Date(Date.UTC(rangeEnd.getUTCFullYear() + 1, 0, 1));
        } else {
            rangeEnd.setUTCDate(rangeEnd.getUTCDate() + 1);
        }

        title.textContent = `${rangeLabels[mode] ?? 'Selected Range'} Reading Volume`;

        chart.batch(() => {
            chart.setData(toChartData(selectedData, mode), false);
            chart.setScale('x', {
                min: rangeStart.getTime() / 1000,
                max: rangeEnd.getTime() / 1000
            });
        });
    });
}

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initReadingVolumeChart);
} else {
    initReadingVolumeChart();
}
