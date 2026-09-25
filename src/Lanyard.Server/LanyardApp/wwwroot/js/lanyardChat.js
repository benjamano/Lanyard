// lanyardChat.js - the chat composer (Quill, loaded globally in App.razor) and thread scrolling.
// The editor's DOM belongs to Quill: Blazor renders an empty host element and never touches its
// contents, and .NET hears about a message only when it's sent.
window.lanyardChat = (() => {
    // On a phone the on-screen keyboard's return key adds a line; there's a Send button for sending.
    function isTouch() {
        return window.matchMedia('(pointer: coarse)').matches;
    }

    function editor(host) {
        return host && host.__lanyardChat;
    }

    return {
        init(host, toolbar, dotNetRef, placeholder) {
            if (!host || host.__lanyardChat) {
                return;
            }

            const quill = new Quill(host, {
                theme: 'snow',
                placeholder,
                formats: ['bold', 'italic', 'underline', 'strike', 'list', 'link'],
                modules: {
                    toolbar,
                    keyboard: {
                        bindings: {
                            send: {
                                key: 'Enter',
                                shiftKey: false,
                                handler: () => {
                                    if (isTouch()) {
                                        return true;
                                    }

                                    send();
                                    return false;
                                }
                            }
                        }
                    }
                }
            });

            let sending = false;

            async function send() {
                if (sending || quill.getText().trim().length === 0) {
                    return;
                }

                sending = true;

                try {
                    const accepted = await dotNetRef.invokeMethodAsync('OnSubmit', quill.root.innerHTML);

                    if (accepted) {
                        quill.setContents([]);
                    }
                } catch {
                    // Component gone or circuit dropped; the text stays for another try.
                } finally {
                    sending = false;
                }
            }

            host.__lanyardChat = { quill, send };
        },

        send(host) {
            const e = editor(host);
            return e ? e.send() : null;
        },

        // Puts a message into the editor to be edited.
        setHtml(host, html) {
            const e = editor(host);

            if (!e) {
                return;
            }

            e.quill.setContents([]);
            e.quill.clipboard.dangerouslyPasteHTML(0, html || '');
            e.quill.focus();
        },

        clear(host) {
            const e = editor(host);

            if (e) {
                e.quill.setContents([]);
            }
        },

        focus(host) {
            const e = editor(host);

            if (e) {
                e.quill.focus();
            }
        },

        setEnabled(host, enabled) {
            const e = editor(host);

            if (e) {
                e.quill.enable(enabled);
            }
        },

        // Tells .NET when the page is hidden (another tab, minimised, phone locked) and shown again,
        // so a conversation left open in the background doesn't count as being read. Returns
        // whether it's visible now.
        watchVisibility(dotNetRef) {
            if (window.__lanyardChatVisibility) {
                document.removeEventListener('visibilitychange', window.__lanyardChatVisibility);
            }

            window.__lanyardChatVisibility = () => {
                dotNetRef.invokeMethodAsync('OnVisibilityChanged', document.visibilityState === 'visible').catch(() => { });
            };

            document.addEventListener('visibilitychange', window.__lanyardChatVisibility);

            return document.visibilityState === 'visible';
        },

        unwatchVisibility() {
            if (window.__lanyardChatVisibility) {
                document.removeEventListener('visibilitychange', window.__lanyardChatVisibility);
                window.__lanyardChatVisibility = null;
            }
        },

        isNearBottom(el) {
            return !el || el.scrollHeight - el.scrollTop - el.clientHeight < 160;
        },

        scrollToBottom(el) {
            if (el) {
                el.scrollTop = el.scrollHeight;
            }
        },

        // After older messages are added above, keep what the reader was looking at in place.
        scrollHeight(el) {
            return el ? el.scrollHeight : 0;
        },

        keepPosition(el, previousHeight) {
            if (el) {
                el.scrollTop = el.scrollHeight - previousHeight;
            }
        }
    };
})();
