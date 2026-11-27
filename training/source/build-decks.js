// Builds one training deck per role into ../ from content.js:  npm install && npm run build
// Every slide's words come from design/user_guide.md; keep the two in step when screens change.
const path = require("path");
const React = require("react");
const ReactDOMServer = require("react-dom/server");
const sharp = require("sharp");
const pptxgen = require("pptxgenjs");
const md = require("react-icons/md");
const { applyTheme } = require("./theme.js");
const { roles, modules } = require("./content.js");

const OUT = path.resolve(__dirname, "..");
const W = 13.333;
const FONT = "Calibri";
const HEX = { ink: "16202B", teal: "0B6662", tealSoft: "E2F1EF", bg: "EEF1F4", muted: "4F5D6B", border: "D8DEE5" };

const THEME = {
    name: "Pulaski training",
    headFontFace: FONT,
    bodyFontFace: FONT,
    colors: {
        dk1: HEX.ink, lt1: "FFFFFF", dk2: HEX.teal, lt2: HEX.bg,
        accent1: HEX.teal, accent2: "2A9455", accent3: "A86A0B", accent4: "2D5791", accent5: "6A4BB5", accent6: "B3261E",
        hlink: HEX.teal, folHlink: HEX.muted,
    },
};

// ---------- text helpers ----------

// "**Bold** words" -> pptxgenjs runs.
function runs(text, base = {}) {
    const parts = String(text).split(/(\*\*[^*]+\*\*|\*[^*\s][^*]*\*)/g).filter(Boolean);
    return parts.map((p) => (p.startsWith("**") ? { text: p.slice(2, -2), options: { ...base, bold: true } }
        : p.length > 2 && p.startsWith("*") && p.endsWith("*") ? { text: p.slice(1, -1), options: { ...base, italic: true } }
            : { text: p, options: { ...base } }));
}

const plain = (t) => String(t).replace(/\*\*?/g, "");

// A bulleted list of marked-up items: the bullet goes on each paragraph's first run (a bullet on a later run would start a new
// paragraph); theme.js then keeps only each paragraph's first properties, which pptxgenjs repeats for every run.
function bulleted(items, base) {
    const out = [];
    items.forEach((t, k) => {
        const rs = runs(t, base);
        rs.forEach((r, j) => out.push({ text: r.text, options: { ...r.options, ...(j === 0 ? { bullet: { indent: 15 }, paraSpaceAfter: 6 } : {}), ...(j === rs.length - 1 && k < items.length - 1 ? { breakLine: true } : {}) } }));
    });
    return out;
}

// Lines a paragraph takes at a size in a width (Calibri averages about half an em a character; a little slack on top).
function lines(text, widthIn, pt) {
    const perLine = Math.floor((widthIn * 72) / (pt * 0.5));
    return String(text).split("\n").reduce((n, para) => n + Math.max(1, Math.ceil(plain(para).length / perLine)), 0);
}

const heightOf = (text, widthIn, pt, spacing = 1.2) => (lines(text, widthIn, pt) * pt * spacing) / 72;

const warnings = [];
function check(deck, slideTitle, what, needed, available) {
    if (needed > available + 0.01) warnings.push(`${deck} · ${slideTitle} · ${what}: needs ${needed.toFixed(2)}" of ${available.toFixed(2)}"`);
}

// ---------- icons ----------

const iconCache = new Map();
async function icon(name, color = "FFFFFF") {
    const key = `${name}:${color}`;
    if (iconCache.has(key)) return iconCache.get(key);
    const Comp = md[name];
    if (!Comp) throw new Error(`No icon ${name}`);
    const svg = ReactDOMServer.renderToStaticMarkup(React.createElement(Comp, { color: `#${color}`, size: 256 }));
    const png = await sharp(Buffer.from(svg)).resize(256, 256).png().toBuffer();
    const data = "image/png;base64," + png.toString("base64");
    iconCache.set(key, data);
    return data;
}

async function iconCircle(slide, C, name, x, y, d, label, fill = C.accent1) {
    slide.addShape("ellipse", { x, y, w: d, h: d, fill: { color: fill }, line: { color: fill }, objectName: `${label} icon circle` });
    const pad = d * 0.22;
    slide.addImage({ data: await icon(name), x: x + pad, y: y + pad, w: d - 2 * pad, h: d - 2 * pad, altText: label, objectName: `${label} icon` });
}

// ---------- slide types ----------

const TOP = 1.55; // content starts below the title
const BOTTOM = 6.75; // and ends above the footer

