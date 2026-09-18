/**
 * Handles client-side persistence of the Auto-match metadata checkbox.
 * Stores preference in localStorage ('kiseki_auto_match_metadata').
 * Defaults to unchecked if no preference exists.
 */
const STORAGE_KEY = 'kiseki_auto_match_metadata';

export function initTtsuAutoMatch() {
    const checkbox = document.querySelector('[data-ttsu-auto-match]');
    if (!checkbox) return;

    try {
        const stored = localStorage.getItem(STORAGE_KEY);
        if (stored !== null) {
            checkbox.checked = stored === 'true';
        } else {
            checkbox.checked = false;
        }
    } catch {
        // Handle blocked/disabled localStorage gracefully
    }

    checkbox.addEventListener('change', () => {
        try {
            localStorage.setItem(STORAGE_KEY, checkbox.checked ? 'true' : 'false');
        } catch {
            // Handle blocked/disabled localStorage gracefully
        }
    });
}
