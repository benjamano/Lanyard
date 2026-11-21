// Browser-side parts of the Music page and the music timeline widget.
//
// The server only tells the page about things that change (a song starts, a pause, a seek), and
// this module moves the timeline on between those itself. That's what keeps a page left open on a
// playing kiosk from re-rendering every second over the Blazor circuit.

const progressState = new WeakMap();

function formatTime(seconds) {
    const s = Math.max(0, Math.floor(seconds));
    const m = Math.floor(s / 60);
    return `${m}:${String(s % 60).padStart(2, '0')}`;
}

function paintProgress(root, state, position) {
    const length = state.length;
    const clamped = length > 0 ? Math.min(Math.max(position, 0), length) : Math.max(position, 0);
    const whole = Math.floor(clamped);

    if (!state.dragging) {
        state.range.value = String(clamped);
    }

    const shown = state.dragging ? Number(state.range.value) : clamped;
    const pct = length > 0 ? (shown / length) * 100 : 0;
    root.style.setProperty('--music-progress', `${pct}%`);

    // Text only changes once a second; skip the DOM write otherwise.
    const shownWhole = Math.floor(shown);
    if (state.lastWhole !== shownWhole || state.lastRemaining !== state.showRemaining) {
        state.lastWhole = shownWhole;
        state.lastRemaining = state.showRemaining;
        if (state.elapsed) {
            state.elapsed.textContent = state.showRemaining && length > 0
                ? `-${formatTime(length - shown)}`
                : formatTime(shown);
        }
        state.range.setAttribute('aria-valuetext', `${formatTime(shown)} of ${formatTime(length)}`);
    }

    return whole;
}

function tick(root) {
    const state = progressState.get(root);
    if (!state || !root.isConnected) {
        return;
    }

    const elapsed = state.playing ? (performance.now() - state.anchorAt) / 1000 : 0;
    paintProgress(root, state, state.anchorPosition + elapsed);

    if (state.playing && !document.hidden) {
        state.frame = requestAnimationFrame(() => tick(root));
    } else {
        state.frame = 0;
    }
}

function restart(root, state) {
    if (state.frame) {
        cancelAnimationFrame(state.frame);
        state.frame = 0;
    }
    tick(root);
}

/** Wires up a progress bar: a range input, an elapsed label and a length label inside `root`. */
export function initProgress(root, dotNetRef) {
    if (!root || progressState.has(root)) {
        return;
    }

    const state = {
        range: root.querySelector('input[type=range]'),
        elapsed: root.querySelector('[data-music-elapsed]'),
        total: root.querySelector('[data-music-total]'),
        anchorPosition: 0,
        anchorAt: performance.now(),
        length: 0,
        playing: false,
        dragging: false,
        showRemaining: false,
        frame: 0,
        lastWhole: -1,
        lastRemaining: null,
        dotNetRef
    };

    progressState.set(root, state);

    const startDrag = () => { state.dragging = true; };
    state.range.addEventListener('pointerdown', startDrag);
    state.range.addEventListener('keydown', startDrag);
    state.range.addEventListener('input', () => {
        state.dragging = true;
        paintProgress(root, state, Number(state.range.value));
    });
    state.range.addEventListener('change', async () => {
        const target = Number(state.range.value);
        // Assume the seek lands, so the thumb doesn't spring back while the kiosk catches up.
        state.anchorPosition = target;
        state.anchorAt = performance.now();
        state.dragging = false;
        restart(root, state);
        try {
            await state.dotNetRef.invokeMethodAsync('SeekTo', target);
        } catch {
            // The page was closed or the circuit dropped; nothing to tell.
        }
    });

    if (state.elapsed) {
        state.elapsed.addEventListener('click', () => {
            state.showRemaining = !state.showRemaining;
            paintProgress(root, state, Number(state.range.value));
        });
    }

    document.addEventListener('visibilitychange', () => restart(root, state));
}

/** New position from the server. `position` is where the song was when the server answered. */
export function setProgress(root, position, length, playing, readOnly) {
    const state = progressState.get(root);
    if (!state) {
        return;
    }

    state.anchorPosition = position;
    state.anchorAt = performance.now();
    state.length = length;
    state.playing = playing;
    state.range.max = String(length > 0 ? length : 1);
    state.range.disabled = length <= 0 || !!readOnly;
    if (state.total) {
        state.total.textContent = formatTime(length);
    }
    state.lastWhole = -1;
    restart(root, state);
}

export function disposeProgress(root) {
    const state = progressState.get(root);
    if (state?.frame) {
        cancelAnimationFrame(state.frame);
    }
    progressState.delete(root);
}

/** A volume slider: follows the thumb locally, tells the server once when it's let go. */
export function initVolume(root, dotNetRef) {
    if (!root || root.dataset.musicVolumeReady) {
        return;
    }
    root.dataset.musicVolumeReady = '1';

    const range = root.querySelector('input[type=range]');
    const label = root.querySelector('[data-music-volume-label]');
    const paint = () => {
        root.style.setProperty('--music-volume', `${range.value}%`);
        if (label) {
            label.textContent = `${range.value}%`;
        }
    };

    range.addEventListener('input', paint);
    range.addEventListener('change', async () => {
        paint();
        try {
            await dotNetRef.invokeMethodAsync('CommitVolume', Number(range.value));
        } catch {
        }
    });
}

