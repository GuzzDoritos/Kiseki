import { getAntiForgeryToken } from '../core/csrf.js';

/**
 * Initializes the cover image editor modal on book details pages.
 */
export function initCoverEditor() {
    const trigger = document.querySelector('[data-cover-edit-trigger]');
    const backdrop = document.querySelector('[data-cover-edit-backdrop]');
    if (!trigger || !backdrop) return;

    const input = backdrop.querySelector('[data-cover-edit-input]');
    const saveButton = backdrop.querySelector('[data-cover-edit-save]');
    const closeButton = backdrop.querySelector('[data-cover-edit-close]');
    const error = backdrop.querySelector('[data-cover-edit-error]');
    const targetUrl = backdrop.dataset.url;
    if (!input || !saveButton || !closeButton || !error || !targetUrl) return;

    const initialValue = input.value;

    function clearError() {
        error.textContent = '';
        error.hidden = true;
        input.classList.remove('has-error');
    }

    function showError(message) {
        error.textContent = message;
        error.hidden = false;
        input.classList.add('has-error');
        input.focus();
    }

    function openEditor() {
        clearError();
        input.value = initialValue;
        input.disabled = false;
        saveButton.disabled = false;
        saveButton.textContent = 'Save';
        backdrop.hidden = false;
        document.body.classList.add('cover-edit-open');
        input.focus();
        input.select();
    }

    function closeEditor() {
        clearError();
        input.value = initialValue;
        backdrop.hidden = true;
        document.body.classList.remove('cover-edit-open');
        trigger.focus();
    }

    async function saveCoverUrl() {
        clearError();
        input.disabled = true;
        saveButton.disabled = true;
        saveButton.textContent = 'Saving...';

        try {
            const response = await fetch(targetUrl, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'Accept': 'application/json',
                    'RequestVerificationToken': getAntiForgeryToken()
                },
                body: JSON.stringify({ coverUrl: input.value })
            });

            if (!response.ok) {
                let message = 'Saving failed. Please try again.';
                try {
                    const responseBody = await response.json();
                    if (responseBody && responseBody.message) {
                        message = responseBody.message;
                    }
                } catch {
                    // Keep the default message when the response is not JSON.
                }

                input.disabled = false;
                saveButton.disabled = false;
                saveButton.textContent = 'Save';
                showError(message);
                return;
            }

            window.location.reload();
        } catch {
            input.disabled = false;
            saveButton.disabled = false;
            saveButton.textContent = 'Save';
            showError('Network error. Please check your connection.');
        }
    }

    trigger.addEventListener('click', openEditor);
    closeButton.addEventListener('click', closeEditor);
    saveButton.addEventListener('click', saveCoverUrl);

    backdrop.addEventListener('click', (event) => {
        if (event.target === backdrop) {
            closeEditor();
        }
    });

    input.addEventListener('input', clearError);
    input.addEventListener('keydown', (event) => {
        if (event.key === 'Enter') {
            event.preventDefault();
            saveCoverUrl();
        }
    });

    document.addEventListener('keydown', (event) => {
        if (event.key === 'Escape' && !backdrop.hidden) {
            event.preventDefault();
            closeEditor();
        }
    });
}
