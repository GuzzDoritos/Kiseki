/**
 * Initializes TTSU directory upload handling.
 * Filters selected folder files to TTSU statistics and progress JSON files
 * and updates the selection summary text.
 */
export function initTtsuFolderInput() {
    const ttsuFolderInput = document.querySelector('[data-ttsu-folder-input]');
    if (!ttsuFolderInput) return;

    const selectionSummary = document.querySelector('[data-ttsu-selection-summary]');

    ttsuFolderInput.addEventListener('change', () => {
        const selectedFiles = Array.from(ttsuFolderInput.files ?? []);
        const sourceFiles = selectedFiles.filter(file => {
            const name = file.name.toLowerCase();
            return name.startsWith('statistics') ||
                (name.startsWith('progress_') && name.endsWith('.json'));
        });
        const statisticsFiles = sourceFiles.filter(file =>
            file.name.toLowerCase().startsWith('statistics'));

        if (selectionSummary) {
            if (statisticsFiles.length === 0) {
                selectionSummary.textContent = 'No statistics files were found in that folder.';
            } else {
                const suffix = statisticsFiles.length === 1 ? 'file' : 'files';
                const progressCount = sourceFiles.length - statisticsFiles.length;
                const progressSummary = progressCount === 0 ? '' : ` and ${progressCount} progress ${progressCount === 1 ? 'file' : 'files'}`;
                selectionSummary.textContent = `${statisticsFiles.length} statistics ${suffix}${progressSummary} ready to preview.`;
            }
        }

        if (statisticsFiles.length === 0 || typeof DataTransfer === 'undefined') {
            return;
        }

        try {
            const filteredFiles = new DataTransfer();
            sourceFiles.forEach(file => filteredFiles.items.add(file));
            ttsuFolderInput.files = filteredFiles.files;
        } catch {
            // The server also filters uploads, so older browsers can submit the original selection.
        }
    });
}
