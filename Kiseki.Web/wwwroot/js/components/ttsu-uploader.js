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
        const isCover = name => name.startsWith('cover_') && (name.endsWith('.jpeg') || name.endsWith('.jpg'));
        const sourceFiles = selectedFiles.filter(file => {
            const name = file.name.toLowerCase();
            return name.startsWith('statistics') ||
                (name.startsWith('progress_') && name.endsWith('.json')) ||
                isCover(name);
        });
        const statisticsFiles = sourceFiles.filter(file =>
            file.name.toLowerCase().startsWith('statistics'));
        const progressFiles = sourceFiles.filter(file => {
            const name = file.name.toLowerCase();
            return name.startsWith('progress_') && name.endsWith('.json');
        });
        const coverFiles = sourceFiles.filter(file =>
            isCover(file.name.toLowerCase()));

        if (selectionSummary) {
            if (statisticsFiles.length === 0) {
                selectionSummary.textContent = 'No statistics files were found in that folder.';
            } else {
                const parts = [
                    `${statisticsFiles.length} statistics ${statisticsFiles.length === 1 ? 'file' : 'files'}`
                ];
                if (progressFiles.length > 0) {
                    parts.push(`${progressFiles.length} progress ${progressFiles.length === 1 ? 'file' : 'files'}`);
                }
                if (coverFiles.length > 0) {
                    parts.push(`${coverFiles.length} cover ${coverFiles.length === 1 ? 'file' : 'files'}`);
                }
                let summary;
                if (parts.length === 1) {
                    summary = parts[0];
                } else if (parts.length === 2) {
                    summary = `${parts[0]} and ${parts[1]}`;
                } else {
                    summary = `${parts[0]}, ${parts[1]}, and ${parts[2]}`;
                }
                selectionSummary.textContent = `${summary} ready to preview.`;
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