async function stepsSlide(ctx, s) {
    const { slide, C } = ctx;
    const hasTips = s.tips && s.tips.length;
    const textW = hasTips ? 6.9 : 11.3;
    const pt = 16;
    let y = TOP + 0.05;
    const gap = 0.2;
    s.steps.forEach((step, i) => {
        const h = Math.max(0.42, heightOf(step, textW, pt));
        slide.addShape("ellipse", { x: 0.6, y, w: 0.42, h: 0.42, fill: { color: C.accent1 }, line: { color: C.accent1 }, objectName: `Step ${i + 1} marker` });
        slide.addText(String(i + 1), { x: 0.6, y, w: 0.42, h: 0.42, fontSize: 14, bold: true, color: C.background1, align: "center", valign: "middle", margin: 0, isTextBox: true, objectName: `Step ${i + 1} number` });
        slide.addText(runs(step), { x: 1.2, y: y - 0.02, w: textW, h: h + 0.04, fontSize: pt, color: C.text1, valign: "top", margin: 0, isTextBox: true, objectName: `Step ${i + 1}` });
        y += h + gap;
    });
    check(ctx.deck, s.title, "steps", y - gap, BOTTOM);
    if (hasTips) await tipsCard(ctx, s.tipsTitle || "Good to know", s.tips, 8.55, TOP, 4.18, BOTTOM - TOP, s.tipsIcon || "MdLightbulbOutline", s.title);
}

async function tipsCard(ctx, title, tips, x, y, w, h, iconName, slideTitle) {
    const { slide, C } = ctx;
    slide.addShape("roundRect", { x, y, w, h, rectRadius: 0.12, fill: { color: C.background2 }, line: { color: C.background2 }, objectName: `${title} card` });
    await iconCircle(slide, C, iconName, x + 0.3, y + 0.3, 0.55, title);
    slide.addText(title, { x: x + 1.0, y: y + 0.3, w: w - 1.2, h: 0.55, fontSize: 17, bold: true, color: C.text1, valign: "middle", margin: 0, isTextBox: true, objectName: `${title} heading` });
    const pt = 15;
    const flat = bulleted(tips, { fontSize: pt, color: C.text1 });
    const need = tips.reduce((n, t) => n + heightOf(t, w - 0.85, pt) + 0.09, 0);
    check(ctx.deck, slideTitle, `${title} card`, need, h - 1.15);
    slide.addText(flat, { x: x + 0.3, y: y + 1.05, w: w - 0.55, h: h - 1.2, valign: "top", margin: 0, isTextBox: true, objectName: `${title} list` });
}

async function cardsSlide(ctx, s) {
    const { slide, C } = ctx;
    const n = s.cards.length;
    const cols = s.cols || (n <= 3 ? n : n === 4 ? 2 : 3);
    const rows = Math.ceil(n / cols);
    const gap = 0.3;
    const x0 = 0.6;
    const totalW = W - 1.2;
    const cw = (totalW - gap * (cols - 1)) / cols;
    const ch = (BOTTOM - TOP - gap * (rows - 1)) / rows;
    for (let i = 0; i < n; i++) {
        const c = s.cards[i];
        const x = x0 + (i % cols) * (cw + gap);
        const y = TOP + Math.floor(i / cols) * (ch + gap);
        slide.addShape("roundRect", { x, y, w: cw, h: ch, rectRadius: 0.1, fill: { color: C.background2 }, line: { color: C.background2 }, objectName: `${c.title} card` });
        await iconCircle(slide, C, c.icon, x + 0.25, y + 0.25, 0.55, c.title, c.color ? C[c.color] : C.accent1);
        slide.addText(runs(c.title), { x: x + 0.95, y: y + 0.22, w: cw - 1.1, h: 0.6, fontSize: 17, bold: true, color: C.text1, valign: "middle", margin: 0, isTextBox: true, objectName: `${c.title} heading` });
        const pt = s.pt || (cols >= 3 ? 15 : 16);
        check(ctx.deck, s.title, `card ${plain(c.title)}`, heightOf(c.text, cw - 0.5, pt) + 0.15, ch - 1.05);
        slide.addText(runs(c.text), { x: x + 0.25, y: y + 0.95, w: cw - 0.45, h: ch - 1.1, fontSize: pt, color: C.text1, valign: "top", margin: 0, isTextBox: true, objectName: `${plain(c.title)} text` });
    }
}

