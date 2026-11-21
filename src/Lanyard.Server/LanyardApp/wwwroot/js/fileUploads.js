// fileUploads.js
//
// Files' upload queue. Uploads go straight to POST api/files/upload over plain HTTP (the signed-in
// cookie), not through the Blazor circuit, so a 400 MB video doesn't have to squeeze through
// SignalR and every file gets real progress. Three upload at a time.
//
// Ways in: the Upload button (pick()), dropping files anywhere on Files (onto a folder tile or a
// folder in the tree puts them in that folder), pasting a picture, and dropping a whole folder,
// which recreates its folders. Before each upload the browser measures what it can (a picture's
// size, a video's or song's length) and takes a frame from a video for its thumbnail.
//
// The .NET side (FileBrowser) gets OnUploadsChanged(items) as things move, OnUploadFinished(item)
// per file, and EnsureFolderPath(parentId, "A/B") when a dropped folder needs creating.
//
// It also carries Files' other browser-side bits: dragging files and folders onto a folder to move
// them (OnItemDragStart / OnItemsDropped), Ctrl/⌘+A and Esc for the selection (SelectAll /
// ClearSelection), the zip download and Copy link. Staff who can't upload get those without the
// upload parts (options.canUpload false).

const INTERNAL_DRAG = 'application/x-lanyard-files';

const CONCURRENCY = 3;
const MAX_FILES_PER_DROP = 500;
const PROGRESS_EVERY_MS = 200;

