// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

const ttsuFolderInput = document.querySelector("[data-ttsu-folder-input]");

if (ttsuFolderInput) {
    const selectionSummary = document.querySelector("[data-ttsu-selection-summary]");

    ttsuFolderInput.addEventListener("change", () => {
        const selectedFiles = Array.from(ttsuFolderInput.files ?? []);
        const statisticsFiles = selectedFiles.filter(file =>
            file.name.toLowerCase().startsWith("statistics"));

        if (selectionSummary) {
            if (statisticsFiles.length === 0) {
                selectionSummary.textContent = "No statistics files were found in that folder.";
            } else {
                const suffix = statisticsFiles.length === 1 ? "file" : "files";
                selectionSummary.textContent = `${statisticsFiles.length} statistics ${suffix} ready to preview.`;
            }
        }

        if (statisticsFiles.length === 0 || typeof DataTransfer === "undefined") {
            return;
        }

        try {
            const filteredFiles = new DataTransfer();
            statisticsFiles.forEach(file => filteredFiles.items.add(file));
            ttsuFolderInput.files = filteredFiles.files;
        } catch {
            // The server also filters uploads, so older browsers can submit the original selection.
        }
    });
}

document.querySelectorAll("[data-cover-image]").forEach(image => {
    const showFallback = () => {
        const frame = image.closest("[data-cover-frame]");
        const fallback = frame?.querySelector("[data-cover-fallback]");

        image.hidden = true;
        frame?.classList.remove("has-image");
        fallback?.removeAttribute("hidden");
    };

    image.addEventListener("error", showFallback, { once: true });

    if (image.complete && image.naturalWidth === 0) {
        showFallback();
    }
});

// =============================================================================
// NProgress & Navigation / Submit Feedback
// =============================================================================
(function () {
    if (typeof NProgress === "undefined") {
        return;
    }

    NProgress.configure({
        showSpinner: false,
        speed: 300,
        minimum: 0.1
    });

    // Complete progress when current page has loaded
    NProgress.done();

    // Reset buttons and complete bar when navigating via browser back/forward cache
    window.addEventListener("pageshow", () => {
        NProgress.done();
        resetLoadingButtons();
    });

    // Handle full-page link transitions
    document.addEventListener("click", (event) => {
        if (event.defaultPrevented || event.button !== 0) return;
        if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;

        const link = event.target.closest("a[href]");
        if (!link) return;

        const target = link.getAttribute("target");
        if (target && target !== "_self") return;

        if (link.hasAttribute("download")) return;

        const rel = link.getAttribute("rel");
        if (rel && rel.includes("external")) return;

        const href = link.getAttribute("href");
        if (!href || href.startsWith("#") || href.startsWith("javascript:") || href.startsWith("mailto:") || href.startsWith("tel:")) {
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

            NProgress.start();
        } catch {
            // Ignore invalid URLs
        }
    });

    // Handle form submissions (start progress and switch submit button to "Loading...")
    document.addEventListener("submit", (event) => {
        const form = event.target;
        if (!form || form.nodeName !== "FORM") return;

        if (form.target && form.target !== "_self") return;

        // Skip if HTML5 form validation fails
        const isNoValidate = form.hasAttribute("novalidate") || (event.submitter && event.submitter.hasAttribute("formnovalidate"));
        if (!isNoValidate && typeof form.checkValidity === "function" && !form.checkValidity()) {
            return;
        }

        NProgress.start();

        const submitButton = event.submitter || form.querySelector('button[type="submit"], input[type="submit"]');
        if (submitButton) {
            setButtonLoading(submitButton);
        }
    });

    function setButtonLoading(button) {
        if (!button || button.dataset.isLoading === "true") return;

        button.dataset.isLoading = "true";

        // Prevent button shrink / layout jump by locking current rendered width
        const rect = button.getBoundingClientRect();
        if (rect.width > 0) {
            button.style.minWidth = `${rect.width}px`;
        }

        const loadingText = button.getAttribute("data-loading-text") || "Loading...";

        // If the button has an internal text span (like the auth or sidebar buttons), update it
        const textSpan = button.querySelector("span");
        if (textSpan) {
            textSpan.dataset.originalText = textSpan.textContent;
            textSpan.textContent = loadingText;
        } else {
            button._savedNodes = Array.from(button.childNodes).map(n => n.cloneNode(true));
            button.textContent = loadingText;
        }

        button.classList.add("is-loading");

        // Defer disabling to allow the browser to dispatch the submit request with button data
        setTimeout(() => {
            if (button.dataset.isLoading === "true") {
                button.disabled = true;
            }
        }, 0);
    }

    function resetLoadingButtons() {
        document.querySelectorAll('[data-is-loading="true"]').forEach((button) => {
            if (button._savedNodes) {
                button.replaceChildren(...button._savedNodes);
                delete button._savedNodes;
            } else {
                const textSpan = button.querySelector("span");
                if (textSpan && textSpan.dataset.originalText) {
                    textSpan.textContent = textSpan.dataset.originalText;
                    delete textSpan.dataset.originalText;
                }
            }

            button.style.minWidth = "";
            button.disabled = false;
            button.classList.remove("is-loading");
            delete button.dataset.isLoading;
        });
    }
})();