async function compareSlide(ctx, s) {
    const { slide, C } = ctx;
    const cols = s.columns;
    const gap = 0.4;
    const cw = (W - 1.2 - gap * (cols.length - 1)) / cols.length;
    for (let i = 0; i < cols.length; i++) {
        const col = cols[i];
        const x = 0.6 + i * (cw + gap);
        slide.addShape("roundRect", { x, y: TOP, w: cw, h: BOTTOM - TOP, rectRadius: 0.1, fill: { color: i === 0 ? C.background2 : C.background1 }, line: { color: HEX.border, width: 1 }, objectName: `${col.title} column` });
        await iconCircle(slide, C, col.icon, x + 0.3, TOP + 0.3, 0.55, col.title, col.color ? C[col.color] : C.accent1);
        slide.addText(col.title, { x: x + 1.05, y: TOP + 0.3, w: cw - 1.3, h: 0.55, fontSize: 18, bold: true, color: C.text1, valign: "middle", margin: 0, isTextBox: true, objectName: `${col.title} heading` });
        const pt = s.pt || (Math.max(...cols.map((c) => c.items.length)) > 6 ? 16 : 17);
        const items = col.items;
        const flat = bulleted(items, { fontSize: pt, color: C.text1 });
        const need = items.reduce((n, t) => n + heightOf(t, cw - 0.95, pt) + 0.12, 0);
        check(ctx.deck, s.title, `column ${col.title}`, need, BOTTOM - TOP - 1.2);
        slide.addText(flat, { x: x + 0.3, y: TOP + 1.05, w: cw - 0.55, h: BOTTOM - TOP - 1.2, valign: "top", margin: 0, isTextBox: true, objectName: `${col.title} list` });
    }
}

async function flowSlide(ctx, s) {
    const { slide, C } = ctx;
    const n = s.stages.length;
    const gap = 0.12;
    const sw = (W - 1.2 - gap * (n - 1)) / n;
    const y = TOP + 0.1;
    const pt = n > 5 ? 15 : 16;
    const textH = Math.max(...s.stages.map((st) => heightOf(st.text, sw - 0.1, pt))) + 0.1;
    const fills = [C.accent4, C.accent1, C.accent2, C.accent3, C.accent5, C.accent6, C.text1];
    s.stages.forEach((st, i) => {
        const x = 0.6 + i * (sw + gap);
        const fill = st.color ? C[st.color] : fills[i % fills.length];
        slide.addShape(i === 0 ? "homePlate" : "chevron", { x, y, w: sw, h: 0.85, fill: { color: fill }, line: { color: fill }, objectName: `${st.label} stage` });
        slide.addText(st.label, { x: x + (i === 0 ? 0.1 : 0.3), y, w: sw - 0.5, h: 0.85, fontSize: n > 5 ? 14 : 16, bold: true, color: C.background1, align: "center", valign: "middle", margin: 0, isTextBox: true, objectName: `${st.label} label` });
        check(ctx.deck, s.title, `stage ${st.label}`, textH, 3.3);
        slide.addText(runs(st.text), { x, y: y + 1.05, w: sw, h: textH, fontSize: pt, color: C.text1, valign: "top", margin: 0, isTextBox: true, objectName: `${st.label} text` });
    });
    if (s.note) {
        const ny = y + 1.05 + textH + 0.35;
        const nh = Math.max(0.9, heightOf(s.note, W - 2.6, 15) + 0.4);
        slide.addShape("roundRect", { x: 0.6, y: ny, w: W - 1.2, h: nh, rectRadius: 0.1, fill: { color: C.background2 }, line: { color: C.background2 }, objectName: "Flow note card" });
        await iconCircle(slide, C, "MdInfoOutline", 0.85, ny + (nh - 0.5) / 2, 0.5, "Note");
        check(ctx.deck, s.title, "flow note", ny + nh, BOTTOM);
        slide.addText(runs(s.note), { x: 1.6, y: ny + 0.1, w: W - 2.45, h: nh - 0.2, fontSize: 15, color: C.text1, valign: "middle", margin: 0, isTextBox: true, objectName: "Flow note" });
    }
}

