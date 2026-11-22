// dashboardDesigner.js
//
// Pointer handling for the dashboard designer (Components/Dashboards/Designer). It only tracks the
// pointer and turns it into grid cells; every layout decision is made in C# by
// DashboardLayoutEngine, which DesignerCanvas calls through the .NET callbacks below:
//
//   PreviewMoveAsync(id, x, y)        the dragged widget is over a new cell
//   PreviewResizeAsync(id, w, h)      a resize handle crossed a cell boundary
//   PreviewPlaceAsync(type, x, y, w, h) a palette item is over a new cell (x = -1: off the canvas)
//   CommitAsync()                     the pointer was released - keep the previewed layout
//   CancelAsync()                     Escape, or dropped off the canvas - put everything back
//   SelectAsync(id | null)            a click (no drag) on a widget, or on empty canvas
//   AddAsync(type)                    a click (no drag) on a palette item
//   KeyAsync(key, shift, ctrl)        a shortcut while the canvas has focus
//
// Pointer events rather than HTML5 drag-and-drop, so the same code works with a mouse, a finger and
// a pen. Previews are only sent when the cell under the pointer changes (a few times a second at
// most), and the dragged widget follows the pointer with a CSS transform in the meantime, so a
// drag never waits on the server to look smooth.
//
// Nothing here adds children to elements Blazor renders: the palette chip lives on document.body,
// which Blazor doesn't manage, so it can never upset Blazor's DOM diffing.

const DRAG_THRESHOLD = 5;

export function scrollToWidget(elementId) {
    document.getElementById(elementId)?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
}

export function init(canvas, palette, dotNet, options) {
    const state = {
        canvas,
        palette,
        dotNet,
        columns: options.columns ?? 12,
        rowHeight: options.rowHeight ?? 120,
        gap: options.gap ?? 8,
        drag: null,
        disposed: false
    };

    const onPointerDown = event => handlePointerDown(state, event);
    const onPointerMove = event => handlePointerMove(state, event);
    const onPointerUp = event => handlePointerUp(state, event);
    const onPointerCancel = () => cancelDrag(state);
    const onKeyDown = event => handleKeyDown(state, event);
    const onWindowKeyDown = event => {
        if (event.key === 'Escape' && state.drag) {
            event.preventDefault();
            cancelDrag(state);
        }
    };

    canvas.addEventListener('pointerdown', onPointerDown);
    palette?.addEventListener('pointerdown', onPointerDown);
    window.addEventListener('pointermove', onPointerMove);
    window.addEventListener('pointerup', onPointerUp);
    window.addEventListener('pointercancel', onPointerCancel);
    canvas.addEventListener('keydown', onKeyDown);
    window.addEventListener('keydown', onWindowKeyDown);

    return {
        setRowHeight(rowHeight) {
            state.rowHeight = rowHeight;
        },
        setPalette(element) {
            state.palette?.removeEventListener('pointerdown', onPointerDown);
            state.palette = element;
            element?.addEventListener('pointerdown', onPointerDown);
        },
        focus() {
            canvas.focus({ preventScroll: true });
        },
        dispose() {
            state.disposed = true;
            removeChip(state);
            canvas.removeEventListener('pointerdown', onPointerDown);
            state.palette?.removeEventListener('pointerdown', onPointerDown);
            window.removeEventListener('pointermove', onPointerMove);
            window.removeEventListener('pointerup', onPointerUp);
            window.removeEventListener('pointercancel', onPointerCancel);
            canvas.removeEventListener('keydown', onKeyDown);
            window.removeEventListener('keydown', onWindowKeyDown);
        }
    };
}

// The grid's geometry right now. Column width follows the canvas's width; rows are a fixed height
// in the designer (DesignerCanvas sets grid-auto-rows to exactly rowHeight), so a y position maps
// to a row with simple arithmetic.
function metrics(state) {
    const grid = state.canvas.querySelector('[data-designer-grid]');
    const rect = (grid ?? state.canvas).getBoundingClientRect();
    const columnWidth = (rect.width - state.gap * (state.columns - 1)) / state.columns;

    return {
        rect,
        columnWidth,
        columnStep: columnWidth + state.gap,
        rowStep: state.rowHeight + state.gap
    };
}

function cellAt(state, clientX, clientY, w) {
    const m = metrics(state);
    const x = Math.round((clientX - m.rect.left) / m.columnStep);
    const y = Math.round((clientY - m.rect.top) / m.rowStep);

    return {
        x: Math.max(0, Math.min(state.columns - (w ?? 1), x)),
        y: Math.max(0, y)
    };
}

function isOverCanvas(state, clientX, clientY) {
    const rect = state.canvas.getBoundingClientRect();

    return clientX >= rect.left && clientX <= rect.right && clientY >= rect.top && clientY <= rect.bottom;
}

