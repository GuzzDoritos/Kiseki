import { initCoverFallbacks } from './components/cover-fallback.js';
import { initCoverEditor } from './components/cover-editor.js';
import { initTtsuFolderInput } from './components/ttsu-uploader.js';
import { initTtsuAutoMatch, initTtsuAutoMatchProgress } from './components/ttsu-auto-match.js';
import { initNavigationProgress } from './components/navigation.js';
import { initInlineEditors } from './components/inline-editor.js';
import { initStatusToggle } from './components/status-toggle.js';

function initApp() {
    initCoverFallbacks();
    initCoverEditor();
    initTtsuFolderInput();
    initTtsuAutoMatch();
    initTtsuAutoMatchProgress();
    initNavigationProgress();
    initInlineEditors();
    initStatusToggle();
}

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initApp);
} else {
    initApp();
}
