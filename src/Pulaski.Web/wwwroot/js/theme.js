// Theme, sidebar and touch-layout preferences. Loaded synchronously in <head> so the stored choice applies
// before first paint. A stored theme sets data-theme; otherwise the OS preference decides.
// data-bs-theme mirrors the effective theme so Bootstrap's own dark styles follow along.
// Touch layout (#119) sets data-touch: on by itself where the main pointer is a finger, and switched per device
// from the header, since many industrial touch panels report a mouse.
(() => {
    const root = document.documentElement;
    const systemDark = window.matchMedia("(prefers-color-scheme: dark)");

    const read = key => {
        try { return localStorage.getItem(key); } catch { return null; }
    };
    const write = (key, value) => {
        try { localStorage.setItem(key, value); } catch { /* storage blocked: keep it for this page only */ }
    };

    const effectiveTheme = () => root.dataset.theme ?? (systemDark.matches ? "dark" : "light");
    const sync = () => { root.dataset.bsTheme = effectiveTheme(); };

    const storedTheme = read("pulaski.theme");
    if (storedTheme === "light" || storedTheme === "dark") {
        root.dataset.theme = storedTheme;
    }
    if (read("pulaski.sidebar") === "rail") {
        root.dataset.sidebar = "rail";
    }
    sync();
    systemDark.addEventListener("change", sync);

    const coarse = window.matchMedia("(pointer: coarse)");
    // A page made only for touch (the operator station, #146) asks for it with <meta name="pulaski-touch" content="always">.
    const touchOn = () => {
        if (document.querySelector('meta[name="pulaski-touch"][content="always"]')) {
            return true;
        }
        const stored = read("pulaski.touch");
        return stored === "on" || (stored !== "off" && coarse.matches);
    };
    const syncTouch = () => {
        const on = touchOn();
        if (on) {
            root.dataset.touch = "";
        } else {
            delete root.dataset.touch;
        }
        document.querySelectorAll(".touch-toggle").forEach(b => b.setAttribute("aria-pressed", on ? "true" : "false"));
    };
    syncTouch();
    document.addEventListener("DOMContentLoaded", syncTouch);
    coarse.addEventListener("change", syncTouch);

    // Narrow screens: the sidebar opens over the content and closes after a link is followed.
    const narrow = window.matchMedia("(max-width: 719.98px)");
    document.addEventListener("click", e => {
        if (e.target instanceof Element && e.target.closest(".app-sidebar a")) {
            delete root.dataset.navOpen;
        }
    });

    window.pulaski = {
        toggleTheme() {
            const next = effectiveTheme() === "dark" ? "light" : "dark";
            root.dataset.theme = next;
            write("pulaski.theme", next);
            sync();
        },
        toggleTouch() {
            write("pulaski.touch", touchOn() ? "off" : "on");
            syncTouch();
        },
        toggleSidebar() {
            if (narrow.matches) {
                if ("navOpen" in root.dataset) {
                    delete root.dataset.navOpen;
                } else {
                    root.dataset.navOpen = "";
                }
                return;
            }
            if (root.dataset.sidebar === "rail") {
                delete root.dataset.sidebar;
                write("pulaski.sidebar", "full");
            } else {
                root.dataset.sidebar = "rail";
                write("pulaski.sidebar", "rail");
            }
        },
    };
})();
