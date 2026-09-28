/**
 * Initializes the Series Details view toggle (List vs. Covers grid).
 * Supports instant client-side toggling without page reload and preserves
 * preference across sessions via cookie and localStorage.
 */
export function initSeriesViewToggle() {
    const radios = document.querySelectorAll('input[name="SeriesView"]');
    if (!radios.length) return;

    const listView = document.getElementById('seriesInstallmentsList');
    const coversView = document.getElementById('seriesCoversGrid');
    if (!listView || !coversView) return;

    function applyView(viewMode) {
        const isCovers = viewMode === 'covers';

        listView.hidden = isCovers;
        coversView.hidden = !isCovers;
        listView.style.display = isCovers ? 'none' : 'flex';
        coversView.style.display = isCovers ? 'grid' : 'none';

        radios.forEach(radio => {
            radio.checked = radio.value === (isCovers ? 'covers' : 'list');
        });

        // Persist preference
        try {
            localStorage.setItem('kiseki_series_details_view', isCovers ? 'covers' : 'list');
        } catch {
            // localStorage not available
        }

        document.cookie = `kiseki_series_details_view=${isCovers ? 'covers' : 'list'}; path=/; max-age=31536000; SameSite=Lax`;

        // Update URL query parameter without reloading
        const url = new URL(window.location.href);
        url.searchParams.set('View', isCovers ? 'covers' : 'list');
        window.history.replaceState(null, '', url.toString());
    }

    // Ensure display styles match currently checked radio on load
    const checkedRadio = Array.from(radios).find(r => r.checked);
    if (checkedRadio) {
        const isCovers = checkedRadio.value === 'covers';
        listView.style.display = isCovers ? 'none' : 'flex';
        coversView.style.display = isCovers ? 'grid' : 'none';
        listView.hidden = isCovers;
        coversView.hidden = !isCovers;
    }

    radios.forEach(radio => {
        radio.addEventListener('change', () => {
            if (radio.checked) {
                applyView(radio.value);
            }
        });
    });
}
