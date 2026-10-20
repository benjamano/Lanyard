// kioskWidgetEditor.js

export const kioskWidgetEditor = (() => {
    let dividerRegistered = false;

    // Quill has no built-in "insert <hr>" control - registering a minimal BlockEmbed blot is
    // the standard way to teach it a new embeddable tag. Deferred until first use (rather than
    // registered when this module loads) so it never depends on Quill having loaded first:
    // QuillGate (Components/Editor) loads Quill on demand.
    function ensureDividerBlot() {
        if (dividerRegistered) {
            return;
        }

        dividerRegistered = true;
        const BlockEmbed = Quill.import('blots/block/embed');

        class DividerBlot extends BlockEmbed { }
        DividerBlot.blotName = 'divider';
        DividerBlot.tagName = 'hr';

        Quill.register(DividerBlot);
    }

    return {
        // Inserts a horizontal rule at the current cursor position (or at the end if the editor
        // doesn't currently have a selection), on its own line.
        insertDivider(hostElement) {
            ensureDividerBlot();

            const container = hostElement.querySelector('.ql-container');

            if (!container || !container.__quill) {
                return;
            }

            const quill = container.__quill;
            const range = quill.getSelection(true);
            const index = range ? range.index : quill.getLength();

            quill.insertEmbed(index, 'divider', true, 'user');
            quill.setSelection(index + 1, 0, 'user');
        }
    };
})();
