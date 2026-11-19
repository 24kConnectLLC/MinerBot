// The dashboard's one script, on the pages whose status box refreshes. An element with data-refresh, the address of its
// fragment, and data-fragment, the fragment's name, gets its content again every 30 seconds while the page is visible.
// The content is replaced only from a 200 that carries the fragment's own marker header. Anything else, such as a
// sign-in or Cloudflare Access redirect, a 401, 403, 429 or 503, or a network failure, stops the refresh and writes the
// words of the note beside the box, which asks for a reload. The note is a live region the page draws empty, so a
// screen reader announces the words; one that was hidden and then shown would be a new node, which most don't. Each
// request is marked as a background refresh, which never keeps the session going. The page works without this: it only
// keeps the box current.
(function () {
    'use strict';

    var every = 30000;

    function start(box) {
        var note = box.parentNode ? box.parentNode.querySelector('[data-refresh-stopped]') : null;
        var address = new URL(box.getAttribute('data-refresh'), window.location.href);
        var fragment = box.getAttribute('data-fragment');
        var last = Date.now();
        var timer = null;
        var stopped = false;

        function stop() {
            if (stopped) {
                return;
            }

            stopped = true;
            window.clearInterval(timer);
            document.removeEventListener('visibilitychange', visible);

            if (note) {
                note.textContent = note.getAttribute('data-refresh-stopped');
            }
        }

        function refresh() {
            if (stopped || document.visibilityState !== 'visible') {
                return;
            }

            last = Date.now();

            window.fetch(address.href, {
                method: 'GET',
                cache: 'no-store',
                credentials: 'same-origin',
                redirect: 'manual',
                headers: { 'X-MinerBot-Refresh': 'background', 'X-Requested-With': 'XMLHttpRequest' }
            }).then(function (response) {
                if (response.status !== 200 || response.headers.get('X-MinerBot-Fragment') !== fragment) {
                    stop();
                    return null;
                }

                return response.text().then(function (html) {
                    if (!stopped) {
                        box.innerHTML = html;
                    }
                });
            }).catch(stop);
        }

        // Back on a page that was hidden for longer than the wait, it refreshes at once and starts the 30 seconds again
        // from there. The interval keeps its own count while the page is hidden, so its next tick could otherwise send
        // another refresh a moment later.
        function visible() {
            if (document.visibilityState === 'visible' && Date.now() - last >= every) {
                refresh();
                window.clearInterval(timer);
                timer = window.setInterval(refresh, every);
            }
        }

        // Only the dashboard's own addresses, which the Content-Security-Policy would hold it to anyway
        if (address.origin !== window.location.origin || !fragment) {
            return;
        }

        timer = window.setInterval(refresh, every);
        document.addEventListener('visibilitychange', visible);
    }

    var boxes = document.querySelectorAll('[data-refresh]');

    for (var i = 0; i < boxes.length; i++) {
        start(boxes[i]);
    }
})();
