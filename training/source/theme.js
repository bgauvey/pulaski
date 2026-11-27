// Writes the deck's own colours and theme name into ppt/theme/theme1.xml: pptxgenjs sets the fonts but keeps Office's palette.
const fs = require("fs");
const JSZip = require(require.resolve("jszip", { paths: [require.resolve("pptxgenjs")] }));

const escape = (s) => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");

async function applyTheme(file, theme) {
    const zip = await JSZip.loadAsync(fs.readFileSync(file));
    const names = Object.keys(zip.files).filter((n) => /^ppt\/theme\/theme\d+\.xml$/.test(n));
    for (const name of names) {
        let xml = await zip.file(name).async("string");
        for (const [slot, hex] of Object.entries(theme.colors)) {
            if (!/^[0-9A-Fa-f]{6}$/.test(hex)) throw new Error(`Colour ${slot} isn't six hex digits: ${hex}`);
            const re = new RegExp(`(<a:${slot}>)[\\s\\S]*?(</a:${slot}>)`);
            xml = xml.replace(re, `$1<a:srgbClr val="${hex.toUpperCase()}"/>$2`);
        }
        xml = xml.replace(/(<a:theme [^>]*name=")[^"]*(")/, `$1${escape(theme.name)}$2`).replace(/(<a:clrScheme name=")[^"]*(")/, `$1${escape(theme.name)}$2`);
        zip.file(name, xml);
    }
    // pptxgenjs writes paragraph properties before every run of a paragraph; only the first is valid (and readers disagree on
    // which they use), so keep that one.
    for (const name of Object.keys(zip.files).filter((n) => /^ppt\/slides\/slide\d+\.xml$/.test(n))) {
        const xml = await zip.file(name).async("string");
        const fixed = xml.replace(/<a:p>([\s\S]*?)<\/a:p>/g, (para, inner) => {
            let seen = false;
            const kept = inner.replace(/<a:pPr\b[^>]*?(?:\/>|>[\s\S]*?<\/a:pPr>)/g, (pPr) => {
                if (seen) return "";
                seen = true;
                return pPr;
            });
            return `<a:p>${kept}</a:p>`;
        });
        zip.file(name, fixed);
    }
    // Every colour written anywhere must be six hex digits; a scheme name in a hex-only option turns into something else.
    for (const name of Object.keys(zip.files).filter((n) => n.endsWith(".xml"))) {
        const xml = await zip.file(name).async("string");
        const bad = [...xml.matchAll(/<a:srgbClr val="([^"]*)"/g)].map((m) => m[1]).filter((v) => !/^[0-9A-Fa-f]{6}$/.test(v));
        if (bad.length) throw new Error(`${name} has colours that aren't hex: ${[...new Set(bad)].join(", ")}`);
    }
    // Folder entries trouble cloud services that open the deck; a .pptx needs none.
    for (const name of Object.keys(zip.files)) if (zip.files[name].dir) delete zip.files[name];
    fs.writeFileSync(file, await zip.generateAsync({ type: "nodebuffer", compression: "DEFLATE" }));
}

module.exports = { applyTheme };
