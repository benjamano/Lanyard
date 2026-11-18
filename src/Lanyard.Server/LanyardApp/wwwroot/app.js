// Writes the ASP.NET culture cookie from the interactive account page (a Blazor circuit has no
// HTTP response to set cookies on). The page then reloads so the new circuit picks it up.
window.appCulture = {
    set(cookie) {
        document.cookie = cookie;
    }
};
