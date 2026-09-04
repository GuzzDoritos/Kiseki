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
