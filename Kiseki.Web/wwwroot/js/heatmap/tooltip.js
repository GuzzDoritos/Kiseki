/**
 * Initializes hover tooltip behavior for heatmap cells.
 * @param {HTMLElement} container 
 * @param {HTMLElement} tooltip 
 * @param {HTMLElement|null} scrollContainer 
 */
export function initHeatmapTooltip(container, tooltip, scrollContainer) {
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

        const strong = document.createElement('strong');
        strong.textContent = formatted;

        tooltip.replaceChildren(
            strong,
            document.createElement('br'),
            document.createTextNode(`${count.toLocaleString()} characters`)
        );
        tooltip.classList.add('is-visible');
        updateTooltipPosition(cell);
    });

    container.addEventListener('mouseout', (e) => {
        if (e.target.closest('.heatmap-cell')) {
            currentHoveredCell = null;
            tooltip.classList.remove('is-visible');
        }
    });

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
