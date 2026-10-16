// The process editor's canvas. Blazor renders operation boxes ([data-op-id]) and one group per
// link ([data-link] with data-from / data-to) and its label ([data-link-label]); this draws the
// link curves between the boxes, moves boxes by their header, and draws new links from the
// handle on a box's right edge. Blazor hears only the end of each gesture.
(() => {
    const curve = (a, b) => {
        const bend = Math.max(40, Math.abs(b.x - a.x) / 2);
        return `M ${a.x} ${a.y} C ${a.x + bend} ${a.y}, ${b.x - bend} ${b.y}, ${b.x} ${b.y}`;
    };

    window.pulaski = window.pulaski || {};
    window.pulaski.processCanvas = {
        attach(stage, dotnet) {
            const box = id => stage.querySelector(`[data-op-id="${id}"]`);
            const rectOf = el => ({ x: el.offsetLeft, y: el.offsetTop, w: el.offsetWidth, h: el.offsetHeight });

            const draw = () => {
                let width = 0, height = 0;
                for (const node of stage.querySelectorAll("[data-op-id]")) {
                    const r = rectOf(node);
                    width = Math.max(width, r.x + r.w);
                    height = Math.max(height, r.y + r.h);
                }
                const svg = stage.querySelector("svg.process-links");
                if (svg) {
                    svg.setAttribute("width", width + 200);
                    svg.setAttribute("height", height + 200);
                }
                stage.style.minWidth = `${width + 160}px`;
                stage.style.minHeight = `${height + 120}px`;

                for (const link of stage.querySelectorAll("[data-link]")) {
                    const from = box(link.dataset.from), to = box(link.dataset.to);
                    if (!from || !to) continue;
                    const f = rectOf(from), t = rectOf(to);
                    const a = { x: f.x + f.w, y: f.y + f.h / 2 }, b = { x: t.x, y: t.y + t.h / 2 };
                    for (const path of link.querySelectorAll("path")) path.setAttribute("d", curve(a, b));
                    const label = stage.querySelector(`[data-link-label="${link.dataset.link}"]`);
                    if (label) {
                        label.style.left = `${(a.x + b.x) / 2}px`;
                        label.style.top = `${(a.y + b.y) / 2}px`;
                    }
                }
            };

            const pointIn = e => {
                const r = stage.getBoundingClientRect();
                return { x: e.clientX - r.left, y: e.clientY - r.top };
            };

            let gesture = null;

            const onDown = e => {
                if (e.button !== 0 || stage.dataset.readonly === "true") return;
                const connector = e.target.closest("[data-connector]");
                const grip = e.target.closest("[data-grip]");
                if (connector) {
                    const from = connector.closest("[data-op-id]");
                    const temp = stage.querySelector("path.process-link-draft");
                    gesture = { kind: "connect", from, temp };
                } else if (grip && !e.target.closest("button, input, select")) {
                    const node = grip.closest("[data-op-id]");
                    const p = pointIn(e);
                    gesture = { kind: "move", node, dx: p.x - node.offsetLeft, dy: p.y - node.offsetTop, moved: false };
                } else {
                    return;
                }
                stage.setPointerCapture(e.pointerId);
                e.preventDefault();
            };

            const onMove = e => {
                if (!gesture) return;
                const p = pointIn(e);
                if (gesture.kind === "move") {
                    gesture.node.style.left = `${Math.max(0, p.x - gesture.dx)}px`;
                    gesture.node.style.top = `${Math.max(0, p.y - gesture.dy)}px`;
                    gesture.moved = true;
                    gesture.node.classList.add("is-moving");
                    draw();
                } else {
                    const f = rectOf(gesture.from);
                    gesture.temp?.setAttribute("d", curve({ x: f.x + f.w, y: f.y + f.h / 2 }, p));
                    stage.querySelectorAll(".is-link-target").forEach(n => n.classList.remove("is-link-target"));
                    const over = document.elementFromPoint(e.clientX, e.clientY)?.closest("[data-op-id]");
                    if (over && over !== gesture.from) over.classList.add("is-link-target");
                }
            };

            const onUp = e => {
                if (!gesture) return;
                const g = gesture;
                gesture = null;
                stage.releasePointerCapture(e.pointerId);
                if (g.kind === "move") {
                    g.node.classList.remove("is-moving");
                    if (g.moved) {
                        dotnet.invokeMethodAsync("OnMoved", g.node.dataset.opId, g.node.offsetLeft, g.node.offsetTop);
                    } else {
                        dotnet.invokeMethodAsync("OnSelectOperation", g.node.dataset.opId);
                    }
                } else {
                    g.temp?.setAttribute("d", "");
                    stage.querySelectorAll(".is-link-target").forEach(n => n.classList.remove("is-link-target"));
                    const target = document.elementFromPoint(e.clientX, e.clientY)?.closest("[data-op-id]");
                    if (target && target !== g.from) {
                        dotnet.invokeMethodAsync("OnConnect", g.from.dataset.opId, target.dataset.opId);
                    }
                }
            };

            stage.addEventListener("pointerdown", onDown);
            stage.addEventListener("pointermove", onMove);
            stage.addEventListener("pointerup", onUp);
            stage.addEventListener("pointercancel", onUp);
            const resize = new ResizeObserver(draw);
            resize.observe(stage);

            return {
                draw,
                // Where a drop landed on the stage, for placing a new operation there.
                point(clientX, clientY) {
                    const r = stage.getBoundingClientRect();
                    return [clientX - r.left, clientY - r.top];
                },
                dispose() {
                    stage.removeEventListener("pointerdown", onDown);
                    stage.removeEventListener("pointermove", onMove);
                    stage.removeEventListener("pointerup", onUp);
                    stage.removeEventListener("pointercancel", onUp);
                    resize.disconnect();
                },
            };
        },
    };
})();
