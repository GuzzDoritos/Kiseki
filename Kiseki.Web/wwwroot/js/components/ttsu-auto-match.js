import { getAntiForgeryToken } from '../core/csrf.js';

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

/**
 * Manages incremental, resumable automatic metadata matching progress.
 * Sequentially requests enrichment for pending books, updating the progress UI
 * and polite live region until all books have been processed, then submits the review form.
 */
export function initTtsuAutoMatchProgress() {
    const container = document.querySelector('[data-ttsu-auto-match-progress]');
    if (!container) return;

    const batchId = container.dataset.batchId;
    if (!batchId) return;

    const totalCount = parseInt(container.dataset.total || '0', 10);
    const initialCompleted = parseInt(container.dataset.completed || '0', 10);

    const spinner = container.querySelector('[data-ttsu-progress-spinner]');
    const statusEl = container.querySelector('[data-ttsu-progress-status]');
    const summaryEl = container.querySelector('[data-ttsu-progress-summary]');
    const currentTitleEl = container.querySelector('[data-ttsu-progress-current-title]');
    const secondaryEl = container.querySelector('[data-ttsu-progress-secondary]');
    const progressBar = container.querySelector('[data-ttsu-progress-bar]');
    const liveRegion = container.querySelector('[data-ttsu-progress-live]');
    const errorContainer = container.querySelector('[data-ttsu-progress-error]');
    const errorMessageEl = container.querySelector('[data-ttsu-progress-error-message]');
    const resumeBtn = container.querySelector('[data-ttsu-progress-resume]');
    const actionBtns = document.querySelectorAll('[data-ttsu-action-btn]');
    const reviewForm = document.getElementById('ttsuReviewForm');

    let isRunning = false;
    let abortController = null;

    function updateProgress(processed, total, currentNumber, isComplete, secondaryText, currentTitle) {
        const pct = total > 0 ? Math.min(100, Math.round((processed / total) * 100)) : 0;

        if (progressBar) {
            progressBar.setAttribute('aria-valuenow', processed.toString());
            progressBar.setAttribute('aria-valuemax', total.toString());
            progressBar.style.width = `${pct}%`;
        }

        if (summaryEl) {
            summaryEl.textContent = `Looking up metadata and covers — ${processed} of ${total} books checked`;
        }

        if (statusEl) {
            if (isComplete) {
                statusEl.textContent = `Finished checking ${total} books. Loading review…`;
            } else {
                statusEl.textContent = `Looking up metadata and covers — ${currentNumber} of ${total} books checked`;
            }
        }

        if (currentTitleEl) {
            currentTitleEl.textContent = currentTitle || '';
        }

        if (secondaryEl && secondaryText) {
            secondaryEl.textContent = secondaryText;
        }

        if (liveRegion) {
            liveRegion.textContent = `Looking up metadata and covers — ${processed} of ${total} books checked`;
        }
    }

    function showError(message) {
        if (errorContainer) {
            errorContainer.hidden = false;
        }
        if (errorMessageEl) {
            errorMessageEl.textContent = message || 'A network error occurred while matching metadata. Completed progress is saved.';
        }
        if (spinner) {
            spinner.classList.add('is-paused');
        }
    }

    function hideError() {
        if (errorContainer) {
            errorContainer.hidden = true;
        }
        if (spinner) {
            spinner.classList.remove('is-paused');
        }
    }

    async function fetchNext() {
        if (!isRunning) return;

        abortController = new AbortController();
        const token = getAntiForgeryToken();

        try {
            const url = `?handler=EnrichNext&batchId=${encodeURIComponent(batchId)}`;
            const response = await fetch(url, {
                method: 'POST',
                headers: {
                    'RequestVerificationToken': token,
                    'X-Requested-With': 'XMLHttpRequest',
                    'Content-Type': 'application/x-www-form-urlencoded'
                },
                body: new URLSearchParams({ batchId }).toString(),
                signal: abortController.signal
            });

            if (!response.ok) {
                isRunning = false;
                showError(`Server returned an error (${response.status}). Click Resume lookup to retry.`);
                return;
            }

            const data = await response.json();
            const processed = data.processed ?? 0;
            const total = data.total ?? totalCount;
            const complete = data.complete === true;
            const currentNum = data.currentNumber ?? (processed + 1);
            const secondaryText = data.googleSecondaryText;

            updateProgress(processed, total, currentNum, complete, secondaryText, data.currentTitle);

            if (complete) {
                isRunning = false;
                if (spinner) {
                    spinner.hidden = true;
                }
                // Re-enable action buttons
                actionBtns.forEach(btn => btn.removeAttribute('disabled'));

                // Submit review flow so the page is rendered from the complete server-held batch
                if (reviewForm) {
                    reviewForm.action = '?handler=Review&fromEnrichment=true';
                    reviewForm.submit();
                }
                return;
            }

            // Continue loop with next pending book
            fetchNext();
        } catch (err) {
            if (err.name === 'AbortError') return;
            isRunning = false;
            showError('Network connection was interrupted. Click Resume lookup to continue.');
        }
    }

    if (resumeBtn) {
        resumeBtn.addEventListener('click', () => {
            hideError();
            if (!isRunning) {
                isRunning = true;
                fetchNext();
            }
        });
    }

    // Start enrichment sequence
    isRunning = true;
    fetchNext();
}
