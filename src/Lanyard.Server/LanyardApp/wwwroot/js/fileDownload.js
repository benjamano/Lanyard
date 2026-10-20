// fileDownload.js
//
// Saves a file the server built in memory and streamed over the Blazor circuit (a
// DotNetStreamReference), e.g. the timesheet CSV export. Going through the circuit means the
// download is authorised by the same signed-in location scope as the page that asked for it,
// with no separate download URL that could be opened or cached on its own.
window.lanyardDownload = {
    fromStream: async function (fileName, contentType, streamRef) {
        const buffer = await streamRef.arrayBuffer();
        const blob = new Blob([buffer], { type: contentType });
        const url = URL.createObjectURL(blob);

        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = fileName;
        anchor.style.display = 'none';
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();

        // Safari starts the download asynchronously, so give it a moment before revoking.
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    }
};