async function tableSlide(ctx, s) {
    const { slide, C } = ctx;
    const pt = s.pt || 15;
    const widths = s.widths || [4.6, W - 1.2 - 4.6];
    const header = s.head.map((h) => ({ text: h, options: { bold: true, color: HEX.ink, fill: { color: HEX.tealSoft }, fontSize: pt } }));
    const body = s.rows.map((r) => r.map((cell, k) => ({ text: runs(cell, { fontSize: pt, color: HEX.ink, italic: k === 0 && s.italicFirst }), options: { fontSize: pt, valign: "top" } })));
    let need = 0.45;
    s.rows.forEach((r) => { need += Math.max(...r.map((cell, k) => heightOf(cell, widths[k] - 0.2, pt))) + 0.2; });
    check(ctx.deck, s.title, "table", need, BOTTOM - TOP);
    slide.addTable([header, ...body], { x: 0.6, y: TOP, w: W - 1.2, colW: widths, fontFace: FONT, border: { type: "solid", pt: 0.75, color: HEX.border }, margin: [0.06, 0.1, 0.06, 0.1], autoPage: false, objectName: `${s.title} table` });
}

async function quizSlide(ctx, s) {
    const { slide, C } = ctx;
    const qs = s.questions;
    const cols = 2;
    const rows = Math.ceil(qs.length / cols);
    const gap = 0.3;
    const cw = (W - 1.2 - gap) / cols;
    const ch = (BOTTOM - TOP - gap * (rows - 1)) / rows;
    for (let i = 0; i < qs.length; i++) {
        const x = 0.6 + (i % cols) * (cw + gap);
        const y = TOP + Math.floor(i / cols) * (ch + gap);
        slide.addShape("roundRect", { x, y, w: cw, h: ch, rectRadius: 0.1, fill: { color: C.background2 }, line: { color: C.background2 }, objectName: `Question ${i + 1} card` });
        slide.addShape("ellipse", { x: x + 0.25, y: y + 0.22, w: 0.5, h: 0.5, fill: { color: C.accent1 }, line: { color: C.accent1 }, objectName: `Question ${i + 1} marker` });
        slide.addText(`Q${i + 1}`, { x: x + 0.25, y: y + 0.22, w: 0.5, h: 0.5, fontSize: 13, bold: true, color: C.background1, align: "center", valign: "middle", margin: 0, isTextBox: true, objectName: `Question ${i + 1} number` });
        check(ctx.deck, s.title, `question ${i + 1}`, heightOf(qs[i].q, cw - 1.2, 17), ch - 0.35);
        slide.addText(runs(qs[i].q), { x: x + 0.95, y: y + 0.2, w: cw - 1.15, h: ch - 0.35, fontSize: 17, color: C.text1, valign: "top", margin: 0, isTextBox: true, objectName: `Question ${i + 1}` });
    }
}

async function answersSlide(ctx, s) {
    // The answers to the knowledge check, as a table so a trainer can hide this slide while asking.
    await tableSlide(ctx, { title: s.title, head: ["Question", "Answer"], widths: [5.0, W - 1.2 - 5.0], rows: s.questions.map((q, i) => [`Q${i + 1}. ${q.short || q.q}`, q.a]) });
}

async function glanceSlide(ctx, s, role) {
    // The role at a glance: what it is for on the left, what a day looks like on the right.
    const { slide, C } = ctx;
    slide.addShape("roundRect", { x: 0.6, y: TOP, w: 5.6, h: BOTTOM - TOP, rectRadius: 0.12, fill: { color: C.accent1 }, line: { color: C.accent1 }, objectName: "Role summary card" });
    await iconCircle(slide, C, role.icon, 0.95, TOP + 0.35, 0.8, role.name, C.text1);
    slide.addText(role.name, { x: 1.95, y: TOP + 0.35, w: 4.0, h: 0.8, fontSize: 26, bold: true, color: C.background1, valign: "middle", margin: 0, isTextBox: true, objectName: "Role name" });
    check(ctx.deck, s.title, "role summary", heightOf(role.summary, 4.9, 18, 1.25), BOTTOM - TOP - 1.6);
    slide.addText(runs(role.summary), { x: 0.95, y: TOP + 1.45, w: 4.95, h: BOTTOM - TOP - 1.65, fontSize: 18, color: C.background1, valign: "top", margin: 0, lineSpacingMultiple: 1.1, isTextBox: true, objectName: "Role summary" });
    slide.addText("A day in this role", { x: 6.7, y: TOP, w: 6.0, h: 0.5, fontSize: 18, bold: true, color: C.text1, valign: "middle", margin: 0, isTextBox: true, objectName: "Day heading" });
    const rowH = (BOTTOM - TOP - 0.65) / role.day.length;
    for (let i = 0; i < role.day.length; i++) {
        const d = role.day[i];
        const y = TOP + 0.65 + i * rowH;
        await iconCircle(slide, C, d.icon, 6.7, y + 0.05, 0.55, d.title);
        slide.addText(d.title, { x: 7.45, y, w: 5.3, h: 0.4, fontSize: 17, bold: true, color: C.text1, margin: 0, valign: "top", isTextBox: true, objectName: `${d.title} heading` });
        check(ctx.deck, s.title, `day ${d.title}`, heightOf(d.text, 5.3, 15) + 0.42, rowH);
        slide.addText(runs(d.text), { x: 7.45, y: y + 0.4, w: 5.3, h: rowH - 0.44, fontSize: 15, color: C.muted || C.text1, margin: 0, valign: "top", isTextBox: true, objectName: `${d.title} text` });
    }
}