export function attach(root, dotnet, options) {
    const canUpload = options.canUpload !== false;
    const state = {
        root,
        dotnet,
        folderId: options.folderId || null,
        folderName: options.folderName || 'Files',
        items: [],
        nextId: 1,
        active: 0,
        dragDepth: 0,
        folderPaths: new Map(),
        lastReport: 0,
        reportTimer: null,
        disposed: false,
    };

    const overlay = document.createElement('div');
    overlay.className = 'files-drop-overlay';
    overlay.setAttribute('aria-hidden', 'true');
    overlay.innerHTML = '<div class="files-drop-card"><div class="files-drop-icon">⤒</div><div class="files-drop-text"></div></div>';
    root.appendChild(overlay);
    state.overlay = overlay;

    const input = document.createElement('input');
    input.type = 'file';
    input.multiple = true;
    input.hidden = true;
    input.addEventListener('change', () => {
        enqueue(state, Array.from(input.files || []).map(file => ({ file, relativeDir: '' })), state.pickFolder ?? state.folderId);
        input.value = '';
    });
    root.appendChild(input);
    state.input = input;

    const types = e => Array.from(e.dataTransfer?.types || []);
    const isInternalDrag = e => types(e).includes(INTERNAL_DRAG);
    const isFileDrag = e => canUpload && !isInternalDrag(e) && types(e).includes('Files');

    // ---- Moving files and folders by dragging them onto a folder ----

    state.onDragStart = e => {
        const item = e.target?.closest?.('[data-drag-item]');
        if (!item || !root.contains(item)) return;
        // Firefox won't start a drag without data.
        e.dataTransfer.setData(INTERNAL_DRAG, item.getAttribute('data-drag-item'));
        e.dataTransfer.effectAllowed = 'move';
        state.internalDrag = true;
        dotnet.invokeMethodAsync('OnItemDragStart', item.getAttribute('data-drag-item')).catch(() => {});
    };
    state.onDragEnd = () => {
        state.internalDrag = false;
        root.querySelectorAll('.is-drop-target').forEach(x => x.classList.remove('is-drop-target'));
    };
    state.onInternalDragOver = e => {
        if (!state.internalDrag && !isInternalDrag(e)) return false;
        const target = e.target?.closest?.('[data-drop-folder]');
        root.querySelectorAll('.is-drop-target').forEach(x => x !== target && x.classList.remove('is-drop-target'));
        if (!target || !root.contains(target)) return true;
        e.preventDefault();
        e.dataTransfer.dropEffect = 'move';
        target.classList.add('is-drop-target');
        return true;
    };
    state.onInternalDrop = e => {
        if (!state.internalDrag && !isInternalDrag(e)) return false;
        const target = e.target?.closest?.('[data-drop-folder]');
        state.onDragEnd();
        if (!target || !root.contains(target)) return true;
        e.preventDefault();
        dotnet.invokeMethodAsync('OnItemsDropped', target.getAttribute('data-drop-folder') || null).catch(() => {});
        return true;
    };

    // ---- Selecting everything / nothing from the keyboard ----

    state.onKeyDown = e => {
        const active = document.activeElement;
        if (active && (active.tagName === 'INPUT' || active.tagName === 'TEXTAREA' || active.tagName === 'SELECT' || active.isContentEditable)) return;
        if (document.querySelector('fluent-dialog')?.offsetParent) return;
        if ((e.ctrlKey || e.metaKey) && (e.key === 'a' || e.key === 'A')) {
            if (!root.querySelector('.files-item')) return;
            e.preventDefault();
            dotnet.invokeMethodAsync('SelectAll').catch(() => {});
        } else if (e.key === 'Escape') {
            dotnet.invokeMethodAsync('ClearSelection').catch(() => {});
        }
    };

    state.onDragEnter = e => {
        if (state.internalDrag) return;
        if (!isFileDrag(e)) return;
        e.preventDefault();
        state.dragDepth++;
        showOverlay(state, dropTarget(state, e.target));
    };
    state.onDragOver = e => {
        if (state.onInternalDragOver(e)) return;
        if (!isFileDrag(e)) return;
        e.preventDefault();
        e.dataTransfer.dropEffect = 'copy';
        showOverlay(state, dropTarget(state, e.target));
    };
    state.onDragLeave = e => {
        if (!isFileDrag(e)) return;
        state.dragDepth = Math.max(0, state.dragDepth - 1);
        if (state.dragDepth === 0) hideOverlay(state);
    };
    state.onDrop = async e => {
        if (state.onInternalDrop(e)) return;
        if (!isFileDrag(e)) return;
        e.preventDefault();
        state.dragDepth = 0;
        hideOverlay(state);
        const target = dropTarget(state, e.target);
        const files = await filesFromDataTransfer(e.dataTransfer);
        enqueue(state, files, target.id);
    };
    state.onPaste = e => {
        const active = document.activeElement;
        if (active && (active.tagName === 'INPUT' || active.tagName === 'TEXTAREA' || active.isContentEditable)) return;
        if (document.querySelector('fluent-dialog[open], .fluent-dialog')) return;
        const files = Array.from(e.clipboardData?.files || []);
        if (files.length === 0) return;
        e.preventDefault();
        const stamp = new Date().toISOString().slice(0, 19).replace('T', ' ').replace(/:/g, '.');
        enqueue(state, files.map((file, i) => ({
            // A pasted screenshot is called "image.png"; give it a name worth keeping.
            file: file.name && file.name !== 'image.png' ? file : new File([file], `Pasted ${stamp}${files.length > 1 ? ` (${i + 1})` : ''}.${(file.type.split('/')[1] || 'png').replace('jpeg', 'jpg')}`, { type: file.type }),
            relativeDir: '',
        })), state.folderId);
    };
    state.onBeforeUnload = e => {
        if (state.items.some(x => x.status === 'queued' || x.status === 'uploading')) {
            e.preventDefault();
            e.returnValue = '';
        }
    };

    root.addEventListener('dragstart', state.onDragStart);
    root.addEventListener('dragend', state.onDragEnd);
    root.addEventListener('dragenter', state.onDragEnter);
    root.addEventListener('dragover', state.onDragOver);
    root.addEventListener('dragleave', state.onDragLeave);
    root.addEventListener('drop', state.onDrop);
    document.addEventListener('keydown', state.onKeyDown);
    if (canUpload) {
        document.addEventListener('paste', state.onPaste);
        window.addEventListener('beforeunload', state.onBeforeUnload);
    }

    return {
        setFolder(folderId, folderName) {
            state.folderId = folderId || null;
            state.folderName = folderName || 'Files';
        },
        pick(folderId) {
            state.pickFolder = folderId || state.folderId;
            input.click();
        },
        cancel(id) {
            const item = state.items.find(x => x.id === id);
            if (!item) return;
            if (item.xhr) item.xhr.abort();
            if (item.status === 'queued' || item.status === 'uploading') {
                item.status = 'cancelled';
                report(state, true);
                pump(state);
            }
        },
        retry(id) {
            const item = state.items.find(x => x.id === id);
            if (!item || (item.status !== 'failed' && item.status !== 'cancelled')) return;
            item.status = 'queued';
            item.error = null;
            item.loaded = 0;
            report(state, true);
            pump(state);
        },
        clearFinished() {
            state.items = state.items.filter(x => x.status === 'queued' || x.status === 'uploading');
            report(state, true);
        },
        dispose() {
            state.disposed = true;
            root.removeEventListener('dragstart', state.onDragStart);
            root.removeEventListener('dragend', state.onDragEnd);
            document.removeEventListener('keydown', state.onKeyDown);
            root.removeEventListener('dragenter', state.onDragEnter);
            root.removeEventListener('dragover', state.onDragOver);
            root.removeEventListener('dragleave', state.onDragLeave);
            root.removeEventListener('drop', state.onDrop);
            document.removeEventListener('paste', state.onPaste);
            window.removeEventListener('beforeunload', state.onBeforeUnload);
            state.items.forEach(x => x.xhr?.abort());
            overlay.remove();
            input.remove();
        },
    };
}