function handlePointerDown(state, event) {
    if (event.button !== 0 || state.drag) {
        return;
    }

    const target = event.target;

    // Anything interactive in the inspector-like chrome on a widget (its own buttons) keeps its
    // normal behaviour.
    if (target.closest('[data-designer-ignore]')) {
        return;
    }

    const resizeHandle = target.closest('[data-resize-handle]');
    const item = target.closest('[data-widget-id]');
    const paletteItem = target.closest('[data-palette-type]');

    if (resizeHandle && item) {
        event.preventDefault();
        state.drag = {
            kind: 'resize',
            id: item.dataset.widgetId,
            edge: resizeHandle.dataset.resizeHandle,
            startX: event.clientX,
            startY: event.clientY,
            x: parseInt(item.dataset.x, 10),
            y: parseInt(item.dataset.y, 10),
            w: parseInt(item.dataset.w, 10),
            h: parseInt(item.dataset.h, 10),
            lastW: parseInt(item.dataset.w, 10),
            lastH: parseInt(item.dataset.h, 10),
            started: false,
            pointerId: event.pointerId
        };
        capture(state, event);
        return;
    }

    if (item) {
        const rect = item.getBoundingClientRect();
        state.drag = {
            kind: 'move',
            id: item.dataset.widgetId,
            element: item,
            startX: event.clientX,
            startY: event.clientY,
            offsetX: event.clientX - rect.left,
            offsetY: event.clientY - rect.top,
            w: parseInt(item.dataset.w, 10),
            lastX: parseInt(item.dataset.x, 10),
            lastY: parseInt(item.dataset.y, 10),
            started: false,
            pointerId: event.pointerId
        };

        // Touch scrolls the page unless we claim the gesture; the canvas's own CSS
        // (touch-action: none on widgets) does that, this just stops text selection on mouse.
        if (event.pointerType === 'mouse') {
            event.preventDefault();
        }

        state.canvas.focus({ preventScroll: true });
        capture(state, event);
        return;
    }

    if (paletteItem) {
        state.drag = {
            kind: 'place',
            type: parseInt(paletteItem.dataset.paletteType, 10),
            label: paletteItem.dataset.paletteLabel ?? '',
            w: parseInt(paletteItem.dataset.w, 10) || 2,
            h: parseInt(paletteItem.dataset.h, 10) || 1,
            startX: event.clientX,
            startY: event.clientY,
            lastX: null,
            lastY: null,
            started: false,
            pointerId: event.pointerId
        };

        if (event.pointerType === 'mouse') {
            event.preventDefault();
        }

        capture(state, event);
        return;
    }

    // A plain press on the empty canvas clears the selection (on release, so it isn't a drag).
    if (state.canvas.contains(target)) {
        state.drag = { kind: 'background', startX: event.clientX, startY: event.clientY, started: false, pointerId: event.pointerId };
        state.canvas.focus({ preventScroll: true });
    }
}

function capture(state, event) {
    try {
        event.target.setPointerCapture?.(event.pointerId);
    } catch {
        // Some elements (SVG icons inside Fluent buttons) refuse capture; window listeners still work.
    }
}

function movedPastThreshold(drag, event) {
    return Math.abs(event.clientX - drag.startX) > DRAG_THRESHOLD || Math.abs(event.clientY - drag.startY) > DRAG_THRESHOLD;
}

function handlePointerMove(state, event) {
    const drag = state.drag;

    if (!drag || event.pointerId !== drag.pointerId) {
        return;
    }

    if (!drag.started) {
        if (!movedPastThreshold(drag, event) || drag.kind === 'background') {
            return;
        }

        drag.started = true;
        startVisuals(state, drag);
    }

    event.preventDefault();

    if (drag.kind === 'move') {
        drag.element.style.transform = `translate(${event.clientX - drag.startX}px, ${event.clientY - drag.startY}px)`;

        const cell = cellAt(state, event.clientX - drag.offsetX, event.clientY - drag.offsetY, drag.w);

        if (cell.x !== drag.lastX || cell.y !== drag.lastY) {
            drag.lastX = cell.x;
            drag.lastY = cell.y;
            invoke(state, 'PreviewMoveAsync', drag.id, cell.x, cell.y);
        }

        autoScroll(state, event);
        return;
    }

    if (drag.kind === 'resize') {
        const m = metrics(state);
        let w = drag.w;
        let h = drag.h;

        if (drag.edge.includes('e')) {
            w = Math.max(1, Math.min(state.columns - drag.x, drag.w + Math.round((event.clientX - drag.startX) / m.columnStep)));
        }

        if (drag.edge.includes('s')) {
            h = Math.max(1, drag.h + Math.round((event.clientY - drag.startY) / m.rowStep));
        }

        if (w !== drag.lastW || h !== drag.lastH) {
            drag.lastW = w;
            drag.lastH = h;
            invoke(state, 'PreviewResizeAsync', drag.id, w, h);
        }

        autoScroll(state, event);
        return;
    }

    if (drag.kind === 'place') {
        moveChip(state, event);

        if (!isOverCanvas(state, event.clientX, event.clientY)) {
            if (drag.lastX !== -1) {
                drag.lastX = -1;
                drag.lastY = -1;
                invoke(state, 'PreviewPlaceAsync', drag.type, -1, -1, drag.w, drag.h);
            }

            return;
        }

        // Centre the new widget's top-left cell on the pointer, roughly where it will land.
        const m = metrics(state);
        const cell = cellAt(state, event.clientX - (m.columnStep * drag.w) / 2 + m.columnStep / 2, event.clientY - m.rowStep / 2, drag.w);

        if (cell.x !== drag.lastX || cell.y !== drag.lastY) {
            drag.lastX = cell.x;
            drag.lastY = cell.y;
            invoke(state, 'PreviewPlaceAsync', drag.type, cell.x, cell.y, drag.w, drag.h);
        }

        autoScroll(state, event);
    }
}

