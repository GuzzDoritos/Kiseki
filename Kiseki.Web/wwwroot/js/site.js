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

            startProgress();
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

        startProgress();

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

// =============================================================================
// Inline Editing for Book Details
// =============================================================================
(function () {
    const editContainers = document.querySelectorAll("[data-inline-edit]");
    if (editContainers.length === 0) return;

    function getAntiForgeryToken() {
        const tokenInput = document.querySelector('#antiforgeryForm input[name="__RequestVerificationToken"]');
        return tokenInput ? tokenInput.value : "";
    }

    editContainers.forEach((container) => {
        const fieldType = container.dataset.inlineEdit;
        const targetUrl = container.dataset.url;
        const displayEl = container.querySelector("[data-inline-display]");
        const editorEl = container.querySelector("[data-inline-editor]");
        const inputEl = container.querySelector("[data-inline-input]");
        const saveBtn = container.querySelector("[data-inline-save]");
        const errorEl = container.querySelector("[data-inline-error]");
        const textEl = container.querySelector("[data-inline-text]");

        if (!displayEl || !editorEl || !inputEl || !saveBtn || !textEl) return;

        function enterEditMode() {
            clearError();
            displayEl.hidden = true;
            editorEl.hidden = false;
            inputEl.value = inputEl.dataset.initialValue || "";
            inputEl.disabled = false;
            saveBtn.disabled = false;
            inputEl.focus();
            inputEl.select();
        }

        function cancelEditMode() {
            clearError();
            inputEl.value = inputEl.dataset.initialValue || "";
            editorEl.hidden = true;
            displayEl.hidden = false;
        }

        function showError(msg) {
            if (errorEl) {
                errorEl.textContent = msg;
                errorEl.hidden = false;
            }
            inputEl.classList.add("has-error");
            inputEl.focus();
        }

        function clearError() {
            if (errorEl) {
                errorEl.textContent = "";
                errorEl.hidden = true;
            }
            inputEl.classList.remove("has-error");
        }

        async function handleSave() {
            clearError();
            const rawValue = inputEl.value;

            let payload;
            if (fieldType === "title") {
                const trimmed = rawValue.trim();
                if (trimmed.length === 0) {
                    showError("Title cannot be empty.");
                    return;
                }
                if (trimmed.length > 500) {
                    showError("Title cannot exceed 500 characters.");
                    return;
                }
                if (trimmed === (inputEl.dataset.initialValue || "").trim()) {
                    cancelEditMode();
                    return;
                }
                payload = { title: trimmed };
            } else if (fieldType === "characterTotal") {
                const sanitized = rawValue.replace(/,/g, "").trim();
                let parsedCount = null;
                if (sanitized.length > 0) {
                    if (!/^\d+$/.test(sanitized)) {
                        showError("Enter a valid number or leave blank.");
                        return;
                    }
                    parsedCount = parseInt(sanitized, 10);
                    if (parsedCount < 0) {
                        showError("Character total cannot be negative.");
                        return;
                    }
                }
                const initialSanitized = (inputEl.dataset.initialValue || "").replace(/,/g, "").trim();
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
                    method: "POST",
                    headers: {
                        "Content-Type": "application/json",
                        "Accept": "application/json",
                        "RequestVerificationToken": token
                    },
                    body: JSON.stringify(payload)
                });

                if (!response.ok) {
                    let errMsg = "Saving failed. Please try again.";
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

                if (fieldType === "title") {
                    textEl.textContent = data.title;
                    inputEl.dataset.initialValue = data.title;
                    inputEl.value = data.title;

                    const breadcrumb = document.querySelector("[data-breadcrumb-title]");
                    if (breadcrumb) {
                        breadcrumb.textContent = data.title;
                    }
                    document.title = `${data.title} - Kiseki`;
                } else if (fieldType === "characterTotal") {
                    textEl.textContent = data.formattedTotalCharacters;
                    const newInitialVal = data.manualOverride != null ? data.manualOverride.toString() : (data.totalCharacters > 0 ? data.totalCharacters.toString() : "");
                    inputEl.dataset.initialValue = newInitialVal;
                    inputEl.value = newInitialVal;

                    // Update progress bar
                    const progressWrap = document.querySelector(".work-progress");
                    if (progressWrap) {
                        const copySpan = progressWrap.querySelector(".progress-copy > span:first-child");
                        if (copySpan) {
                            copySpan.textContent = data.progressLabel;
                        }

                        const percentageSpan = progressWrap.querySelector(".progress-percentage");
                        if (percentageSpan) {
                            percentageSpan.textContent = `${data.formattedProgressPercentage}%`;
                        }

                        const progressTrack = progressWrap.querySelector(".progress-track");
                        if (progressTrack) {
                            progressTrack.setAttribute("aria-valuenow", data.formattedProgressPercentage);
                        }

                        const progressFill = progressWrap.querySelector(".progress-fill");
                        if (progressFill) {
                            progressFill.style.width = `${data.formattedProgressPercentage}%`;
                            if (data.isCompleted) {
                                progressFill.classList.add("complete");
                            } else {
                                progressFill.classList.remove("complete");
                            }
                        }
                    }

                    // Update metadata aside source
                    const sourceEl = document.querySelector("[data-metadata-source]");
                    if (sourceEl) {
                        sourceEl.textContent = data.characterTotalSource;
                    }

                    // Update status badge & aside status if returned
                    if (data.statusLabel && data.statusCssClass) {
                        const badge = document.querySelector("[data-status-toggle]");
                        if (badge) {
                            const badgeText = badge.querySelector("[data-status-text]");
                            if (badgeText) badgeText.textContent = data.statusLabel;
                            badge.className = `media-status media-status-${data.statusCssClass} status-toggle-badge`;
                            badge.setAttribute("aria-label", `Status: ${data.statusLabel}. Click to toggle.`);
                        }
                        const metaStatus = document.querySelector("[data-metadata-status]");
                        if (metaStatus) {
                            metaStatus.textContent = data.statusLabel;
                        }
                    }

                    // Update manual row if present or manage its visibility
                    const manualRow = document.querySelector("[data-metadata-manual-row]");
                    const manualTotal = document.querySelector("[data-metadata-manual-total]");
                    if (data.manualOverride != null) {
                        if (manualTotal) {
                            manualTotal.textContent = Number(data.manualOverride).toLocaleString("en-US");
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
                showError("Network error. Please check your connection.");
            }
        }

        // Clicking anywhere on display enters edit mode
        displayEl.addEventListener("click", () => {
            enterEditMode();
        });

        // Save button
        saveBtn.addEventListener("click", (e) => {
            e.preventDefault();
            e.stopPropagation();
            handleSave();
        });

        // Keyboard navigation
        inputEl.addEventListener("keydown", (e) => {
            if (e.key === "Enter") {
                e.preventDefault();
                handleSave();
            } else if (e.key === "Escape") {
                e.preventDefault();
                cancelEditMode();
            }
        });

        // Clear error on input
        inputEl.addEventListener("input", () => {
            if (inputEl.classList.contains("has-error")) {
                clearError();
            }
        });
    });
})();

// =============================================================================
// Status Toggle Badge
// =============================================================================
(function () {
    const statusBtn = document.querySelector("[data-status-toggle]");
    if (!statusBtn) return;

    function getAntiForgeryToken() {
        const tokenInput = document.querySelector('#antiforgeryForm input[name="__RequestVerificationToken"]');
        return tokenInput ? tokenInput.value : "";
    }

    statusBtn.addEventListener("click", async (e) => {
        e.preventDefault();
        const url = statusBtn.dataset.url;
        if (!url || statusBtn.disabled) return;

        statusBtn.disabled = true;

        try {
            const token = getAntiForgeryToken();
            const response = await fetch(url, {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "Accept": "application/json",
                    "RequestVerificationToken": token
                }
            });

            if (!response.ok) {
                statusBtn.disabled = false;
                return;
            }

            const data = await response.json();
            statusBtn.disabled = false;

            // Update badge text and classes
            const statusText = statusBtn.querySelector("[data-status-text]");
            if (statusText) {
                statusText.textContent = data.statusLabel;
            }
            statusBtn.className = `media-status media-status-${data.statusCssClass} status-toggle-badge`;
            statusBtn.setAttribute("aria-label", `Status: ${data.statusLabel}. Click to toggle.`);

            // Update metadata aside status
            const metadataStatus = document.querySelector("[data-metadata-status]");
            if (metadataStatus) {
                metadataStatus.textContent = data.statusLabel;
            }

            // Update progress bar
            const progressWrap = document.querySelector(".work-progress");
            if (progressWrap) {
                const copySpan = progressWrap.querySelector(".progress-copy > span:first-child");
                if (copySpan) {
                    copySpan.textContent = data.progressLabel;
                }

                const percentageSpan = progressWrap.querySelector(".progress-percentage");
                if (percentageSpan) {
                    percentageSpan.textContent = `${data.formattedProgressPercentage}%`;
                }

                const progressTrack = progressWrap.querySelector(".progress-track");
                if (progressTrack) {
                    progressTrack.setAttribute("aria-valuenow", data.formattedProgressPercentage);
                }

                const progressFill = progressWrap.querySelector(".progress-fill");
                if (progressFill) {
                    progressFill.style.width = `${data.formattedProgressPercentage}%`;
                    if (data.isCompleted) {
                        progressFill.classList.add("complete");
                    } else {
                        progressFill.classList.remove("complete");
                    }
                }
            }
        } catch {
            statusBtn.disabled = false;
        }
    });
})();
