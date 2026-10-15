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

    // Drag and drop for the schedule. Firefox only starts a drag that carries data, which Blazor
    // can't set. Drop zones (data-drop-zone) are highlighted here rather than from dragover
    // handlers, so a drag costs no server round trips until the drop.
    document.addEventListener("dragstart", e => {
        if (e.target instanceof Element && e.target.closest("[draggable=true]")) {
            e.dataTransfer?.setData("text/plain", "");
            if (e.dataTransfer) e.dataTransfer.effectAllowed = "move";
        }
    });
    let dropZone = null;
    const setDropZone = zone => {
        if (zone === dropZone) return;
        dropZone?.classList.remove("is-drop-target");
        dropZone = zone;
        dropZone?.classList.add("is-drop-target");
    };
    document.addEventListener("dragover", e => {
        setDropZone(e.target instanceof Element ? e.target.closest("[data-drop-zone]:not([data-drop-blocked]):not([data-drop-refused])") : null);
    });
    document.addEventListener("dragend", () => setDropZone(null));
    document.addEventListener("drop", () => setDropZone(null));

    window.pulaski = window.pulaski || {};

    // Gantt drag and drop. The slot is the job a drop would go in front of: the first one whose
    // midpoint lies right of the pointer (the track lists them in data-slots), or the end of the
    // line. Blazor hears about a slot only when it changes, and about the drop itself, so a drag
    // costs a handful of round trips however long it lasts.
    window.pulaski.gantt = {
        attach(root, dotnet) {
            let slot = null;
            let payload = null;

            const rowOf = target => target instanceof Element ? target.closest(".gantt-row[data-line-id]") : null;

            const slotAt = (row, clientX) => {
                const track = row.querySelector(".gantt-track");
                const rect = track.getBoundingClientRect();
                const percent = rect.width > 0 ? (clientX - rect.left) / rect.width * 100 : 100;
                const moving = root.dataset.draggingJob;
                let before = null;
                for (const entry of (track.dataset.slots || "").split(";").filter(Boolean)) {
                    const [id, mid] = entry.split("@");
                    if (id !== moving && parseFloat(mid) > percent) {
                        before = id;
                        break;
                    }
                }
                return { line: row.dataset.lineId, before, percent: Math.min(100, Math.max(0, percent)) };
            };

            const setSlot = next => {
                const key = next ? `${next.line}|${next.before ?? ""}` : null;
                if (key === slot) return;
                slot = key;
                dotnet.invokeMethodAsync("OnDragSlot", next?.line ?? null, next?.before ?? null);
            };

            const onDragStart = e => {
                payload = e.target instanceof Element ? e.target.closest("[data-drag]")?.dataset.drag ?? null : null;
            };

            const onDragOver = e => {
                const row = rowOf(e.target);
                if (!row || row.hasAttribute("data-drop-blocked")) {
                    setSlot(null);
                    return;
                }
                setSlot(slotAt(row, e.clientX));
                // A refused slot keeps its preview (which says why) but won't take the drop.
                if (!row.hasAttribute("data-drop-refused")) {
                    e.preventDefault();
                    if (e.dataTransfer) e.dataTransfer.dropEffect = "move";
                }
            };

            const onDragLeave = e => {
                if (!(e.relatedTarget instanceof Node) || !root.contains(e.relatedTarget)) setSlot(null);
            };

            const onDrop = e => {
                const row = rowOf(e.target);
                if (!row || row.hasAttribute("data-drop-blocked") || row.hasAttribute("data-drop-refused")) return;
                e.preventDefault();
                const at = slotAt(row, e.clientX);
                slot = null;
                dotnet.invokeMethodAsync("OnDropSlot", at.line, at.before, payload, at.percent);
                payload = null;
            };

            const onDragEnd = () => {
                payload = null;
                setSlot(null);
            };

            document.addEventListener("dragstart", onDragStart);
            document.addEventListener("dragend", onDragEnd);
            root.addEventListener("dragover", onDragOver);
            root.addEventListener("dragleave", onDragLeave);
            root.addEventListener("drop", onDrop);

            return {
                dispose() {
                    document.removeEventListener("dragstart", onDragStart);
                    document.removeEventListener("dragend", onDragEnd);
                    root.removeEventListener("dragover", onDragOver);
                    root.removeEventListener("dragleave", onDragLeave);
                    root.removeEventListener("drop", onDrop);
                },
            };
        },
    };
    window.pulaski.ui = {
        // Where clientX falls across an element, from 0 (left edge) to 1 (right edge).
        fraction(id, clientX) {
            const rect = document.getElementById(id)?.getBoundingClientRect();
            return rect && rect.width > 0 ? Math.min(1, Math.max(0, (clientX - rect.left) / rect.width)) : 1;
        },
        // Scroll an element into view inside its scroll container, only as far as needed.
        reveal(id) {
            document.getElementById(id)?.scrollIntoView({ block: "nearest" });
        },
        focus(id) {
            document.getElementById(id)?.focus();
        },
    };
})();