// ---------- the deck ----------

function defineLayouts(pres, C, role) {
    const footer = `Pulaski training · ${role.name}`;
    pres.defineSlideMaster({
        title: "Pulaski title",
        background: { color: HEX.ink },
        objects: [
            { placeholder: { options: { name: "title", type: "title", x: 0.8, y: 2.35, w: 11.7, h: 1.5, fontSize: 46, bold: true, color: "FFFFFF", align: "left", valign: "bottom", margin: 0 }, text: "" } },
            { placeholder: { options: { name: "body", type: "body", x: 0.8, y: 4.05, w: 11.7, h: 1.6, fontSize: 20, color: "A9D8D3", valign: "top", margin: 0 }, text: "" } },
        ],
    });
    pres.defineSlideMaster({
        title: "Pulaski section",
        background: { color: HEX.teal },
        objects: [
            { placeholder: { options: { name: "title", type: "title", x: 0.8, y: 2.55, w: 11.7, h: 1.3, fontSize: 42, bold: true, color: "FFFFFF", align: "left", valign: "bottom", margin: 0 }, text: "" } },
            { placeholder: { options: { name: "body", type: "body", x: 0.8, y: 4.05, w: 11.7, h: 1.6, fontSize: 20, color: HEX.tealSoft, valign: "top", margin: 0 }, text: "" } },
            { text: { text: footer, options: { x: 0.8, y: 6.95, w: 8, h: 0.3, fontSize: 10, color: HEX.tealSoft, margin: 0 } } },
        ],
    });
    pres.defineSlideMaster({
        title: "Pulaski content",
        background: { color: "FFFFFF" },
        margin: [0.5, 0.6, 0.6, 0.6],
        objects: [
            { placeholder: { options: { name: "title", type: "title", x: 0.6, y: 0.45, w: 12.1, h: 0.85, fontSize: 32, bold: true, color: HEX.ink, align: "left", valign: "middle", margin: 0 }, text: "" } },
            { text: { text: footer, options: { x: 0.6, y: 7.0, w: 8, h: 0.3, fontSize: 10, color: HEX.muted, margin: 0 } } },
        ],
        slideNumber: { x: 12.2, y: 7.0, w: 0.5, h: 0.3, fontSize: 10, color: HEX.muted, align: "right" },
    });
}

async function addContent(pres, C, role, s, section) {
    const slide = pres.addSlide({ masterName: "Pulaski content", sectionTitle: section });
    slide.addText(s.title, { placeholder: "title" });
    check(role.name, s.title, "title", heightOf(s.title, 12.1, 32), 0.9);
    const ctx = { slide, C, deck: role.name };
    switch (s.type) {
        case "steps": await stepsSlide(ctx, s); break;
        case "cards": await cardsSlide(ctx, s); break;
        case "compare": await compareSlide(ctx, s); break;
        case "flow": await flowSlide(ctx, s); break;
        case "table": await tableSlide(ctx, s); break;
        case "quiz": await quizSlide(ctx, s); break;
        case "answers": await answersSlide(ctx, s); break;
        case "glance": await glanceSlide(ctx, s, role); break;
        default: throw new Error(`Unknown slide type ${s.type}`);
    }
    if (s.notes) slide.addNotes(s.notes);
}

function sectionSlide(pres, title, subtitle, notes) {
    pres.addSection({ title });
    const slide = pres.addSlide({ masterName: "Pulaski section", sectionTitle: title });
    slide.addText(title, { placeholder: "title" });
    if (subtitle) slide.addText(subtitle, { placeholder: "body" });
    if (notes) slide.addNotes(notes);
}

