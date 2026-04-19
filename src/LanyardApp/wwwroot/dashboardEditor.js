let currentResizeState = null;

export function getGridCoordinates(canvasId, clientX, clientY, columns, rowHeight) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) {
        return [0, 0];
    }

    const rect = canvas.getBoundingClientRect();
    const relativeX = Math.max(0, clientX - rect.left);
    const relativeY = Math.max(0, clientY - rect.top);

    const columnWidth = rect.width / columns;
    const x = Math.floor(relativeX / columnWidth);
    const y = Math.floor(relativeY / rowHeight);

    return [x, y];
}

export function startWidgetResize(dotNetRef, widgetId, canvasId, startClientX, startClientY, startGridW, startGridH, columns, rowHeight) {
    stopWidgetResize();

    const canvas = document.getElementById(canvasId);
    const canvasWidth = canvas ? canvas.getBoundingClientRect().width : window.innerWidth;
    const columnWidth = canvasWidth <= 0 ? 1 : canvasWidth / columns;

    currentResizeState = {
        dotNetRef,
        widgetId,
        startClientX,
        startClientY,
        startGridW,
        startGridH,
        columns,
        rowHeight,
        columnWidth,
        onMove: null,
        onUp: null
    };

    currentResizeState.onMove = (event) => {
        if (!currentResizeState) {
            return;
        }

        const deltaX = event.clientX - currentResizeState.startClientX;
        const deltaY = event.clientY - currentResizeState.startClientY;

        const widthDeltaGrid = Math.round(deltaX / currentResizeState.columnWidth);
        const heightDeltaGrid = Math.round(deltaY / currentResizeState.rowHeight);

        const gridW = Math.max(1, currentResizeState.startGridW + widthDeltaGrid);
        const gridH = Math.max(1, currentResizeState.startGridH + heightDeltaGrid);

        currentResizeState.dotNetRef.invokeMethodAsync("OnWidgetResizeChanged", currentResizeState.widgetId, gridW, gridH);
    };

    currentResizeState.onUp = () => {
        stopWidgetResize();
    };

    window.addEventListener("mousemove", currentResizeState.onMove);
    window.addEventListener("mouseup", currentResizeState.onUp, { once: true });
}

function stopWidgetResize() {
    if (!currentResizeState) {
        return;
    }

    if (currentResizeState.onMove) {
        window.removeEventListener("mousemove", currentResizeState.onMove);
    }

    if (currentResizeState.onUp) {
        window.removeEventListener("mouseup", currentResizeState.onUp);
    }

    currentResizeState = null;
}
