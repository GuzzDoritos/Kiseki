/**
 * Initializes TTSU directory and file upload handling.
 * Groups detected files by book, allows selective book importing,
 * real-time filtering, batch selection, and prepares selected files for preview.
 */
export function initTtsuFolderInput() {
    const ttsuFolderInput = document.querySelector('[data-ttsu-folder-input]');
    const ttsuFilesInput = document.querySelector('[data-ttsu-files-input]');
    const uploadForm = document.getElementById('ttsuUploadForm') || document.querySelector('form[asp-page-handler="Preview"]');
    if (!ttsuFolderInput && !ttsuFilesInput) {
        initReviewToolbar();
        return;
    }

    const selectionSummary = document.querySelector('[data-ttsu-selection-summary]');
    const bookPanel = document.querySelector('[data-ttsu-book-panel]');
    const detectedCountEl = document.querySelector('[data-ttsu-detected-count]');
    const selectedCountEl = document.querySelector('[data-ttsu-selected-count]');
    const bookListEl = document.querySelector('[data-ttsu-book-list]');
    const bookFilterInput = document.querySelector('[data-ttsu-book-filter]');
    const selectAllBtn = document.querySelector('[data-ttsu-select-all]');
    const deselectAllBtn = document.querySelector('[data-ttsu-deselect-all]');
    const previewBtn = document.querySelector('[data-ttsu-preview-btn]');

    // Map: folderKey -> { folder, title, files: File[], hasStats: bool, hasCover: bool, hasProgress: bool, selected: bool }
    const detectedBooks = new Map();

    function isCover(name) {
        return name.startsWith('cover_') && (name.endsWith('.jpeg') || name.endsWith('.jpg'));
    }

    function isStats(name) {
        return name.startsWith('statistics');
    }

    function isProgress(name) {
        return name.startsWith('progress_') && name.endsWith('.json');
    }

    function escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    function processSelectedFiles(filesList) {
        detectedBooks.clear();
        const files = Array.from(filesList ?? []);

        files.forEach(file => {
            const rawPath = (file.webkitRelativePath || file.name).replace(/\\/g, '/');
            const parts = rawPath.split('/').filter(Boolean);
            const fileName = (parts[parts.length - 1] || '').toLowerCase();

            if (!isStats(fileName) && !isProgress(fileName) && !isCover(fileName)) {
                return;
            }

            // Derive book folder key
            let folderKey;
            if (parts.length > 2) {
                folderKey = parts[parts.length - 2];
            } else if (parts.length === 2) {
                folderKey = parts[0];
            } else {
                folderKey = file.name.replace(/\.[^/.]+$/, '');
            }

            if (!detectedBooks.has(folderKey)) {
                detectedBooks.set(folderKey, {
                    folder: folderKey,
                    title: folderKey,
                    files: [],
                    hasStats: false,
                    hasCover: false,
                    hasProgress: false,
                    selected: true
                });
            }

            const book = detectedBooks.get(folderKey);
            book.files.push(file);
            if (isStats(fileName)) book.hasStats = true;
            if (isCover(fileName)) book.hasCover = true;
            if (isProgress(fileName)) book.hasProgress = true;
        });

        renderBookPanel();
    }

    function renderBookPanel() {
        if (!bookPanel) return;

        const books = Array.from(detectedBooks.values());

        if (books.length === 0) {
            bookPanel.hidden = true;
            if (selectionSummary) {
                selectionSummary.textContent = 'No statistics, progress, or cover files found in that selection.';
            }
            if (previewBtn) {
                previewBtn.textContent = 'Preview books';
                previewBtn.disabled = true;
            }
            return;
        }

        bookPanel.hidden = false;
        if (selectionSummary) {
            selectionSummary.textContent = `Found ${books.length} ${books.length === 1 ? 'book' : 'books'}. Choose which ones to import below:`;
        }

        if (bookFilterInput) {
            bookFilterInput.value = '';
        }

        renderBookListItems();
        updateSelectedCounts();
    }

    function renderBookListItems() {
        if (!bookListEl) return;
        bookListEl.innerHTML = '';

        detectedBooks.forEach((book, key) => {
            const item = document.createElement('label');
            item.className = 'ttsu-book-checkbox-item';
            item.dataset.bookKey = key;

            const badgesHtml = [
                book.hasStats ? '<span class="ttsu-mini-badge ttsu-mini-badge-stats">Stats</span>' : '',
                book.hasCover ? '<span class="ttsu-mini-badge ttsu-mini-badge-cover">Cover</span>' : '',
                book.hasProgress ? '<span class="ttsu-mini-badge ttsu-mini-badge-progress">Bookmark</span>' : ''
            ].filter(Boolean).join(' ');

            item.innerHTML = `
                <input type="checkbox" class="form-check-input" ${book.selected ? 'checked' : ''} />
                <span class="ttsu-book-title" title="${escapeHtml(book.title)}">${escapeHtml(book.title)}</span>
                <span class="ttsu-book-badges">${badgesHtml}</span>
            `;

            const chk = item.querySelector('input[type="checkbox"]');
            chk.addEventListener('change', () => {
                book.selected = chk.checked;
                updateSelectedCounts();
            });

            bookListEl.appendChild(item);
        });
    }

    function updateSelectedCounts() {
        const total = detectedBooks.size;
        const selected = Array.from(detectedBooks.values()).filter(b => b.selected).length;

        if (detectedCountEl) {
            detectedCountEl.textContent = `Detected ${total} ${total === 1 ? 'book' : 'books'}`;
        }

        if (selectedCountEl) {
            selectedCountEl.textContent = `${selected} of ${total} selected`;
        }

        if (previewBtn) {
            previewBtn.textContent = `Preview selected books (${selected})`;
            previewBtn.disabled = selected === 0;
        }
    }

    // Filter input handler
    if (bookFilterInput) {
        bookFilterInput.addEventListener('input', () => {
            const query = bookFilterInput.value.trim().toLowerCase();
            const items = bookListEl?.querySelectorAll('.ttsu-book-checkbox-item') ?? [];

            items.forEach(item => {
                const key = item.dataset.bookKey;
                const book = detectedBooks.get(key);
                const title = (book?.title || '').toLowerCase();
                const visible = !query || title.includes(query);
                item.style.display = visible ? 'flex' : 'none';
            });
        });
    }

    // Select all / Deselect all
    if (selectAllBtn) {
        selectAllBtn.addEventListener('click', () => {
            const query = bookFilterInput?.value.trim().toLowerCase() || '';
            detectedBooks.forEach((book, key) => {
                if (!query || book.title.toLowerCase().includes(query)) {
                    book.selected = true;
                }
            });
            syncCheckboxStates();
            updateSelectedCounts();
        });
    }

    if (deselectAllBtn) {
        deselectAllBtn.addEventListener('click', () => {
            const query = bookFilterInput?.value.trim().toLowerCase() || '';
            detectedBooks.forEach((book, key) => {
                if (!query || book.title.toLowerCase().includes(query)) {
                    book.selected = false;
                }
            });
            syncCheckboxStates();
            updateSelectedCounts();
        });
    }

    function syncCheckboxStates() {
        const items = bookListEl?.querySelectorAll('.ttsu-book-checkbox-item') ?? [];
        items.forEach(item => {
            const key = item.dataset.bookKey;
            const book = detectedBooks.get(key);
            const chk = item.querySelector('input[type="checkbox"]');
            if (chk && book) {
                chk.checked = book.selected;
            }
        });
    }

    // Event listeners on file/folder pickers
    if (ttsuFolderInput) {
        ttsuFolderInput.addEventListener('change', () => {
            if (ttsuFolderInput.files && ttsuFolderInput.files.length > 0) {
                if (ttsuFilesInput) ttsuFilesInput.value = '';
                processSelectedFiles(ttsuFolderInput.files);
            }
        });
    }

    if (ttsuFilesInput) {
        ttsuFilesInput.addEventListener('change', () => {
            if (ttsuFilesInput.files && ttsuFilesInput.files.length > 0) {
                if (ttsuFolderInput) ttsuFolderInput.value = '';
                processSelectedFiles(ttsuFilesInput.files);
            }
        });
    }

    // On form submit, populate FolderFiles via DataTransfer with ONLY selected books' files
    if (uploadForm) {
        uploadForm.addEventListener('submit', (e) => {
            if (detectedBooks.size === 0) return;

            const selectedBooks = Array.from(detectedBooks.values()).filter(b => b.selected);
            if (selectedBooks.length === 0) {
                e.preventDefault();
                alert('Please select at least one book to preview.');
                return;
            }

            if (typeof DataTransfer !== 'undefined') {
                try {
                    const dt = new DataTransfer();
                    selectedBooks.forEach(b => {
                        b.files.forEach(f => dt.items.add(f));
                    });
                    if (ttsuFolderInput) {
                        ttsuFolderInput.files = dt.files;
                    }
                    if (ttsuFilesInput) {
                        ttsuFilesInput.value = '';
                    }
                } catch {
                    // Fall back to original files if DataTransfer is restricted
                }
            }
        });
    }

    initReviewToolbar();
}

/**
 * Initializes batch selection toolbar in the review screen (Review X books).
 */
function initReviewToolbar() {
    const selectAllBtn = document.querySelector('[data-review-select-all]');
    const deselectAllBtn = document.querySelector('[data-review-deselect-all]');
    const countEl = document.querySelector('[data-review-selected-count]');
    const checkboxes = document.querySelectorAll('.ttsu-book-select input[type="checkbox"]');

    if (!checkboxes.length) return;

    function updateCount() {
        const total = checkboxes.length;
        const checked = Array.from(checkboxes).filter(c => c.checked).length;
        if (countEl) {
            countEl.textContent = `${checked} of ${total} books selected`;
        }
    }

    if (selectAllBtn) {
        selectAllBtn.addEventListener('click', () => {
            checkboxes.forEach(c => { c.checked = true; });
            updateCount();
        });
    }

    if (deselectAllBtn) {
        deselectAllBtn.addEventListener('click', () => {
            checkboxes.forEach(c => { c.checked = false; });
            updateCount();
        });
    }

    checkboxes.forEach(c => {
        c.addEventListener('change', updateCount);
    });

    updateCount();
}
