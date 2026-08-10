// courseImageEditor.js

window.courseImageEditor = (() => {
    // --- Move up/down controls -------------------------------------------------
    //
    // quill-blot-formatter's own click-to-select overlay is NOT a real Quill/DOM
    // text selection (Quill.getSelection() stays null while it's showing) - it's
    // a purely visual overlay the library draws on top of the image. That means
    // native cut/copy/drag never has anything to act on when the user clicks
    // directly on an image, which is why repositioning an inserted image that
    // way doesn't work. Rather than fight that, this shows two small buttons
    // next to any clicked image that reorder it one line at a time by directly
    // editing the Quill Delta - no text selection involved.

    let toolbarEl = null;
    let currentImage = null;
    let stylesInjected = false;

    function ensureStyles() {
        if (stylesInjected) {
            return;
        }

        stylesInjected = true;
        const style = document.createElement('style');
        style.textContent = `
            .course-image-move-controls {
                position: fixed;
                display: none;
                gap: 2px;
                z-index: 2000;
                background-color: var(--colorNeutralBackground1, #fff);
                border: 1px solid var(--colorNeutralStroke1, #d1d1d1);
                border-radius: var(--borderRadiusMedium, 4px);
                box-shadow: 0 2px 6px rgba(0, 0, 0, 0.25);
                padding: 2px;
            }
            .course-image-move-controls button {
                width: 26px;
                height: 26px;
                border: none;
                background: transparent;
                cursor: pointer;
                font-size: 12px;
                color: var(--colorNeutralForeground1, #242424);
                border-radius: var(--borderRadiusSmall, 2px);
            }
            .course-image-move-controls button:hover {
                background-color: var(--colorNeutralBackground3, #e0e0e0);
            }
        `;
        document.head.appendChild(style);
    }

    function ensureToolbar() {
        if (toolbarEl) {
            return toolbarEl;
        }

        ensureStyles();
        toolbarEl = document.createElement('div');
        toolbarEl.className = 'course-image-move-controls';
        toolbarEl.innerHTML =
            '<button type="button" data-direction="up" title="Move image up">&#9650;</button>' +
            '<button type="button" data-direction="down" title="Move image down">&#9660;</button>';
        toolbarEl.style.display = 'none';
        document.body.appendChild(toolbarEl);

        // mousedown (not click) so this fires before the browser/Quill would
        // otherwise treat the click as "outside the image" and deselect it.
        toolbarEl.addEventListener('mousedown', (event) => {
            const button = event.target.closest('button[data-direction]');

            if (!button || !currentImage) {
                return;
            }

            event.preventDefault();
            moveImage(currentImage, button.dataset.direction);
        });

        return toolbarEl;
    }

    function hideToolbar() {
        if (toolbarEl) {
            toolbarEl.style.display = 'none';
        }

        currentImage = null;
    }

    function showToolbarFor(img) {
        const toolbar = ensureToolbar();
        currentImage = img;

        const rect = img.getBoundingClientRect();
        toolbar.style.display = 'flex';
        toolbar.style.top = `${Math.max(rect.top - 34, 4)}px`;
        toolbar.style.left = `${rect.left}px`;
    }

    // Moves the image exactly one line up/down by deleting it and re-inserting
    // it at the start of the adjacent line - the same treatment in both
    // directions (always prepend to the target line), so one click always moves
    // exactly one line regardless of whether that line is empty.
    function moveImage(img, direction) {
        const container = img.closest('.ql-container');

        if (!container || !container.__quill) {
            return;
        }

        const quill = container.__quill;
        const blot = Quill.find(img);

        if (!blot) {
            return;
        }

        const imgIndex = blot.offset(quill.scroll);
        const [line] = quill.getLine(imgIndex);
        const targetLine = direction === 'up' ? line.prev : line.next;

        if (!targetLine) {
            return;
        }

        let targetIndex = targetLine.offset(quill.scroll);
        const src = img.getAttribute('src');
        const alt = img.getAttribute('alt') || src;
        // quill-blot-formatter resizes an image by setting plain width/height
        // HTML attributes (confirmed in its source) rather than a Quill format
        // operation, so deleting and re-inserting the image loses any resize
        // unless those attributes are captured and carried over explicitly here.
        // Quill's image format silently skips null values, so this is safe to
        // pass even when the image was never resized.
        const width = img.getAttribute('width');
        const height = img.getAttribute('height');
        const Delta = Quill.import('delta');

        quill.deleteText(imgIndex, 1, 'user');

        if (targetIndex > imgIndex) {
            targetIndex -= 1;
        }

        quill.updateContents(new Delta().retain(targetIndex).insert({ image: src }, { alt, width, height }), 'user');
        quill.setSelection(targetIndex, 1, 'user');

        // Quill re-renders the moved image as a new DOM node, so the toolbar
        // needs to be pointed at that node to keep working for another click.
        // Quill's updateContents() patches the DOM synchronously (only its event
        // emission is deferred), so the new node is findable immediately - no
        // need to wait a frame, which only opens a window for something else
        // (Quill's own scroll-into-view, blot-formatter re-registering) to run
        // first and leave the toolbar pointed at a stale position. A rAF-based
        // follow-up re-check runs afterward anyway, purely to correct position
        // if a scroll happens on the next frame - it never decides show/hide.
        const relocate = () => {
            const [newLeaf] = quill.getLeaf(targetIndex);

            if (newLeaf && newLeaf.domNode && newLeaf.domNode.tagName === 'IMG') {
                showToolbarFor(newLeaf.domNode);
                return true;
            }

            return false;
        };

        if (!relocate()) {
            hideToolbar();
        }

        requestAnimationFrame(relocate);
    }

    document.addEventListener('click', (event) => {
        const img = event.target.closest('.ql-editor img');

        if (img) {
            showToolbarFor(img);
            return;
        }

        if (toolbarEl && !toolbarEl.contains(event.target)) {
            hideToolbar();
        }
    });

    // --- Insert at cursor --------------------------------------------------

    return {
        // Blazored.TextEditor's own InsertImage helper calls Quill's getSelection()
        // with no argument, which returns null whenever the editor doesn't currently
        // have DOM focus - always true right after our image-picker dialog closes -
        // so it silently falls back to inserting at document position 0. Calling
        // getSelection(true) instead restores focus and returns the last-known
        // cursor position, so the image lands where the user actually left it.
        insertImageAtCursor(hostElement, imageUrl) {
            const container = hostElement.querySelector('.ql-container');

            if (!container || !container.__quill) {
                return;
            }

            const quill = container.__quill;
            const range = quill.getSelection(true);
            const index = range ? range.index : quill.getLength();
            const Delta = Quill.import('delta');

            quill.updateContents(new Delta().retain(index).insert({ image: imageUrl }, { alt: imageUrl }));
            quill.setSelection(index + 1, 0);
        }
    };
})();
