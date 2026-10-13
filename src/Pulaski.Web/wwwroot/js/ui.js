// Small DOM helpers Blazor cannot do on its own.
(() => {
    // Keyboard widgets marked data-arrow-keys own the arrow, Home and End keys: stop the page
    // scrolling while still letting Blazor's keydown handler see the key. Tab is left alone.
    const owned = new Set(["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Home", "End"]);
    document.addEventListener("keydown", e => {
        if (owned.has(e.key) && e.target instanceof Element && e.target.matches("[data-arrow-keys]")) {
            e.preventDefault();
        }
    });

    window.pulaski = window.pulaski || {};
    window.pulaski.ui = {
        // Scroll an element into view inside its scroll container, only as far as needed.
        reveal(id) {
            document.getElementById(id)?.scrollIntoView({ block: "nearest" });
        },
        focus(id) {
            document.getElementById(id)?.focus();
        },
    };
})();
