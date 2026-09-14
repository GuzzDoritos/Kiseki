/**
 * Initializes cover image fallback handling.
 * If an image fails to load or has a natural width of 0, it is hidden and
 * its placeholder frame displays the fallback Japanese glyph.
 */
export function initCoverFallbacks() {
    document.querySelectorAll('[data-cover-image]').forEach(image => {
        const showFallback = () => {
            const frame = image.closest('[data-cover-frame]');
            const fallback = frame?.querySelector('[data-cover-fallback]');

            image.hidden = true;
            frame?.classList.remove('has-image');
            fallback?.removeAttribute('hidden');
        };

        image.addEventListener('error', showFallback, { once: true });

        if (image.complete && image.naturalWidth === 0) {
            showFallback();
        }
    });
}
