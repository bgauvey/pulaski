// Live updates for line boards and operator stations (#143b, D-117). A page asks for it with
//   <meta name="pulaski-live" content="board|station" data-every="seconds" data-idle="minutes">
// It listens to /terminal/live (the terminal's changes, passed on by the web) and reloads the page, as a GET, when one arrives: at most every
// few seconds. A station never reloads while someone is typing in one of its forms (a field has focus or was changed since the page loaded),
// unless nothing was typed for its idle minutes; the reload waits and happens once they are done. data-every is the timer that stands in when
// the stream is down (0: none). Pages without the meta tag are left alone.
(() => {
    const minimumGap = 3000;
    let source = null;
    let dirty = false;
    let lastInput = Date.now();
    let pending = false;
    let lastReload = Date.now();
    let timer = null;

    const config = () => {
        const meta = document.querySelector('meta[name="pulaski-live"]');
        if (!meta) return null;
        return {
            mode: meta.getAttribute('content') || 'board',
            every: Number(meta.dataset.every || 0) * 1000,
            idle: Number(meta.dataset.idle || 15) * 60000,
        };
    };

    const typing = (c) => {
        if (c.mode !== 'station') return false;
        if (Date.now() - lastInput > c.idle) return false;
        const active = document.activeElement;
        const focused = active && /^(INPUT|SELECT|TEXTAREA)$/.test(active.tagName) && active.type !== 'hidden';
        return dirty || focused;
    };

    // Network drops (#134, D-133). A page with <meta name="pulaski-offline" data-offline="…" data-unsent="…"> never swaps itself for the browser's
    // error page: before reloading or sending a form it asks /terminal/ping; while that fails it stays as it is, says so in a banner, keeps what
    // was typed, and tries again every few seconds, reloading once Pulaski answers. Nothing is queued: what wasn't sent is sent again by hand.
    const offlineMeta = () => document.querySelector('meta[name="pulaski-offline"]');
    let offline = false;
    let notice = null;

    const reachable = async () => {
        const abort = new AbortController();
        const timeout = setTimeout(() => abort.abort(), 4000);
        try {
            const response = await fetch('terminal/ping', { cache: 'no-store', credentials: 'same-origin', signal: abort.signal });
            return response.status < 500;
        } catch {
            return false;
        } finally {
            clearTimeout(timeout);
        }
    };

    const showBanner = (text) => {
        if (!notice) {
            notice = document.createElement('div');
            notice.className = 'offline-banner';
            notice.setAttribute('role', 'alert');
            document.body.prepend(notice);
        }
        notice.textContent = text;
    };

    const goOffline = (unsent) => {
        const meta = offlineMeta();
        offline = true;
        const text = (meta && meta.dataset.offline) || 'No connection to Pulaski: nothing is saved until it is back.';
        showBanner(unsent ? `${text} ${(meta && meta.dataset.unsent) || 'What you entered was not sent: it is still here; send it again once the connection is back.'}` : text);
    };

    const goOnline = () => {
        offline = false;
        if (notice) { notice.remove(); notice = null; }
    };

    const reload = async () => {
        lastReload = Date.now();
        if (offlineMeta() && !(await reachable())) {
            goOffline(false);
            return;
        }
        location.replace(location.pathname + location.search);
    };

    // While offline, try every five seconds; once Pulaski answers, the page comes back as it now is (unless a form is being typed in).
    setInterval(async () => {
        if (!offline || !offlineMeta()) return;
        if (await reachable()) {
            goOnline();
            const c = config();
            if (!c || !typing(c)) location.replace(location.pathname + location.search);
        }
    }, 5000);

    window.addEventListener('offline', () => { if (offlineMeta()) goOffline(false); });
    window.addEventListener('online', async () => { if (offlineMeta() && await reachable()) goOnline(); });

    // A form is sent only when Pulaski answers; otherwise it stays, as typed, and the banner says it wasn't sent.
    document.addEventListener('submit', async (e) => {
        const form = e.target;
        if (!offlineMeta() || !(form instanceof HTMLFormElement) || form.dataset.pulaskiChecked === '1') {
            if (form instanceof HTMLFormElement) delete form.dataset.pulaskiChecked;
            return;
        }
        e.preventDefault();
        e.stopImmediatePropagation();
        // A second tap while the check is on its way would send the form twice.
        if (form.dataset.pulaskiChecking === '1') return;
        form.dataset.pulaskiChecking = '1';
        const submitter = e.submitter;
        const ok = await reachable();
        delete form.dataset.pulaskiChecking;
        if (ok) {
            goOnline();
            form.dataset.pulaskiChecked = '1';
            if (submitter) form.requestSubmit(submitter); else form.requestSubmit();
        } else {
            goOffline(true);
        }
    }, true);

    const tryReload = () => {
        const c = config();
        if (!c) return;
        if (typing(c)) {
            pending = true;
            return;
        }
        const wait = lastReload + minimumGap - Date.now();
        if (wait > 0) {
            pending = true;
            return;
        }
        pending = false;
        reload();
    };

    const connect = () => {
        const c = config();
        if (!c) {
            if (source) { source.close(); source = null; }
            return;
        }
        if (source || typeof EventSource === 'undefined') return;
        source = new EventSource('terminal/live');
        source.addEventListener('changes', tryReload);
        source.onerror = () => {
            // The browser reconnects by itself; if the page itself is gone, the timer below still reloads.
        };
    };

    document.addEventListener('input', () => { dirty = true; lastInput = Date.now(); }, true);
    document.addEventListener('focusin', () => { lastInput = Date.now(); }, true);
    document.addEventListener('focusout', () => setTimeout(() => { if (pending) tryReload(); }, 0), true);

    // Once a second: a waiting reload, the stand-in timer, and pages that came and went.
    setInterval(() => {
        connect();
        const c = config();
        if (!c) return;
        if (pending) tryReload();
        if (c.every > 0 && Date.now() - lastReload >= c.every) {
            if (typing(c)) pending = true; else reload();
        }
    }, 1000);

    connect();
    if (typeof Blazor !== 'undefined' && Blazor.addEventListener) {
        Blazor.addEventListener('enhancedload', () => { dirty = false; lastReload = Date.now(); connect(); });
    }
})();
