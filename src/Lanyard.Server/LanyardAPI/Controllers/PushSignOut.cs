namespace Lanyard.API.Controllers
{
    // Signing out of a browser also turns off that browser's push notifications, so a shared phone
    // or the till PC doesn't keep showing someone's shift alerts after they've gone. The sign-out
    // paths pass through a self-submitting form page, so the unsubscribe happens there: the script
    // unsubscribes the browser, puts the endpoint in a hidden field, then submits. The POST removes
    // the matching row for the person signing out. If anything is slow or fails the form is
    // submitted anyway after a short wait; an unsubscribed browser can't receive pushes, and the
    // server drops the row the next time its push service answers 410.
    //
    // A session that timed out (AutoLogoutManager) is not the person leaving the device, so it
    // uses PlainSubmitScript and keeps notifications on: a phone would otherwise lose them every
    // time the session expired, and nothing turns them back on by itself.
    internal static class PushSignOut
    {
        public const string EndpointField = "pushEndpoint";

        public static string HiddenField => $"""<input type="hidden" name="{EndpointField}" value="" />""";

        public static string SubmitScript(string formId) => Script(formId, unsubscribe: true);

        // Sends the endpoint without unsubscribing. Pairing uses this: the browser is only
        // unsubscribed once the pairing has actually succeeded (UnsubscribeThenGoPage).
        public static string CaptureEndpointScript(string formId) => Script(formId, unsubscribe: false);

        public static string PlainSubmitScript(string formId) =>
            $"<script>document.getElementById('{formId}').submit();</script>";

        // The page a successful POST returns instead of a plain redirect: unsubscribes this browser,
        // then moves on (after a short wait at most).
        public static string UnsubscribeThenGoPage(string title, string url) => $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8" />
                <title>{{title}}</title>
                <meta name="robots" content="noindex" />
            </head>
            <body>
                <noscript><a href="{{url}}">Continue</a></noscript>
                <script>
                (function () {
                    var gone = false;
                    function go() { if (!gone) { gone = true; location.replace('{{url}}'); } }
                    setTimeout(go, 1500);
                    try {
                        if (!('serviceWorker' in navigator)) { go(); return; }
                        navigator.serviceWorker.getRegistration()
                            .then(function (reg) { return reg && reg.pushManager ? reg.pushManager.getSubscription() : null; })
                            .then(function (sub) { return sub ? sub.unsubscribe().then(go, go) : go(); })
                            .catch(go);
                    } catch (e) { go(); }
                })();
                </script>
            </body>
            </html>
            """;

        private static string Script(string formId, bool unsubscribe) => $$"""
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
                            {{(unsubscribe ? "return sub.unsubscribe().then(go, go);" : "go();")}}
                        })
                        .catch(go);
                } catch (e) { go(); }
            })();
            </script>
            """;
    }
}
