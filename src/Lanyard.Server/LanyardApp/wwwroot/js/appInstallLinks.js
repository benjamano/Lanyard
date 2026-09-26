// appInstallLinks.js
// Points the page's manifest, apple-touch-icon and iPhone home-screen title at the current company,
// so installing Lanyard (Android "Install app", iPhone "Add to Home Screen") uses its name and logo. App.razor
// renders them server-side when it already knows the company; this covers the cases it can't:
// picking a company on /login, which only changes the query string within the circuit.
// Browsers watch the manifest link, so changing its href is enough for the next install prompt.
(() => {
    const cookieName = 'lanyard.app-company';

    const setHref = (selector, href) => {
        const link = document.querySelector(selector);
        if (link && typeof href === 'string' && href.length > 0 && link.getAttribute('href') !== href) {
            link.setAttribute('href', href);
        }
    };

    window.lanyardSetAppCompany = (companyId, manifestHref, appleTouchIconHref, appName) => {
        if (!Number.isInteger(companyId)) {
            return;
        }

        setHref('link[rel="manifest"]', manifestHref);
        setHref('link[rel="apple-touch-icon"]', appleTouchIconHref);

        const title = document.querySelector('meta[name="apple-mobile-web-app-title"]');
        if (title && typeof appName === 'string' && appName.length > 0) {
            title.setAttribute('content', appName);
        }

        const secure = location.protocol === 'https:' ? '; Secure' : '';
        document.cookie = `${cookieName}=${companyId}; path=/; max-age=31536000; SameSite=Lax${secure}`;
    };
})();
