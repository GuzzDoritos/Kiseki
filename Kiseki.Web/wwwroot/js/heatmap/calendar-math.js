export const MONTH_NAMES = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
export const WEEKDAY_NAMES = ['Mon', 'Wed', 'Fri'];
export const CELL_SIZE = 12;
export const CELL_GAP = 3;
export const STEP = CELL_SIZE + CELL_GAP; // 15px
export const LEFT_MARGIN = 32;
export const TOP_MARGIN = 24;

/**
 * Converts a Date object to an ISO date string (YYYY-MM-DD) in local time.
 * @param {Date} d 
 * @returns {string}
 */
export function toISODate(d) {
    const year = d.getFullYear();
    const month = String(d.getMonth() + 1).padStart(2, '0');
    const day = String(d.getDate()).padStart(2, '0');
    return `${year}-${month}-${day}`;
}

/**
 * Formats a Date object for display (e.g. "Sep 14, 2026").
 * @param {Date} d 
 * @returns {string}
 */
export function formatDateDisplay(d) {
    return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
}

/**
 * Determines the activity level (0-4) based on character count.
 * @param {number} count 
 * @returns {number}
 */
export function getLevel(count) {
    if (!count || count <= 0) return 0;
    if (count < 2500) return 1;
    if (count < 7500) return 2;
    if (count < 15000) return 3;
    return 4;
}
