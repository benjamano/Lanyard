// demoBranding.js
// "See Lanyard in your own branding" for demo visitors. The demo company is shared by everyone, so a
// visitor's name, colour and logo are kept only in this browser (localStorage) and never sent to
// the server as files. The logo itself never crosses the Blazor circuit either (it would blow the
// 32 KB message limit): .NET only ever gets short blob: URLs for it, rebuilt from storage on load.
//
// Two copies of the logo: the saved one (what the app is showing) and the dialog's working one
// (what's being previewed). They share a blob: URL until the visitor picks something new, and a
// URL is only released once neither the app nor the dialog can still be showing it.
window.lanyardDemoBranding = (() => {
    const storageKey = 'lanyard:demo-branding';
    const promptKey = 'lanyard:demo-branding-prompt-dismissed';
    const maxFileBytes = 5 * 1024 * 1024;
    const maxLogoSide = 384;

    const saved = { dataUrl: null, blobUrl: null };
    const working = { dataUrl: null, blobUrl: null };
    let originalFavicon = null;
    let previewIconUrls = [];

    const tryStorage = fn => { try { return fn(); } catch { return null; } };

    const readStored = () => tryStorage(() => {
        const value = JSON.parse(localStorage.getItem(storageKey));
        return value && typeof value === 'object' ? value : null;
    });

    const loadImage = src => new Promise((resolve, reject) => {
        const img = new Image();
        img.onload = () => resolve(img);
        img.onerror = () => reject(new Error("That file couldn't be read as an image."));
        img.src = src;
    });

    const readFile = file => new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(reader.result);
        reader.onerror = () => reject(new Error("That file couldn't be read."));
        reader.readAsDataURL(file);
    });

    // Decoded by hand rather than fetch(dataUrl): the page's CSP (connect-src) doesn't allow data: fetches.
    const toBlobUrl = async dataUrl => {
        const [header, base64] = dataUrl.split(',', 2);
        const type = (/^data:([^;]+)/.exec(header) || [])[1] || 'image/png';
        const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
        return URL.createObjectURL(new Blob([bytes], { type }));
    };

    const releaseWorking = () => {
        if (working.blobUrl && working.blobUrl !== saved.blobUrl) {
            URL.revokeObjectURL(working.blobUrl);
        }
    };

    const useSavedAsWorking = () => {
        releaseWorking();
        working.dataUrl = saved.dataUrl;
        working.blobUrl = saved.blobUrl;
        return working.blobUrl;
    };

    // Shrinks the picked image so it fits comfortably in localStorage.
    const downscale = async file => {
        if (!file.type.startsWith('image/')) {
            throw new Error('Please choose an image file (PNG, JPG, SVG...).');
        }

        if (file.size > maxFileBytes) {
            throw new Error('That image is over 5 MB - please choose a smaller one.');
        }

        const img = await loadImage(await readFile(file));
        const width = img.naturalWidth || maxLogoSide;
        const height = img.naturalHeight || maxLogoSide;
        const scale = Math.min(1, maxLogoSide / Math.max(width, height));
        const canvas = document.createElement('canvas');
        canvas.width = Math.max(1, Math.round(width * scale));
        canvas.height = Math.max(1, Math.round(height * scale));
        canvas.getContext('2d').drawImage(img, 0, 0, canvas.width, canvas.height);

        return canvas.toDataURL('image/png');
    };

    // An app/favicon icon the way Lanyard makes them for real companies: the logo centred on a
    // white rounded square (AppIconService), optionally with the λ badge in the corner.
    const makeIcon = async (logoDataUrl, size, withBadge) => {
        const canvas = document.createElement('canvas');
        canvas.width = canvas.height = size;
        const ctx = canvas.getContext('2d');

        ctx.fillStyle = '#ffffff';
        ctx.beginPath();
        ctx.roundRect(0, 0, size, size, size * 0.22);
        ctx.fill();

        if (logoDataUrl) {
            const img = await loadImage(logoDataUrl);
            const box = size * 0.72;
            const scale = Math.min(box / img.naturalWidth, box / img.naturalHeight);
            const w = img.naturalWidth * scale;
            const h = img.naturalHeight * scale;
            ctx.drawImage(img, (size - w) / 2, (size - h) / 2, w, h);
        }

        if (withBadge) {
            ctx.fillStyle = '#1f1f1f';
            ctx.font = `bold ${Math.round(size * 0.2)}px sans-serif`;
            ctx.textAlign = 'right';
            ctx.textBaseline = 'bottom';
            ctx.fillText('λ', size * 0.9, size * 0.95);
        }

        return canvas.toDataURL('image/png');
    };

    // The tab's favicon follows the saved logo, and goes back to Lanyard's own without one.
    const applyFavicon = async () => {
        const link = document.querySelector('link[rel="icon"]');

        if (!link) {
            return;
        }

        if (originalFavicon === null) {
            originalFavicon = link.getAttribute('href');
        }

        link.setAttribute('href', saved.dataUrl ? await makeIcon(saved.dataUrl, 64, false) : originalFavicon);
    };

    return {
        // Returns { name, color, logoUrl } (logoUrl a blob: URL) or null, and puts the saved
        // logo's favicon in the tab.
        load: async () => {
            const stored = readStored();

            if (saved.blobUrl) {
                URL.revokeObjectURL(saved.blobUrl);
            }

            saved.dataUrl = stored && typeof stored.logo === 'string' ? stored.logo : null;
            saved.blobUrl = saved.dataUrl ? await toBlobUrl(saved.dataUrl) : null;
            useSavedAsWorking();
            await applyFavicon();

            return stored ? { name: stored.name ?? null, color: stored.color ?? null, logoUrl: saved.blobUrl } : null;
        },

        // Saves the name and colour with the dialog's working logo, and returns the logo's URL.
        save: async (name, color) => {
            tryStorage(() => localStorage.setItem(storageKey, JSON.stringify({ name, color, logo: working.dataUrl })));

            if (saved.blobUrl && saved.blobUrl !== working.blobUrl) {
                URL.revokeObjectURL(saved.blobUrl);
            }

            saved.dataUrl = working.dataUrl;
            saved.blobUrl = working.blobUrl;
            await applyFavicon();

            return saved.blobUrl;
        },

        clear: async () => {
            tryStorage(() => localStorage.removeItem(storageKey));
            releaseWorking();

            if (saved.blobUrl) {
                URL.revokeObjectURL(saved.blobUrl);
            }

            saved.dataUrl = saved.blobUrl = working.dataUrl = working.blobUrl = null;
            await applyFavicon();
        },

        // The dialog starts from, and a cancel goes back to, the saved logo.
        revertLogo: async () => useSavedAsWorking(),

        removeLogo: async () => {
            releaseWorking();
            working.dataUrl = working.blobUrl = null;
        },

        // Listens for a file being picked; hands .NET a blob: URL (or an error message).
        watchLogoInput: (input, dotnet) => {
            if (!input || input.dataset.lanyardWatching) {
                return;
            }

            input.dataset.lanyardWatching = '1';
            input.addEventListener('change', async () => {
                const file = input.files && input.files[0];
                input.value = '';

                if (!file) {
                    return;
                }

                try {
                    const dataUrl = await downscale(file);
                    releaseWorking();
                    working.dataUrl = dataUrl;
                    working.blobUrl = await toBlobUrl(dataUrl);
                    await dotnet.invokeMethodAsync('OnLogoPicked', working.blobUrl, null);
                } catch (e) {
                    await dotnet.invokeMethodAsync('OnLogoPicked', null, e && e.message ? e.message : "That image couldn't be used.");
                }
            });
        },

        // blob: URLs for the preview's browser tab and home-screen icon, from the working logo.
        // The previous pair is released each time, since the preview asks again on every change.
        icons: async () => {
            previewIconUrls.forEach(url => URL.revokeObjectURL(url));
            previewIconUrls = [
                await toBlobUrl(await makeIcon(working.dataUrl, 64, false)),
                await toBlobUrl(await makeIcon(working.dataUrl, 192, true)),
            ];
            return { favicon: previewIconUrls[0], appIcon: previewIconUrls[1] };
        },

        promptDismissed: () => tryStorage(() => localStorage.getItem(promptKey) === '1') === true,
        dismissPrompt: () => { tryStorage(() => localStorage.setItem(promptKey, '1')); },
    };
})();
