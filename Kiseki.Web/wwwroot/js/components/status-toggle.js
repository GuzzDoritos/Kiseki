import { getAntiForgeryToken } from '../core/csrf.js';

/**
 * Initializes the status toggle badge on book details pages.
 * Clicking toggles between Reading and Completed status asynchronously.
 */
export function initStatusToggle() {
    const statusBtn = document.querySelector('[data-status-toggle]');
    if (!statusBtn) return;

    statusBtn.addEventListener('click', async (e) => {
        e.preventDefault();
        const url = statusBtn.dataset.url;
        if (!url || statusBtn.disabled) return;

        statusBtn.disabled = true;

        try {
            const token = getAntiForgeryToken();
            const response = await fetch(url, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'Accept': 'application/json',
                    'RequestVerificationToken': token
                }
            });

            if (!response.ok) {
                statusBtn.disabled = false;
                return;
            }

            const data = await response.json();
            statusBtn.disabled = false;

            // Update badge text and classes
            const statusText = statusBtn.querySelector('[data-status-text]');
            if (statusText) {
                statusText.textContent = data.statusLabel;
            }
            statusBtn.className = `media-status media-status-${data.statusCssClass} status-toggle-badge`;
            statusBtn.setAttribute('aria-label', `Status: ${data.statusLabel}. Click to toggle.`);

            // Update metadata aside status
            const metadataStatus = document.querySelector('[data-metadata-status]');
            if (metadataStatus) {
                metadataStatus.textContent = data.statusLabel;
            }

            // Update progress bar
            const progressWrap = document.querySelector('.work-progress');
            if (progressWrap) {
                const copySpan = progressWrap.querySelector('.progress-copy > span:first-child');
                if (copySpan) {
                    copySpan.textContent = data.progressLabel;
                }

                const percentageSpan = progressWrap.querySelector('.progress-percentage');
                if (percentageSpan) {
                    percentageSpan.textContent = `${data.formattedProgressPercentage}%`;
                }

                const progressTrack = progressWrap.querySelector('.progress-track');
                if (progressTrack) {
                    progressTrack.setAttribute('aria-valuenow', data.formattedProgressPercentage);
                }

                const progressFill = progressWrap.querySelector('.progress-fill');
                if (progressFill) {
                    progressFill.style.width = `${data.formattedProgressPercentage}%`;
                    if (data.isCompleted) {
                        progressFill.classList.add('complete');
                    } else {
                        progressFill.classList.remove('complete');
                    }
                }
            }
        } catch {
            statusBtn.disabled = false;
        }
    });
}
