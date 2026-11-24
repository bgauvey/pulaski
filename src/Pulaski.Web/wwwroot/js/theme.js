// Theme, sidebar and touch-layout preferences, chosen in the header's user menu (Preferences dialog). Loaded synchronously in <head> so the stored choice applies
// before first paint. A stored theme sets data-theme; otherwise the OS preference decides.
// data-bs-theme mirrors the effective theme so Bootstrap's own dark styles follow along.
// Touch layout (#119) sets data-touch: on by itself where the main pointer is a finger, and switched per device
// in Preferences, since many industrial touch panels report a mouse.
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
        // A link in a popover menu (the user menu) closes it: the page changes without a reload, so it would stay open.
        const link = e.target instanceof Element ? e.target.closest("[popover] a") : null;
        if (link) {
            link.closest("[popover]").hidePopover();
        }
    });

    // The Preferences dialog's choices: what is stored, ticked when it opens; a change applies at once.
    const choices = () => ({
        "pulaski-theme": read("pulaski.theme") === "light" || read("pulaski.theme") === "dark" ? read("pulaski.theme") : "system",
        "pulaski-touch": read("pulaski.touch") === "on" || read("pulaski.touch") === "off" ? read("pulaski.touch") : "auto",
    });
    const tickChoices = () => {
        const chosen = choices();
        document.querySelectorAll("input[data-pref]").forEach(i => { i.checked = chosen[i.dataset.pref] === i.value; });
    };
    document.addEventListener("change", e => {
        const input = e.target instanceof HTMLInputElement && e.target.dataset.pref ? e.target : null;
        if (input?.dataset.pref === "pulaski-theme") {
            window.pulaski.setTheme(input.value);
        } else if (input?.dataset.pref === "pulaski-touch") {
            window.pulaski.setTouch(input.value);
        }
    });

    window.pulaski = {
        /** "light", "dark", or "system" to follow the device. */
        setTheme(choice) {
            if (choice === "light" || choice === "dark") {
                root.dataset.theme = choice;
            } else {
                delete root.dataset.theme;
            }
            write("pulaski.theme", root.dataset.theme ?? "system");
            sync();
        },
        /** "on", "off", or "auto" for on where the main pointer is a finger. */
        setTouch(choice) {
            write("pulaski.touch", choice === "on" || choice === "off" ? choice : "auto");
            syncTouch();
        },
        /** Opens a modal dialog with the stored choices ticked, closing any open popover menu first. */
        openDialog(id) {
            document.querySelectorAll("[popover]:popover-open").forEach(p => p.hidePopover());
            tickChoices();
            document.getElementById(id)?.showModal();
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
