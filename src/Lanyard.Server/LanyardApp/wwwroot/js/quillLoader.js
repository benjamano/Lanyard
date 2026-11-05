// quillLoader.js
//
// Loads Quill, its toolbar theme and Blazored.TextEditor's glue on first use, rather than on every
// page from App.razor: only the rich-text editors and the chat composer need them. Called through
// QuillGate (Components/Editor) with the files' fingerprinted URLs. Every caller shares one load per
// page; a failed load is forgotten so the next call tries again.
//
// quill.core.css stays global in App.razor: read-only views of saved Quill HTML (.ql-editor) need
// its list and alignment rules without the editor.

let loading = null;

function addScript(src) {
    return new Promise((resolve, reject) => {
        const script = document.createElement('script');
        script.src = src;
        script.onload = () => resolve();
        script.onerror = () => {
            script.remove();
            reject(new Error(`Couldn't load ${src}`));
        };
        document.head.appendChild(script);
    });
}

function addStylesheet(href) {
    if (document.querySelector(`link[rel="stylesheet"][href="${href}"]`)) {
        return Promise.resolve();
    }

    return new Promise((resolve, reject) => {
        const link = document.createElement('link');
        link.rel = 'stylesheet';
        link.href = href;
        link.onload = () => resolve();
        link.onerror = () => {
            link.remove();
            reject(new Error(`Couldn't load ${href}`));
        };
        document.head.appendChild(link);
    });
}

// urls: { quill, snowCss, blotFormatter, blazoredQuill }. The scripts depend on each other in that
// order (blot-formatter registers with Quill, Blazored's createQuill uses both), so they load one
// after another; the stylesheet loads alongside them.
export function ensureQuill(urls) {
    if (window.Quill && window.QuillFunctions && window.QuillBlotFormatter) {
        return addStylesheet(urls.snowCss);
    }

    loading ??= Promise.all([
        addStylesheet(urls.snowCss),
        (async () => {
            if (!window.Quill) {
                await addScript(urls.quill);
            }
            if (!window.QuillBlotFormatter) {
                await addScript(urls.blotFormatter);
            }
            if (!window.QuillFunctions) {
                await addScript(urls.blazoredQuill);
            }
        })()
    ]).catch(error => {
        loading = null;
        throw error;
    });

    return loading;
}