function handlePointerUp(state, event) {
    const drag = state.drag;

    if (!drag || event.pointerId !== drag.pointerId) {
        return;
    }

    state.drag = null;
    stopVisuals(state, drag);

    if (!drag.started) {
        // A click, not a drag.
        if (drag.kind === 'move' || drag.kind === 'resize') {
            invoke(state, 'SelectAsync', drag.id);
        } else if (drag.kind === 'place') {
            invoke(state, 'AddAsync', drag.type);
        } else if (drag.kind === 'background') {
            invoke(state, 'SelectAsync', null);
        }

        return;
    }

    if (drag.kind === 'place' && (drag.lastX === null || drag.lastX === -1)) {
        invoke(state, 'CancelAsync');
        return;
    }

    invoke(state, 'CommitAsync');
}

function cancelDrag(state) {
    const drag = state.drag;

    if (!drag) {
        return;
    }

    state.drag = null;
    stopVisuals(state, drag);

    if (drag.started) {
        invoke(state, 'CancelAsync');
    }
}

function startVisuals(state, drag) {
    document.body.classList.add('dashboard-designer-dragging');
    document.body.style.userSelect = 'none';
    document.body.style.cursor = drag.kind === 'resize' ? 'nwse-resize' : 'grabbing';

    if (drag.kind === 'move') {
        drag.element.classList.add('is-dragging');
    }

    if (drag.kind === 'resize') {
        document.body.classList.add('dashboard-designer-resizing');
    }

    if (drag.kind === 'place') {
        const chip = document.createElement('div');
        chip.className = 'dashboard-designer-chip';
        chip.textContent = drag.label;
        // Styled here rather than in a stylesheet: it lives on <body>, outside every component's
        // scoped CSS, and only exists while a drag is in progress.
        chip.style.cssText = [
            'position: fixed', 'left: 0', 'top: 0', 'z-index: 10000', 'pointer-events: none',
            'padding: 6px 12px', 'border-radius: 999px', 'white-space: nowrap',
            'font: 600 13px/1.4 var(--fontFamilyBase, sans-serif)',
            'background: var(--colorBrandBackground, #0f6cbd)', 'color: var(--colorNeutralForegroundOnBrand, #fff)',
            'box-shadow: var(--shadow16, 0 6px 16px rgba(0,0,0,.25))'
        ].join(';');
        document.body.appendChild(chip);
        state.chip = chip;
    }
}

function stopVisuals(state, drag) {
    document.body.classList.remove('dashboard-designer-dragging', 'dashboard-designer-resizing');
    document.body.style.userSelect = '';
    document.body.style.cursor = '';

    if (drag.kind === 'move' && drag.element) {
        drag.element.classList.remove('is-dragging');
        drag.element.style.transform = '';
    }

    removeChip(state);
}

function moveChip(state, event) {
    if (state.chip) {
        state.chip.style.transform = `translate(${event.clientX + 12}px, ${event.clientY + 12}px)`;
    }
}

function removeChip(state) {
    state.chip?.remove();
    state.chip = null;
}

// Scroll the canvas's scroll container (or the page) when a drag nears its top or bottom edge, so a
// widget can be dragged further down than the visible area.
function autoScroll(state, event) {
    const scroller = state.canvas.closest('[data-designer-scroll]') ?? document.scrollingElement;
    const rect = scroller === document.scrollingElement
        ? { top: 0, bottom: window.innerHeight }
        : scroller.getBoundingClientRect();
    const edge = 48;

    if (event.clientY > rect.bottom - edge) {
        scroller.scrollTop += 12;
    } else if (event.clientY < rect.top + edge) {
        scroller.scrollTop -= 12;
    }
}

const HANDLED_KEYS = new Set(['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Delete', 'Backspace', 'Escape']);

function handleKeyDown(state, event) {
    // Leave typing inside a widget (or any input) alone.
    if (event.target !== state.canvas && event.target.closest('input, textarea, [contenteditable="true"]')) {
        return;
    }

    const ctrl = event.ctrlKey || event.metaKey;
    const key = event.key;

    if (HANDLED_KEYS.has(key) || (ctrl && (key === 'd' || key === 'D'))) {
        event.preventDefault();
        invoke(state, 'KeyAsync', key, event.shiftKey, ctrl);
    }
}

function invoke(state, method, ...args) {
    if (state.disposed) {
        return;
    }

    state.dotNet.invokeMethodAsync(method, ...args).catch(() => {
        // The circuit went away mid-drag; nothing to undo on this side.
    });
}
