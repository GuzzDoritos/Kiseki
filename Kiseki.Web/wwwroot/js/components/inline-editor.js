import { getAntiForgeryToken } from '../core/csrf.js';

/**
 * Initializes inline editing for editable book fields (e.g. title, total character count).
 */
export function initInlineEditors() {
    const editContainers = document.querySelectorAll('[data-inline-edit]');
    if (editContainers.length === 0) return;

    editContainers.forEach((container) => {
        const fieldType = container.dataset.inlineEdit;
        const targetUrl = container.dataset.url;
        const displayEl = container.querySelector('[data-inline-display]');
        const editorEl = container.querySelector('[data-inline-editor]');
        const inputEl = container.querySelector('[data-inline-input]');
        const saveBtn = container.querySelector('[data-inline-save]');
        const errorEl = container.querySelector('[data-inline-error]');
        const textEl = container.querySelector('[data-inline-text]');

        if (!displayEl || !editorEl || !inputEl || !saveBtn || !textEl) return;

        function enterEditMode() {
            clearError();
            displayEl.hidden = true;
            editorEl.hidden = false;
            inputEl.value = inputEl.dataset.initialValue || '';
            inputEl.disabled = false;
            saveBtn.disabled = false;
            inputEl.focus();
            inputEl.select();
        }

        function cancelEditMode() {
            clearError();
            inputEl.value = inputEl.dataset.initialValue || '';
            editorEl.hidden = true;
            displayEl.hidden = false;
        }

        function showError(msg) {
            if (errorEl) {
                errorEl.textContent = msg;
                errorEl.hidden = false;
            }
            inputEl.classList.add('has-error');
            inputEl.focus();
        }

        function clearError() {
            if (errorEl) {
                errorEl.textContent = '';
                errorEl.hidden = true;
            }
            inputEl.classList.remove('has-error');
        }

        async function handleSave() {
            clearError();
            const rawValue = inputEl.value;

            let payload;
            if (fieldType === 'title') {
                const trimmed = rawValue.trim();
                if (trimmed.length === 0) {
                    showError('Title cannot be empty.');
                    return;
                }
                if (trimmed.length > 500) {
                    showError('Title cannot exceed 500 characters.');
                    return;
                }
                if (trimmed === (inputEl.dataset.initialValue || '').trim()) {
                    cancelEditMode();
                    return;
                }
                payload = { title: trimmed };
            } else if (fieldType === 'characterTotal') {
                const sanitized = rawValue.replace(/,/g, '').trim();
                let parsedCount = null;
                if (sanitized.length > 0) {
                    if (!/^\d+$/.test(sanitized)) {
                        showError('Enter a valid number or leave blank.');
                        return;
                    }
                    parsedCount = parseInt(sanitized, 10);
                    if (parsedCount < 0) {
                        showError('Character total cannot be negative.');
                        return;
                    }
                }
                const initialSanitized = (inputEl.dataset.initialValue || '').replace(/,/g, '').trim();
                const initialCount = initialSanitized.length > 0 ? parseInt(initialSanitized, 10) : null;
                if (parsedCount === initialCount) {
                    cancelEditMode();
                    return;
                }
                payload = { manualCharacterCount: parsedCount };
            } else {
                return;
            }

            inputEl.disabled = true;
            saveBtn.disabled = true;

            try {
                const token = getAntiForgeryToken();
                const response = await fetch(targetUrl, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'Accept': 'application/json',
                        'RequestVerificationToken': token
                    },
                    body: JSON.stringify(payload)
                });

                if (!response.ok) {
                    let errMsg = 'Saving failed. Please try again.';
                    try {
                        const errData = await response.json();
                        if (errData && errData.message) {
                            errMsg = errData.message;
                        }
                    } catch {
                        // Use default error message
                    }
                    inputEl.disabled = false;
                    saveBtn.disabled = false;
                    showError(errMsg);
                    return;
                }

                const data = await response.json();

                if (fieldType === 'title') {
                    textEl.textContent = data.title;
                    inputEl.dataset.initialValue = data.title;
                    inputEl.value = data.title;

                    const breadcrumb = document.querySelector('[data-breadcrumb-title]');
                    if (breadcrumb) {
                        breadcrumb.textContent = data.title;
                    }
                    document.title = `${data.title} - Kiseki`;
                } else if (fieldType === 'characterTotal') {
                    textEl.textContent = data.formattedTotalCharacters;
                    const newInitialVal = data.manualOverride != null ? data.manualOverride.toString() : (data.totalCharacters > 0 ? data.totalCharacters.toString() : '');
                    inputEl.dataset.initialValue = newInitialVal;
                    inputEl.value = newInitialVal;

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

                    // Update metadata aside source
                    const sourceEl = document.querySelector('[data-metadata-source]');
                    if (sourceEl) {
                        sourceEl.textContent = data.characterTotalSource;
                    }

                    // Update status badge & aside status if returned
                    if (data.statusLabel && data.statusCssClass) {
                        const badge = document.querySelector('[data-status-toggle]');
                        if (badge) {
                            const badgeText = badge.querySelector('[data-status-text]');
                            if (badgeText) badgeText.textContent = data.statusLabel;
                            badge.className = `media-status media-status-${data.statusCssClass} status-toggle-badge`;
                            badge.setAttribute('aria-label', `Status: ${data.statusLabel}. Click to toggle.`);
                        }
                        const metaStatus = document.querySelector('[data-metadata-status]');
                        if (metaStatus) {
                            metaStatus.textContent = data.statusLabel;
                        }
                    }

                    // Update manual row if present or manage its visibility
                    const manualRow = document.querySelector('[data-metadata-manual-row]');
                    const manualTotal = document.querySelector('[data-metadata-manual-total]');
                    if (data.manualOverride != null) {
                        if (manualTotal) {
                            manualTotal.textContent = Number(data.manualOverride).toLocaleString('en-US');
                        }
                        if (manualRow) {
                            manualRow.hidden = false;
                        }
                    } else if (manualRow) {
                        manualRow.hidden = true;
                    }
                }

                editorEl.hidden = true;
                displayEl.hidden = false;
            } catch {
                inputEl.disabled = false;
                saveBtn.disabled = false;
                showError('Network error. Please check your connection.');
            }
        }

        // Clicking anywhere on display enters edit mode
        displayEl.addEventListener('click', () => {
            enterEditMode();
        });

        // Save button
        saveBtn.addEventListener('click', (e) => {
            e.preventDefault();
            e.stopPropagation();
            handleSave();
        });

        // Keyboard navigation
        inputEl.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') {
                e.preventDefault();
                handleSave();
            } else if (e.key === 'Escape') {
                e.preventDefault();
                cancelEditMode();
            }
        });

        // Clear error on input
        inputEl.addEventListener('input', () => {
            if (inputEl.classList.contains('has-error')) {
                clearError();
            }
        });
    });
}