async function buildDeck(role) {
    const pres = new pptxgen();
    pres.layout = "LAYOUT_WIDE";
    pres.theme = { headFontFace: FONT, bodyFontFace: FONT };
    pres.title = `Pulaski training: ${role.name}`;
    pres.subject = `What the ${role.name} role does in Pulaski, step by step`;
    pres.author = "Pulaski";
    pres.company = "Pulaski";
    const C = pres.SchemeColor;
    defineLayouts(pres, C, role);

    pres.addSection({ title: "Welcome" });
    const t = pres.addSlide({ masterName: "Pulaski title", sectionTitle: "Welcome" });
    t.addText(`${role.name} training`, { placeholder: "title" });
    t.addText(`Pulaski MES · ${role.tagline}`, { placeholder: "body" });
    t.addNotes(role.welcomeNotes || `This course covers what the ${role.name} role does in Pulaski. Each topic has numbered steps for beginners and notes for the details. Pulaski is pre-alpha: screens change, and design/user_guide.md is the reference.`);

    await addContent(pres, C, role, { type: "glance", title: "Your role at a glance", notes: role.glanceNotes }, "Welcome");
    await addContent(pres, C, role, {
        type: "compare", title: "What you can see and change",
        columns: [
            { title: "You can see", icon: "MdVisibility", items: role.sees },
            { title: "You can change", icon: "MdEdit", items: role.changes },
        ],
        notes: "These are the roles a new Pulaski starts with. An administrator may have changed them: Configure → Users, Roles and Permissions tabs, shows what each role may do today. A role is held at a site, so you see that site's data only. Someone with two roles can do what either allows.",
    }, "Welcome");
    if (role.agenda) await addContent(pres, C, role, { type: "cards", title: "What this course covers", cards: role.agenda, notes: "The course follows the order of a working day: the basics first, then each task in the order you meet it." }, "Welcome");

    for (const id of role.modules) {
        const m = modules[id];
        if (!m) throw new Error(`No module ${id} (role ${role.code})`);
        sectionSlide(pres, m.title, m.subtitle, m.notes);
        for (const s of m.slides) await addContent(pres, C, role, s, m.title);
    }

    sectionSlide(pres, "Check what you learned", "A few questions, then where to get help", "Ask the questions aloud, or hand them out. The next slide has the answers: hide it while asking.");
    await addContent(pres, C, role, { type: "quiz", title: "Knowledge check", questions: role.quiz, notes: role.quiz.map((q, i) => `Q${i + 1}: ${plain(q.a)}`).join("\n") }, "Check what you learned");
    await addContent(pres, C, role, { type: "answers", title: "Knowledge check: answers", questions: role.quiz }, "Check what you learned");
    await addContent(pres, C, role, { type: "table", title: "Quick reference", head: ["To…", "Go to"], widths: [5.6, W - 1.2 - 5.6], rows: role.quickref, notes: "A one-page reminder of where each task is done. Print it for the desk or the line." }, "Check what you learned");
    await addContent(pres, C, role, {
        type: "cards", title: "Where to get help",
        cards: [
            { icon: "MdHelpOutline", title: "Help (?)", text: "The **?** at the top right opens the guide's section for the screen you're on. Stations and terminals have a **Help** link instead." },
            { icon: "MdErrorOutline", title: "Read the message", text: "Red and yellow boxes say why something was refused, in plain words. Fix what it says and try again." },
            { icon: "MdLock", title: "Missing a button?", text: "Your roles don't allow that change, or not at this site. Ask an administrator; the message names the permission." },
            { icon: "MdSupportAgent", title: "Ask a person", text: role.helpFrom || "Your supervisor or key user first; your administrator for sign-in, roles and setup." },
        ],
        notes: "Pulaski is pre-alpha. The guide's section 23, What Pulaski doesn't do yet, lists the known gaps, so nobody spends time looking for them.",
    }, "Check what you learned");

    const file = path.join(OUT, `Pulaski training - ${role.name}.pptx`);
    await pres.writeFile({ fileName: file });
    await applyTheme(file, THEME);
    return file;
}

(async () => {
    const only = process.argv[2];
    for (const role of roles.filter((r) => !only || r.code === only)) {
        const file = await buildDeck(role);
        console.log("wrote", path.relative(OUT, file));
    }
    if (warnings.length) {
        console.log(`\n${warnings.length} fit warnings:`);
        warnings.forEach((w) => console.log("  " + w));
        process.exitCode = 2;
    }
})().catch((e) => { console.error(e); process.exit(1); });