// ---------- Drop targets and the overlay ----------

function dropTarget(state, element) {
    const folder = element?.closest?.('[data-drop-folder]');
    if (folder && state.root.contains(folder)) {
        return { id: folder.getAttribute('data-drop-folder') || null, name: folder.getAttribute('data-drop-name') || 'this folder', element: folder };
    }
    return { id: state.folderId, name: state.folderName, element: null };
}

function showOverlay(state, target) {
    state.root.classList.add('is-dragging');
    state.overlay.querySelector('.files-drop-text').textContent = `Drop to upload to ${target.name}`;
    state.root.querySelectorAll('.is-drop-target').forEach(x => x !== target.element && x.classList.remove('is-drop-target'));
    target.element?.classList.add('is-drop-target');
}

function hideOverlay(state) {
    state.root.classList.remove('is-dragging');
    state.root.querySelectorAll('.is-drop-target').forEach(x => x.classList.remove('is-drop-target'));
}

// Files and, for a dropped folder, every file inside it with the folders it sits in.
async function filesFromDataTransfer(dataTransfer) {
    const entries = Array.from(dataTransfer.items || [])
        .filter(x => x.kind === 'file')
        .map(x => (x.webkitGetAsEntry ? x.webkitGetAsEntry() : null));

    if (entries.length === 0 || entries.some(x => !x)) {
        return Array.from(dataTransfer.files || []).map(file => ({ file, relativeDir: '' }));
    }

    const out = [];

    async function walk(entry, dir) {
        if (out.length >= MAX_FILES_PER_DROP) return;
        if (entry.isFile) {
            const file = await new Promise((resolve, reject) => entry.file(resolve, reject)).catch(() => null);
            if (file && !file.name.startsWith('.')) out.push({ file, relativeDir: dir });
        } else if (entry.isDirectory) {
            const reader = entry.createReader();
            const path = dir ? `${dir}/${entry.name}` : entry.name;
            for (;;) {
                const batch = await new Promise((resolve, reject) => reader.readEntries(resolve, reject)).catch(() => []);
                if (batch.length === 0) break;
                for (const child of batch) await walk(child, path);
            }
        }
    }

    for (const entry of entries) await walk(entry, '');
    return out;
}

// ---------- The queue ----------

