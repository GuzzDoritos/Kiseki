/**
 * Sets a button into loading state: locks rendered width, updates text, and disables it after dispatch.
 * @param {HTMLButtonElement|HTMLInputElement} button 
 */
export function setButtonLoading(button) {
    if (!button || button.dataset.isLoading === 'true') return;

    button.dataset.isLoading = 'true';

    // Prevent button shrink / layout jump by locking current rendered width
    const rect = button.getBoundingClientRect();
    if (rect.width > 0) {
        button.style.minWidth = `${rect.width}px`;
    }

    const loadingText = button.getAttribute('data-loading-text') || 'Loading...';

    // If the button has an internal text span (like the auth or sidebar buttons), update it
    const textSpan = button.querySelector('span');
    if (textSpan) {
        textSpan.dataset.originalText = textSpan.textContent;
        textSpan.textContent = loadingText;
    } else {
        button._savedNodes = Array.from(button.childNodes).map(n => n.cloneNode(true));
        button.textContent = loadingText;
    }

    button.classList.add('is-loading');

    // Defer disabling to allow the browser to dispatch the submit request with button data
    setTimeout(() => {
        if (button.dataset.isLoading === 'true') {
            button.disabled = true;
        }
    }, 0);
}

/**
 * Resets all buttons currently in loading state back to their initial text and enabled status.
 */
export function resetLoadingButtons() {
    document.querySelectorAll('[data-is-loading="true"]').forEach((button) => {
        if (button._savedNodes) {
            button.replaceChildren(...button._savedNodes);
            delete button._savedNodes;
        } else {
            const textSpan = button.querySelector('span');
            if (textSpan && textSpan.dataset.originalText) {
                textSpan.textContent = textSpan.dataset.originalText;
                delete textSpan.dataset.originalText;
            }
        }

        button.style.minWidth = '';
        button.disabled = false;
        button.classList.remove('is-loading');
        delete button.dataset.isLoading;
    });
}

/**
 * Configures NProgress and wires up page link clicks, form submissions, and bfcache navigation.
 */
export function initNavigationProgress() {
    if (typeof NProgress === 'undefined') {
        return;
    }

    NProgress.configure({
        showSpinner: false,
        speed: 350,
        minimum: 0.3,
        trickle: true,
        trickleSpeed: 180,
        trickleRate: 0.06
    });

    function startProgress() {
        NProgress.start();
        if (NProgress.status && NProgress.status < 0.35) {
            NProgress.set(0.35);
        }
    }

    // Complete progress when current page has loaded
    NProgress.done();

    // Reset buttons and complete bar when navigating via browser back/forward cache
    window.addEventListener('pageshow', () => {
        NProgress.done();
        resetLoadingButtons();
    });

    // Handle full-page link transitions
    document.addEventListener('click', (event) => {
        if (event.defaultPrevented || event.button !== 0) return;
        if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;

        const link = event.target.closest('a[href]');
        if (!link) return;

        const target = link.getAttribute('target');
        if (target && target !== '_self') return;

        if (link.hasAttribute('download')) return;

        const rel = link.getAttribute('rel');
        if (rel && rel.includes('external')) return;

        const href = link.getAttribute('href');
        if (!href || href.startsWith('#') || href.startsWith('javascript:') || href.startsWith('mailto:') || href.startsWith('tel:')) {
            return;
        }

        try {
            const url = new URL(link.href, window.location.href);
            if (url.origin !== window.location.origin) return;

            // In-page anchor jumps on the same page
            if (url.pathname === window.location.pathname &&
                url.search === window.location.search &&
                url.hash) {
                return;
            }

            startProgress();
        } catch {
            // Ignore invalid URLs
        }
    });

    // Handle form submissions (start progress and switch submit button to "Loading...")
    document.addEventListener('submit', (event) => {
        const form = event.target;
        if (!form || form.nodeName !== 'FORM') return;

        if (form.target && form.target !== '_self') return;

        // Skip if HTML5 form validation fails
        const isNoValidate = form.hasAttribute('novalidate') || (event.submitter && event.submitter.hasAttribute('formnovalidate'));
        if (!isNoValidate && typeof form.checkValidity === 'function' && !form.checkValidity()) {
            return;
        }

        startProgress();

        const submitButton = event.submitter || form.querySelector('button[type="submit"], input[type="submit"]');
        if (submitButton) {
            setButtonLoading(submitButton);
        }
    });
}
