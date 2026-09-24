import { initCoverFallbacks } from './components/cover-fallback.js';
import { initCoverEditor } from './components/cover-editor.js';
import { initTtsuFolderInput } from './components/ttsu-uploader.js';
import { initTtsuAutoMatch, initTtsuAutoMatchProgress } from './components/ttsu-auto-match.js';
import { initNavigationProgress } from './components/navigation.js';
import { initInlineEditors } from './components/inline-editor.js';
import { initStatusToggle } from './components/status-toggle.js';

function initSubmitLocks() {
    document.querySelectorAll('form[data-submit-lock]').forEach((form) => {
        form.addEventListener('submit', () => {
            if (form.getAttribute('aria-busy') === 'true') return;

            form.setAttribute('aria-busy', 'true');
            const button = form.querySelector('button[type="submit"]');
            const status = form.querySelector('[data-submit-status]');
            if (button) {
                button.disabled = true;
                button.setAttribute('aria-disabled', 'true');
                button.textContent = button.dataset.submittingLabel || 'Working…';
            }
            if (status) status.textContent = button?.textContent || 'Working…';
        });
    });
}

function initTtsuTargetSelection() {
    const containers = document.querySelectorAll('[data-ttsu-target-selection]');
    if (containers.length === 0) return;

    const confirmBtn = document.querySelector('[data-ttsu-confirm-btn]');
    const reviewBtn = document.querySelector('[data-ttsu-review-btn]');
    const confirmHint = document.querySelector('[data-ttsu-confirm-hint]');

    function markReviewStale() {
        if (confirmBtn) {
            confirmBtn.disabled = true;
            confirmBtn.setAttribute('aria-disabled', 'true');
            confirmBtn.title = 'Review required after changing target selection. Select "Refresh review" first.';
        }
        if (reviewBtn) {
            reviewBtn.classList.add('btn-highlight-refresh');
        }
        if (confirmHint) {
            confirmHint.textContent = 'Target selection modified. Select "Refresh review" before confirming.';
            confirmHint.style.display = '';
        }
    }

    containers.forEach((container) => {
        const index = container.dataset.bookIndex;
        const modeInput = container.querySelector('[data-ttsu-mode-input]');
        const radios = container.querySelectorAll('[data-ttsu-intent-radio]');
        const wrapExisting = container.querySelector(`#wrap_existing_${index}`);
        const wrapNewCopy = container.querySelector(`#wrap_newcopy_${index}`);
        const optionCards = container.querySelectorAll('[data-ttsu-option-card]');
        const targetSelect = container.querySelector('[data-ttsu-target-select]');
        const installmentSelect = container.querySelector('[data-ttsu-installment-select]');

        function applyIntent(intent, reviewIsStale) {
            if (wrapExisting) {
                wrapExisting.style.display = intent === 'ExistingCopy' ? '' : 'none';
            }
            if (wrapNewCopy) {
                wrapNewCopy.style.display = intent === 'NewCopyUnderExistingInstallment' ? '' : 'none';
            }

            optionCards.forEach((card) => {
                card.classList.toggle('is-selected', card.dataset.ttsuOptionCard === intent);
            });

            if (targetSelect) {
                const isActive = intent === 'ExistingCopy';
                targetSelect.disabled = !isActive;
                if (!isActive) targetSelect.value = '';
            }
            if (installmentSelect) {
                const isActive = intent === 'NewCopyUnderExistingInstallment';
                installmentSelect.disabled = !isActive;
                if (!isActive) installmentSelect.value = '';
            }
            if (modeInput) {
                modeInput.value = intent === 'ExistingCopy' ? 'Merge' : 'Create';
            }
            if (reviewIsStale) markReviewStale();
        }

        radios.forEach((radio) => {
            radio.addEventListener('change', () => {
                if (!radio.checked) return;
                applyIntent(radio.dataset.intent, true);
            });
        });

        const selectedRadio = Array.from(radios).find((radio) => radio.checked);
        applyIntent(selectedRadio?.dataset.intent, false);

        if (targetSelect) {
            targetSelect.addEventListener('change', markReviewStale);
        }
        if (installmentSelect) {
            installmentSelect.addEventListener('change', markReviewStale);
        }
    });

    const reviewForm = document.getElementById('ttsuReviewForm');
    if (reviewForm) {
        reviewForm.querySelectorAll('input[type="radio"][name$=".CandidateKey"], input[type="radio"][name$=".SelectedCoverKey"], select[name$=".ProgressChoice"], select[name$=".Choice"], select[name$=".OrphanLogIds"]').forEach((el) => {
            el.addEventListener('change', markReviewStale);
        });
    }
}

function initApp() {
    initCoverFallbacks();
    initCoverEditor();
    initTtsuFolderInput();
    initTtsuAutoMatch();
    initTtsuAutoMatchProgress();
    initNavigationProgress();
    initInlineEditors();
    initStatusToggle();
    initSubmitLocks();
    initTtsuTargetSelection();
}

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initApp);
} else {
    initApp();
}
