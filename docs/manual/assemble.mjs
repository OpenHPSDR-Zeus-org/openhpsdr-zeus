// Assemble the Zeus Operator's Manual chapters into one print-ready HTML file.
// Reads docs/manual/chapters/NN-*.md (in order), builds a cover + clickable
// table of contents + page-broken chapters, writes docs/manual/build/Zeus-Operator-Manual.html.
// build.sh then prints that to PDF via headless Chrome. See README.md.
import { marked } from 'marked';
import { readFileSync, readdirSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const SRC = join(HERE, 'chapters');
const BUILD = join(HERE, 'build');
const OUT = join(BUILD, 'Zeus-Operator-Manual.html');
const EDITION = process.env.MANUAL_EDITION || 'v0.10.0 — June 2026';
const COVERS = process.env.MANUAL_COVERS || 'Covers the Zeus 0.10.0 release';

// Cover logo, inlined as a data URI so the print engine never depends on a
// relative file path (headless Chrome prints from build/, the asset lives in
// assets/). assets/zeus_manual_logo.png is the brand emblem on a transparent
// background, so it floats on the cover gradient with no box.
const LOGO_DATA_URI =
  'data:image/png;base64,' +
  readFileSync(join(HERE, 'assets', 'zeus_manual_logo.png')).toString('base64');

marked.setOptions({ gfm: true, breaks: false });
mkdirSync(BUILD, { recursive: true });

const slugCounts = new Map();
function slug(text) {
  const base = text.toLowerCase().replace(/<[^>]+>/g, '').replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  const n = slugCounts.get(base) ?? 0;
  slugCounts.set(base, n + 1);
  return n === 0 ? base : `${base}-${n}`;
}
const stripTags = (s) => s.replace(/<[^>]+>/g, '').trim();

const files = readdirSync(SRC).filter((f) => /^\d\d-.*\.md$/.test(f)).sort();
const toc = [];
const chapterHtml = [];
let chapterNum = 0;

for (const f of files) {
  let html = marked.parse(readFileSync(join(SRC, f), 'utf8'));
  const num = ++chapterNum;
  const sections = [];
  let firstH2 = false;
  html = html.replace(/<h([23])>([\s\S]*?)<\/h\1>/g, (m, lvl, inner) => {
    const title = stripTags(inner);
    const id = slug(title);
    if (lvl === '2' && !firstH2) {
      firstH2 = true;
      toc.push({ num, title, id, sections });
      return `<h2 id="${id}"><span class="chnum">${num}</span>${inner}</h2>`;
    }
    if (lvl === '3') sections.push({ title, id });
    return `<h${lvl} id="${id}">${inner}</h${lvl}>`;
  });
  chapterHtml.push(`<section class="chapter">${html}</section>`);
}

const tocHtml = toc.map((c) => `
  <div class="toc-ch"><a href="#${c.id}"><span class="toc-num">${c.num}</span><span class="toc-title">${c.title}</span></a></div>
  ${c.sections.map((s) => `<div class="toc-sec"><a href="#${s.id}">${s.title}</a></div>`).join('')}
`).join('');

const css = `
  :root{ --ink:#1c2330; --muted:#5b6677; --accent:#1f6feb; --rule:#dfe4ec; --panel-top:#2a2f3a; --panel-bot:#10131a; --amber:#f0a028; }
  *{ box-sizing:border-box; } html,body{ margin:0; padding:0; }
  body{ font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Helvetica,Arial,sans-serif; color:var(--ink); font-size:11pt; line-height:1.55; }
  @page{ size:Letter; margin:20mm 18mm 18mm 18mm; }
  a{ color:var(--accent); text-decoration:none; }
  .cover{ page-break-after:always; height:247mm; display:flex; flex-direction:column; justify-content:center; align-items:center; background:#ffffff; color:#111111; text-align:center; margin:-20mm -18mm 0 -18mm; padding:0 22mm; }
  .cover .logo{ width:104mm; max-width:72%; height:auto; margin-bottom:12mm; }
  .cover h1{ font-size:46pt; margin:0; letter-spacing:1px; font-weight:800; }
  .cover .king{ font-size:15pt; color:#111111; letter-spacing:5px; text-transform:uppercase; margin-top:3mm; }
  .cover .sub{ font-size:18pt; color:#111111; margin-top:14mm; font-weight:400; }
  .cover .ed{ margin-top:20mm; font-size:11pt; color:#555555; }
  .cover .covers{ font-size:9.5pt; color:#777777; margin-top:2mm; max-width:120mm; }
  .toc{ page-break-after:always; } .toc h2{ border:0; color:var(--ink); font-size:22pt; margin:0 0 6mm 0; }
  .toc-ch{ margin-top:3.5mm; } .toc-ch a{ color:var(--ink); font-weight:700; font-size:12pt; display:flex; gap:6mm; align-items:baseline; }
  .toc-num{ color:var(--accent); min-width:8mm; font-variant-numeric:tabular-nums; }
  .toc-sec{ margin:0.6mm 0 0 14mm; } .toc-sec a{ color:var(--muted); font-size:9.5pt; }
  .chapter{ page-break-before:always; }
  h2{ font-size:20pt; font-weight:800; color:var(--ink); margin:0 0 5mm 0; padding-bottom:3mm; border-bottom:2px solid var(--accent); display:flex; align-items:baseline; gap:5mm; }
  .chnum{ color:#fff; background:var(--accent); border-radius:5px; font-size:12pt; padding:1mm 3mm; font-weight:800; }
  h3{ font-size:13.5pt; color:var(--accent); margin:7mm 0 2mm 0; } h4{ font-size:11.5pt; margin:5mm 0 1mm 0; }
  p{ margin:0 0 3mm 0; } ul,ol{ margin:0 0 3mm 0; padding-left:7mm; } li{ margin:0.8mm 0; } strong{ color:#0d1b2e; }
  code{ font-family:"SF Mono",Menlo,Consolas,monospace; font-size:9.5pt; background:#eef1f6; padding:0.3mm 1.2mm; border-radius:3px; }
  table{ border-collapse:collapse; width:100%; margin:3mm 0; font-size:9.5pt; } th,td{ border:1px solid var(--rule); padding:1.6mm 2.4mm; text-align:left; vertical-align:top; } th{ background:#f1f4f9; }
  blockquote{ margin:3mm 0; padding:2mm 4mm; border-left:3px solid var(--amber); background:#fff8ec; color:#5a4a2a; }
  h2,h3,h4{ break-after:avoid; } table,blockquote,pre{ break-inside:avoid; }
`;

const cover = `<div class="cover"><img class="logo" src="${LOGO_DATA_URI}" alt="Zeus — Software Defined Radio" /><div class="king">The King of SDRs</div><div class="sub">Operator's Manual</div><div class="ed">${EDITION}</div><div class="covers">${COVERS}</div></div>`;
const doc = `<!doctype html><html><head><meta charset="utf-8"><title>Zeus Operator's Manual</title><style>${css}</style></head><body>${cover}<div class="toc"><h2>Table of Contents</h2>${tocHtml}</div>${chapterHtml.join('\n')}</body></html>`;

writeFileSync(OUT, doc, 'utf8');
console.log(`OK chapters=${toc.length} -> ${OUT}`);
