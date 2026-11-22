// The dashboard's one script, on the pages with a box that keeps itself current. An element with data-refresh, the address
// of its fragment, and data-fragment, the fragment's name, gets its content again while the page is visible: every 30
// seconds, or after the seconds its data-refresh-every gives, from 2 to 30. A fragment's answer can name the wait before
// the next look in X-MinerBot-Refresh-Next, in the same range, or end the looks with "end" once what it shows can't
// change, such as a request the bot has answered. The content is replaced only from a 200 that carries the fragment's own
// marker header. Anything else, such as a sign-in or Cloudflare Access redirect, a 401, 403, 429 or 503, or a network
// failure, stops the looks and writes the words of the note beside the box, which asks for a reload; so does a box with
// data-refresh-every 15 minutes after the page loaded, a cap behind the server's own end. There's no retry: a reload
// shows the answer. The note is a live region the page draws empty, so a screen reader announces the words; one that was
// hidden and then shown would be a new node, which most don't. Each look is marked as a background refresh, which never
// keeps the session going. The page works without this: it only keeps the boxes current.
(function () {
    'use strict';

    var every = 30000;

    // How long a box with a wait of its own looks at most, from the page's load
    var limit = 900000;
    var loaded = Date.now();

    // Whole seconds from 2 to 30, as milliseconds, or null for anything else
    function seconds(text) {
        var value = /^[0-9]{1,2}$/.test(text || '') ? parseInt(text, 10) : 0;
        return value >= 2 && value <= 30 ? value * 1000 : null;
    }

    function start(box) {
        var note = box.parentNode ? box.parentNode.querySelector('[data-refresh-stopped]') : null;
        var address = new URL(box.getAttribute('data-refresh'), window.location.href);
        var fragment = box.getAttribute('data-fragment');
        var capped = box.hasAttribute('data-refresh-every');
        var wait = seconds(box.getAttribute('data-refresh-every')) || every;
        var last = Date.now();
        var timer = null;
        var stopped = false;

        // Ends the looks. Only a box whose content is final ends quietly; anything else asks for a reload.
        function stop(final) {
            if (stopped) {
                return;
            }

            stopped = true;
            window.clearTimeout(timer);
            document.removeEventListener('visibilitychange', visible);

            if (note && final !== true) {
                note.textContent = note.getAttribute('data-refresh-stopped');
            }
        }

        function later() {
            window.clearTimeout(timer);
            timer = window.setTimeout(refresh, wait);
        }

        function refresh() {
            if (stopped || document.visibilityState !== 'visible') {
                return;
            }

            if (capped && Date.now() - loaded >= limit) {
                stop(false);
                return;
            }

            // One look at a time: a wait that came due while this one runs starts again from its answer
            window.clearTimeout(timer);
            last = Date.now();

            window.fetch(address.href, {
                method: 'GET',
                cache: 'no-store',
                credentials: 'same-origin',
                redirect: 'manual',
                headers: { 'X-MinerBot-Refresh': 'background', 'X-Requested-With': 'XMLHttpRequest' }
            }).then(function (response) {
                if (response.status !== 200 || response.headers.get('X-MinerBot-Fragment') !== fragment) {
                    stop(false);
                    return null;
                }

                var next = response.headers.get('X-MinerBot-Refresh-Next');

                return response.text().then(function (html) {
                    if (stopped) {
                        return;
                    }

                    box.innerHTML = html;

                    if (next === 'end') {
                        stop(true);
                        return;
                    }

                    wait = seconds(next) || wait;
                    later();
                });
            }).catch(function () {
                stop(false);
            });
        }

        // A look that came due while the page was hidden sent nothing, so coming back after longer than the wait looks at
        // once, which starts the chain again
        function visible() {
            if (document.visibilityState === 'visible' && Date.now() - last >= wait) {
                refresh();
            }
        }

        // Only the dashboard's own addresses, which the Content-Security-Policy would hold it to anyway
        if (address.origin !== window.location.origin || !fragment) {
            return;
        }

        later();
        document.addEventListener('visibilitychange', visible);
    }

    var boxes = document.querySelectorAll('[data-refresh]');

    for (var i = 0; i < boxes.length; i++) {
        start(boxes[i]);
    }
})();