export function setVolume(root, volume) {
    const range = root?.querySelector('input[type=range]');
    // Don't move the thumb under someone's finger.
    if (!range || document.activeElement === range && range.matches(':active')) {
        return;
    }
    range.value = String(volume);
    root.style.setProperty('--music-volume', `${volume}%`);
    const label = root.querySelector('[data-music-volume-label]');
    if (label) {
        label.textContent = `${volume}%`;
    }
}

/** Shows `bar` (a compact player) whenever `card` (the full one) is scrolled out of view. */
export function observeCard(card, bar) {
    if (!card || !bar || !('IntersectionObserver' in window)) {
        return;
    }

    const observer = new IntersectionObserver(([entry]) => {
        bar.classList.toggle('is-visible', !entry.isIntersecting);
    }, { threshold: 0, rootMargin: '-64px 0px 0px 0px' });

    observer.observe(card);
    bar._musicObserver = observer;
}

export function unobserveCard(bar) {
    bar?._musicObserver?.disconnect();
}

export function scrollIntoView(element) {
    element?.scrollIntoView({ behavior: 'smooth', block: 'start' });
}

/** Places a menu next to the button that opened it, keeping it on screen. */
export function positionMenu(menu, anchor) {
    if (!menu || !anchor) {
        return;
    }

    // Phones show the menu as a sheet along the bottom (CSS); nothing to place.
    if (window.matchMedia('(max-width: 599px)').matches) {
        menu.style.top = '';
        menu.style.left = '';
        menu.querySelector('button')?.focus();
        return;
    }

    const rect = anchor.getBoundingClientRect();
    const width = menu.offsetWidth;
    const height = menu.offsetHeight;
    let top = rect.bottom + 4;
    if (top + height > window.innerHeight - 8) {
        top = Math.max(8, rect.top - height - 4);
    }
    let left = rect.right - width;
    left = Math.min(Math.max(8, left), window.innerWidth - width - 8);

    menu.style.top = `${top}px`;
    menu.style.left = `${left}px`;
    menu.querySelector('button')?.focus();
}

/** positionMenu, with the anchor found by a selector inside `scope` (rows are virtualised). */
export function positionMenuFor(menu, scope, selector) {
    positionMenu(menu, scope?.querySelector(selector));
}

/**
 * Drag-to-reorder for a track list. Rows carry data-song-id and a [data-drag-handle]; only the
 * handle starts a drag, so clicking a row still plays it. Tells .NET the song and its new index.
 */
export function initReorder(list, dotNetRef) {
    if (!list || list.dataset.musicReorderReady) {
        return;
    }
    list.dataset.musicReorderReady = '1';

    let dragged = null;

    list.addEventListener('pointerdown', (e) => {
        const handle = e.target.closest('[data-drag-handle]');
        const row = handle?.closest('[data-song-id]');
        if (row) {
            row.setAttribute('draggable', 'true');
        }
    });

    list.addEventListener('dragstart', (e) => {
        dragged = e.target.closest('[data-song-id]');
        if (!dragged) {
            return;
        }
        dragged.classList.add('is-dragging');
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', dragged.dataset.songId);
    });

    list.addEventListener('dragover', (e) => {
        if (!dragged) {
            return;
        }
        const row = e.target.closest('[data-song-id]');
        if (!row || row === dragged) {
            return;
        }
        e.preventDefault();
        list.querySelectorAll('.drop-before, .drop-after').forEach(x => x.classList.remove('drop-before', 'drop-after'));
        const rect = row.getBoundingClientRect();
        row.classList.add(e.clientY < rect.top + rect.height / 2 ? 'drop-before' : 'drop-after');
    });

    list.addEventListener('drop', async (e) => {
        if (!dragged) {
            return;
        }
        e.preventDefault();
        const row = e.target.closest('[data-song-id]');
        const before = row?.classList.contains('drop-before');
        list.querySelectorAll('.drop-before, .drop-after').forEach(x => x.classList.remove('drop-before', 'drop-after'));
        if (!row || row === dragged) {
            return;
        }
        let index = Number(row.dataset.index) + (before ? 0 : 1);
        if (Number(dragged.dataset.index) < index) {
            index -= 1;
        }
        const songId = dragged.dataset.songId;
        try {
            await dotNetRef.invokeMethodAsync('MoveSongTo', songId, index);
        } catch {
        }
    });

    list.addEventListener('dragend', () => {
        dragged?.classList.remove('is-dragging');
        dragged?.removeAttribute('draggable');
        list.querySelectorAll('.drop-before, .drop-after').forEach(x => x.classList.remove('drop-before', 'drop-after'));
        dragged = null;
    });
}

/** Scrolls the selected chip of each sideways-scrolling row inside `root` into view. */
export function revealSelected(root) {
    root?.querySelectorAll('.music-kiosk-chip.is-selected, .music-playlist-item.is-selected').forEach(el => {
        const row = el.parentElement;
        if (row && row.scrollWidth > row.clientWidth) {
            const left = el.offsetLeft - row.offsetLeft - 12;
            row.scrollTo({ left: Math.max(0, left), behavior: 'smooth' });
        }
    });
}

const storageKeyPrefix = 'staff.music.';

export function load(key) {
    try {
        return localStorage.getItem(storageKeyPrefix + key);
    } catch {
        return null;
    }
}

export function save(key, value) {
    try {
        if (value === null || value === undefined || value === '') {
            localStorage.removeItem(storageKeyPrefix + key);
        } else {
            localStorage.setItem(storageKeyPrefix + key, value);
        }
    } catch {
    }
}