function enqueue(state, files, folderId) {
    if (state.disposed || files.length === 0) return;

    for (const { file, relativeDir } of files.slice(0, MAX_FILES_PER_DROP)) {
        state.items.push({
            id: state.nextId++,
            file,
            name: file.name,
            relativeDir,
            folderId: folderId || null,
            status: 'queued',
            loaded: 0,
            total: file.size,
            error: null,
            finalName: null,
            fileId: null,
            xhr: null,
        });
    }

    report(state, true);
    pump(state);
}

function pump(state) {
    while (!state.disposed && state.active < CONCURRENCY) {
        const next = state.items.find(x => x.status === 'queued');
        if (!next) return;
        state.active++;
        next.status = 'uploading';
        upload(state, next).finally(() => {
            state.active--;
            pump(state);
        });
    }
}

async function upload(state, item) {
    try {
        let folderId = item.folderId;

        if (item.relativeDir) {
            folderId = await folderFor(state, item.folderId, item.relativeDir);
            if (folderId === null) throw new Error(`Couldn't make the folder "${item.relativeDir}".`);
        }

        const media = await measure(item.file);

        if (item.status !== 'uploading') return; // Cancelled while measuring.

        const form = new FormData();
        form.append('file', item.file, item.file.name);
        if (folderId) form.append('folderId', folderId);
        if (media.width) form.append('width', String(media.width));
        if (media.height) form.append('height', String(media.height));
        if (media.duration) form.append('durationSeconds', String(media.duration));
        if (media.poster) form.append('poster', media.poster, 'poster.jpg');

        const response = await send(state, item, form);

        if (response.ok && response.data) {
            item.status = 'done';
            item.loaded = item.total;
            item.finalName = response.data.fileName;
            item.fileId = response.data.id;
            item.folderId = folderId;
            report(state, true);
            await state.dotnet.invokeMethodAsync('OnUploadFinished', snapshot(item)).catch(() => {});
        } else if (item.status !== 'cancelled') {
            item.status = 'failed';
            item.error = response.error || 'The upload failed.';
            report(state, true);
        }
    } catch (error) {
        if (item.status !== 'cancelled') {
            item.status = 'failed';
            item.error = error?.message || 'The upload failed.';
            report(state, true);
        }
    } finally {
        item.xhr = null;
    }
}

function send(state, item, form) {
    return new Promise(resolve => {
        const xhr = new XMLHttpRequest();
        item.xhr = xhr;
        xhr.open('POST', 'api/files/upload');
        xhr.responseType = 'json';
        xhr.upload.onprogress = e => {
            if (e.lengthComputable) {
                // The form adds a little framing; keep the file's own size as the total.
                item.loaded = Math.min(item.total, Math.round(item.total * (e.loaded / e.total)));
                report(state, false);
            }
        };
        xhr.onload = () => {
            const body = xhr.response || {};
            if (xhr.status >= 200 && xhr.status < 300 && body.isSuccess) {
                resolve({ ok: true, data: body.data });
            } else if (xhr.status === 413) {
                resolve({ ok: false, error: 'The file is too big.' });
            } else if (xhr.status === 401 || xhr.status === 403) {
                resolve({ ok: false, error: 'You need to sign in again.' });
            } else {
                resolve({ ok: false, error: body.error || `The upload failed (${xhr.status}).` });
            }
        };
        xhr.onerror = () => resolve({ ok: false, error: 'The connection dropped. Try again.' });
        xhr.onabort = () => resolve({ ok: false, error: 'Cancelled.' });
        xhr.send(form);
    });
}

// One request per folder path, however many files are inside it.
function folderFor(state, parentId, relativeDir) {
    const key = `${parentId || ''}|${relativeDir}`;
    if (!state.folderPaths.has(key)) {
        state.folderPaths.set(key, state.dotnet.invokeMethodAsync('EnsureFolderPath', parentId, relativeDir).catch(() => null));
    }
    return state.folderPaths.get(key);
}

// ---------- Measuring ----------

