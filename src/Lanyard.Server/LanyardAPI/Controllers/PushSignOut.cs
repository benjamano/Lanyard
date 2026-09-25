namespace Lanyard.API.Controllers
{
    // Signing out of a browser also turns off that browser's push notifications, so a shared phone
    // or the till PC doesn't keep showing someone's shift alerts after they've gone. Every sign-out
    // path (nav, /logout, auto-logout, pairing a clock-in tablet) passes through a self-submitting
    // form page, so the unsubscribe happens there: the script unsubscribes the browser, puts the
    // endpoint in a hidden field, then submits. The POST removes the matching row for the person
    // signing out. If anything is slow or fails the form is submitted anyway after a short wait;
    // an unsubscribed browser can't receive pushes, and the server drops the row the next time
    // its push service answers 410.
    internal static class PushSignOut
    {
        public const string EndpointField = "pushEndpoint";

        public static string HiddenField => $"""<input type="hidden" name="{EndpointField}" value="" />""";

        public static string SubmitScript(string formId) => $$"""
            <script>
            (function () {
                var form = document.getElementById('{{formId}}');
                var sent = false;
                function go() { if (!sent) { sent = true; form.submit(); } }
                setTimeout(go, 1500);
                try {
                    if (!('serviceWorker' in navigator)) { go(); return; }
                    navigator.serviceWorker.getRegistration()
                        .then(function (reg) { return reg && reg.pushManager ? reg.pushManager.getSubscription() : null; })
                        .then(function (sub) {
                            if (!sub) { go(); return; }
                            form.elements['{{EndpointField}}'].value = sub.endpoint;
                            return sub.unsubscribe().then(go, go);
                        })
                        .catch(go);
                } catch (e) { go(); }
            })();
            </script>
            """;
    }
}