async function measure(file) {
    const type = file.type || '';

    try {
        if (type.startsWith('image/') && type !== 'image/svg+xml' && 'createImageBitmap' in window) {
            const bitmap = await createImageBitmap(file);
            const result = { width: bitmap.width, height: bitmap.height };
            bitmap.close?.();
            return result;
        }

        if (type.startsWith('video/')) {
            return await measureVideo(file);
        }

        if (type.startsWith('audio/')) {
            return await measureAudio(file);
        }
    } catch {
        // Not knowing a size or length is fine; the upload goes ahead without it.
    }

    return {};
}

function withTimeout(promise, ms) {
    return Promise.race([promise, new Promise(resolve => setTimeout(() => resolve({}), ms))]);
}

function measureAudio(file) {
    return withTimeout(new Promise(resolve => {
        const url = URL.createObjectURL(file);
        const audio = new Audio();
        audio.preload = 'metadata';
        audio.onloadedmetadata = () => { resolve({ duration: isFinite(audio.duration) ? audio.duration : null }); URL.revokeObjectURL(url); };
        audio.onerror = () => { resolve({}); URL.revokeObjectURL(url); };
        audio.src = url;
    }), 8000);
}

// The video's size and length, and a JPEG of a frame about a second in for its thumbnail.
function measureVideo(file) {
    return withTimeout(new Promise(resolve => {
        const url = URL.createObjectURL(file);
        const video = document.createElement('video');
        video.preload = 'metadata';
        video.muted = true;
        video.playsInline = true;
        const done = result => { resolve(result); URL.revokeObjectURL(url); video.removeAttribute('src'); video.load(); };

        video.onloadedmetadata = () => {
            const info = { width: video.videoWidth, height: video.videoHeight, duration: isFinite(video.duration) ? video.duration : null };
            video.onseeked = () => {
                try {
                    const scale = Math.min(1, 960 / Math.max(video.videoWidth, video.videoHeight));
                    const canvas = document.createElement('canvas');
                    canvas.width = Math.max(1, Math.round(video.videoWidth * scale));
                    canvas.height = Math.max(1, Math.round(video.videoHeight * scale));
                    canvas.getContext('2d').drawImage(video, 0, 0, canvas.width, canvas.height);
                    canvas.toBlob(blob => done({ ...info, poster: blob || null }), 'image/jpeg', 0.82);
                } catch {
                    done(info);
                }
            };
            video.currentTime = Math.min(1, (info.duration || 2) / 2);
        };
        video.onerror = () => done({});
        video.src = url;
    }), 12000);
}

// ---------- Telling .NET ----------

function snapshot(item) {
    return {
        id: item.id,
        name: item.name,
        relativeDir: item.relativeDir || null,
        folderId: item.folderId,
        status: item.status,
        loaded: item.loaded,
        total: item.total,
        error: item.error,
        finalName: item.finalName,
        fileId: item.fileId,
    };
}

function report(state, now) {
    if (state.disposed) return;

    const send = () => {
        state.lastReport = Date.now();
        state.reportTimer = null;
        state.dotnet.invokeMethodAsync('OnUploadsChanged', state.items.map(snapshot)).catch(() => {});
    };

    if (now || Date.now() - state.lastReport >= PROGRESS_EVERY_MS) {
        if (state.reportTimer) clearTimeout(state.reportTimer);
        send();
    } else if (!state.reportTimer) {
        state.reportTimer = setTimeout(send, PROGRESS_EVERY_MS);
    }
}


// ---------- Zip download and Copy link ----------

// Asks the server to prepare a zip of the selection, then lets the browser download it like any
// file, so a big zip streams to disk. Returns null when it started, or the reason it couldn't.
export async function downloadZip(fileIds, folderIds) {
    try {
        const response = await fetch('api/files/zip', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ fileIds, folderIds }),
            credentials: 'same-origin',
        });
        const body = await response.json().catch(() => ({}));
        if (!response.ok || !body.isSuccess) return body.error || 'Couldn\'t prepare the download.';
        const link = document.createElement('a');
        link.href = body.data;
        link.download = '';
        link.style.display = 'none';
        document.body.appendChild(link);
        link.click();
        link.remove();
        return null;
    } catch {
        return 'Couldn\'t prepare the download.';
    }
}

export async function copyText(text) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        return false;
    }
}
