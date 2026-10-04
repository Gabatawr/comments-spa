/**
 * cascade-compare.mjs — deterministic, backend-free screenshot of the reply
 * cascade for comparison with docs/task/page1-X10.png (914x824).
 *
 * What it does
 *   1. serves src/Frontend/dist/comments-spa/browser over a throw-away HTTP
 *      server (SPA fallback to index.html);
 *   2. stubs every /api/** call with a fixed fixture: a reply cascade 4 levels
 *      deep, every reply carrying a `quotedText` snapshot, long body text;
 *   3. renders it in Chromium at a viewport whose cascade area is close to the
 *      reference canvas width (its inner card then matches the reference 850px card), screenshots ONLY the cascade wrapper;
 *   4. writes docs/qa/design-v2/cascade-<timestamp>.png (+ reference copy) and
 *      builds side-by-side / diff images through compose-compare.py (PIL);
 *   5. dumps computed-style metrics of every cascade level to
 *      docs/qa/design-v2/metrics-<timestamp>.json — the numbers behind the
 *      honest divergence list in docs/qa/design-v2/README.md.
 *
 * Usage (from tests/frontend):
 *   PLAYWRIGHT_BROWSERS_PATH=$PWD/../../.playwright \
 *   NPM_CONFIG_CACHE=$PWD/../../.npm-cache node cascade-compare.mjs
 *
 * Env: BASE (skip the built-in server and use an external URL),
 *      DIST, OUT_DIR, VIEWPORT (default 1000), DSF (default 2).
 */
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../..');
const DIST = process.env.DIST ?? path.join(ROOT, 'src/Frontend/dist/comments-spa/browser');
const OUT_DIR = process.env.OUT_DIR ?? path.join(ROOT, 'docs/qa/design-v2');
const REF = path.join(ROOT, 'docs/task/page1-X10.png');
const VIEWPORT = Number(process.env.VIEWPORT ?? 1000);
const DSF = Number(process.env.DSF ?? 2);
const stamp = () => new Date().toISOString().replace(/[-:]/g, '').replace(/\..+$/, '').replace('T', '-');
const TAG = process.env.TAG ?? stamp();

fs.mkdirSync(OUT_DIR, { recursive: true });
if (!process.env.BASE && !fs.existsSync(path.join(DIST, 'index.html'))) {
  console.error(`No built app at ${DIST}. Run: cd src/Frontend && npm run build`);
  process.exit(2);
}

// ------------------------------------------------------------------ fixtures
const now = Date.now();
const iso = (min) => new Date(now - min * 60000).toISOString();

/** Mirror of the backend QuoteSnapshot rule (docs/DESIGN-v2.1-decisions.md §1.1). */
function quote(textPlain) {
  const flat = String(textPlain).replace(/<[^>]+>/g, '').replace(/\s+/g, ' ').trim();
  if (!flat) return null;
  if (flat.length <= 160) return flat;
  let cut = flat.slice(0, 160);
  const sp = cut.lastIndexOf(' ');
  if (sp > 0) cut = cut.slice(0, sp);
  return `${cut}…`;
}

const BODY = {
  root1:
    '<p>Каждый из нас понимает очевидную вещь: семантический разбор внешних противодействий предоставляет широкие возможности.</p>',
  root2:
    '<p>Безусловно, постоянное информационно-пропагандистское обеспечение нашей деятельности предопределяет высокую востребованность позиций, занимаемых участниками в отношении поставленных задач.</p>',
  d1:
    '<p>Внезапно, тщательные исследования конкурентов, которые представляют собой <strong>яркий пример</strong> континентально-европейского типа политической культуры, будут ассоциативно распределены по отраслям.</p>',
  d2:
    '<p>Идейные соображения высшего порядка, а также понимание сути <i>ресурсосберегающих</i> технологий играет определяющее значение для новых принципов формирования материально-технической и кадровой базы!</p>',
  d2b: '<p>А ещё интерактивные прототипы являются только методом политического.</p>',
  d3:
    '<p>Равным образом, дальнейшее развитие различных форм деятельности позволяет оценить значение системы массового участия. Задача организации, в особенности же рамки и место обучения кадров в значительной степени обусловливает создание направлений прогрессивного развития.</p>',
};

const plainOf = (html) => html.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim();

const mk = (id, parentId, userName, email, homePage, text, createdAt, replies = [], quotedText = null) => ({
  id,
  parentId,
  userName,
  email,
  homePage,
  text,
  textPlain: plainOf(text),
  quotedText,
  createdAt,
  clientIp: `203.0.113.${id}`,
  userAgent: 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126 Safari/537.36',
  attachment: null,
  replyCount: replies.length,
  replies,
});

const d3 = mk(5, 3, 'Anonym', 'anon@example.com', null, BODY.d3, iso(10), [], quote(BODY.d2b));
const d2 = mk(4, 2, 'Anonym', 'anon@example.com', null, BODY.d2b, iso(25), [d3], quote(BODY.d2));
const d1 = mk(3, 1, 'Anonym', 'anon@example.com', null, BODY.d2, iso(40), [d2], quote(BODY.d1));
const root = mk(
  1,
  null,
  'Anonym',
  'anon@example.com',
  'https://example.com/',
  BODY.root1 + BODY.root2,
  iso(90),
  [
    mk(2, 1, 'Rum_8', 'rum8@example.com', 'https://rum8.dev/', BODY.d1, iso(60), [d1], quote(BODY.root1)),
  ],
);

const pageDto = {
  items: [root],
  page: 1,
  pageSize: 25,
  totalItems: 1,
  totalPages: 1,
  sortBy: 'createdAt',
  sortDir: 'desc',
  nextCursor: null,
};

const PNG_1PX =
  'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==';

// ------------------------------------------------------------------ server
const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon',
  '.png': 'image/png',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2',
  '.txt': 'text/plain; charset=utf-8',
};

function startServer(root) {
  return new Promise((resolve) => {
    const srv = http.createServer((req, res) => {
      const url = new URL(req.url, 'http://localhost');
      let file = path.join(root, decodeURIComponent(url.pathname));
      if (!file.startsWith(root)) {
        res.writeHead(403).end('forbidden');
        return;
      }
      if (url.pathname === '/' || !fs.existsSync(file) || fs.statSync(file).isDirectory()) {
        file = path.join(root, 'index.html');
      }
      fs.readFile(file, (err, buf) => {
        if (err) {
          res.writeHead(404).end('not found');
          return;
        }
        res.writeHead(200, { 'content-type': MIME[path.extname(file)] ?? 'application/octet-stream' });
        res.end(buf);
      });
    });
    srv.listen(0, '127.0.0.1', () => resolve({ srv, port: srv.address().port }));
  });
}

// ------------------------------------------------------------------ browser
const { chromium } = await import('playwright');
let server = null;
let BASE = process.env.BASE;
if (!BASE) {
  const started = await startServer(DIST);
  server = started.srv;
  BASE = `http://127.0.0.1:${started.port}`;
}

const browser = await chromium.launch({ headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
const context = await browser.newContext({
  viewport: { width: VIEWPORT, height: 900 },
  deviceScaleFactor: DSF,
});
const page = await context.newPage();

const json = (route, o) =>
  route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(o) });

await page.route('**/api/**', (route) => {
  const u = route.request().url();
  if (u.includes('/api/health')) {
    return json(route, {
      status: 'ok', database: 'ok', cache: 'ok', redis: 'ok', broker: 'ok',
      search: 'ok', storage: 'ok', version: '2.0.0',
      queue: { pending: 0, processed: 1827 }, websocket: { clients: 0 },
    });
  }
  if (u.includes('/api/captcha')) return json(route, { captchaId: 'fixture', image: PNG_1PX, expiresInSeconds: 300 });
  if (u.includes('/api/preview')) return json(route, { valid: true, html: '<strong>ok</strong>', plain: 'ok', errors: [] });
  if (u.includes('/api/comments') || u.includes('/api/graphql')) return json(route, pageDto);
  return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' });
});

const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const CASCADE = ['[data-testid="reply-cascade"]', '.cascade', '[class*="cascade"]', '[data-testid="reply-children"]'];

async function firstVisible(sel, timeout = 6000) {
  const deadline = Date.now() + timeout;
  for (;;) {
    const loc = page.locator(sel).first();
    try {
      if ((await loc.count()) > 0 && (await loc.isVisible())) return loc;
    } catch { /* retry */ }
    if (Date.now() > deadline) return null;
    await wait(150);
  }
}

async function findCascade() {
  for (const sel of CASCADE) {
    const loc = await firstVisible(sel, 4000);
    if (loc) return { loc, sel };
  }
  return null;
}

/**
 * Comparison target: the cascade wrapper `.cascade` (data-testid="reply-cascade").
 * At 914 CSS px wide its inner comment card is exactly 850 px — the width of the
 * reference card (x 32..881 of a 914 canvas) — and it carries the page-level
 * 24px top / 32px side gaps, so it maps 1:1 onto the reference canvas.
 */
async function findTarget() {
  const cascade = await findCascade();
  return cascade;
}

const REF_WIDTH = 914;

const notes = [];
const note = (s) => { if (!notes.includes(s)) notes.push(s); };
console.log(`cascade-compare: BASE=${BASE} viewport=${VIEWPORT}x900 dsf=${DSF}`);

/** Load the app, expand the first cascade, return the screenshot target. */
async function render() {
  await page.goto(BASE, { waitUntil: 'domcontentloaded', timeout: 45000 });
  await page.waitForLoadState('networkidle', { timeout: 20000 }).catch(() => {});
  await page.waitForTimeout(1500);

  // Expand the cascade if it is rendered behind a toggle.
  let loc = await findTarget();
  if (!loc) {
    const toggles = [
      '[data-testid="reply-toggle"]',
      '[data-testid="cascade-toggle"]',
      'button:has-text("ответ")',
      'button:has-text("Свернуть")',
    ];
    for (const sel of toggles) {
      const t = page.locator(sel).first();
      if ((await t.count()) > 0) {
        await t.click().catch(() => {});
        await page.waitForTimeout(800);
        loc = await findTarget();
        if (loc) { note(`expanded via ${sel}`); break; }
      }
    }
  }
  if (!loc) return null;
  await loc.loc.scrollIntoViewIfNeeded().catch(() => {});
  await page.waitForTimeout(300);
  return { loc: loc.loc, sel: loc.sel, cascadeSel: loc.cascadeSel, box: await loc.loc.boundingBox() };
}

// Pass 1: measure. Pass 2 (if needed): resize the viewport so the card is
// 850 CSS px wide — the same width the card occupies in the 914x824 reference
// (card x 32..881). Then the raw PNG scales ~1:1 with no resampling blur.
let cascade = await render();
if (cascade?.box) {
  const delta = REF_WIDTH - cascade.box.width;
  if (Math.abs(delta) > 8) {
    const want = Math.max(480, Math.min(1800, VIEWPORT + delta));
    console.log(`cascade width ${cascade.box.width.toFixed(1)}px -> resizing viewport ${VIEWPORT} -> ${want}`);
    await page.setViewportSize({ width: want, height: 900 });
    const again = await render();
    if (again) cascade = again;
    note(`viewport adjusted ${VIEWPORT} -> ${want} to match reference canvas width ${REF_WIDTH}px`);
  }
}

if (!cascade) {
  console.error('FAIL: cascade container not found (tried ' + CASCADE.join(', ') + ')');
  await page.screenshot({ path: path.join(OUT_DIR, `cascade-NOT-FOUND-${TAG}.png`), fullPage: true });
  await browser.close();
  server?.close();
  process.exit(1);
}

const cards = await page.locator('[data-testid="comment-card"]').count();
console.log(`screenshot target: ${cascade.sel}; comment cards: ${cards}`);
if (cards < 3) note(`only ${cards} comment cards rendered (expected >=3 levels)`);

await cascade.loc.scrollIntoViewIfNeeded().catch(() => {});
await page.waitForTimeout(400);

const box = await cascade.loc.boundingBox();
const rawPath = path.join(OUT_DIR, `cascade-${TAG}.png`);
await cascade.loc.screenshot({ path: rawPath });
await page.screenshot({ path: path.join(OUT_DIR, `page-${TAG}.png`), fullPage: true });

// ------------------------------------------------------------- metrics dump
const metrics = await page.evaluate(() => {
  const rgb = (c) => {
    const m = /rgba?\(([^)]+)\)/.exec(c || '');
    if (!m) return c;
    const p = m[1].split(',').map((v) => parseFloat(v));
    const hx = (n) => Math.round(n).toString(16).padStart(2, '0');
    return `#${hx(p[0])}${hx(p[1])}${hx(p[2])}`;
  };
  const cs = (el) => (el ? getComputedStyle(el) : null);
  const rect = (el) => {
    if (!el) return null;
    const r = el.getBoundingClientRect();
    return { x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height) };
  };
  const cards = [...document.querySelectorAll('[data-testid="comment-card"]')];
  const cascadeEl = document.querySelector('[data-testid="reply-cascade"], .cascade, [class*="cascade"]');
  return {
    cascade: rect(cascadeEl),
    cards: cards.map((card) => {
      const depth = Number(card.getAttribute('data-depth') ?? 0);
      const cardRect = card.getBoundingClientRect();
      const descendants = [...card.querySelectorAll('*')];
      // header band = widest non-transparent child, 30..80px tall
      let head = null;
      for (const el of descendants) {
        const s = cs(el);
        if (!s || s.backgroundColor === 'rgba(0, 0, 0, 0)' || s.backgroundColor === 'transparent') continue;
        const r = el.getBoundingClientRect();
        if (r.width >= cardRect.width * 0.85 && r.height >= 30 && r.height <= 90) {
          if (!head || r.height > head.r.height) head = { el, r, s };
        }
      }
      const headerImage = { h: head ? Math.round(head.r.height) : null, bg: head ? rgb(head.s.backgroundColor) : null };
      let contentLeft = null;
      if (head) {
        for (const el of head.el.querySelectorAll('*')) {
          const r = el.getBoundingClientRect();
          if (r.width > 0 && r.height > 0) contentLeft = contentLeft === null ? Math.round(r.x) : Math.min(contentLeft, Math.round(r.x));
        }
      }
      const avatar = card.querySelector('[class*="avatar"], img[class*="avatar"]');
      const name = card.querySelector('[class*="username"], [class*="user-name"]')
        ?? card.querySelector('[class*="author"]');
      const time = card.querySelector('time');
      const svgs = [...card.querySelectorAll('svg')];
      const body = card.querySelector('[data-testid="comment-text"], [class*="body"]');
      const quoteEl = card.querySelector('[class*="quote"]');
      const voteEl = card.querySelector('[class*="vote"]');
      const quoteBar = quoteEl
        ? (() => {
            const s = cs(quoteEl);
            const r = quoteEl.getBoundingClientRect();
            return {
              left: Math.round(r.x),
              borderLeft: s.borderLeftWidth + ' ' + s.borderLeftStyle + ' ' + rgb(s.borderLeftColor),
              textColor: rgb(s.color),
              fontStyle: s.fontStyle,
            };
          })()
        : null;
      const quoteText = quoteEl ? (quoteEl.textContent || '').replace(/\s+/g, ' ').trim() : null;
      return {
        depth,
        rect: { x: Math.round(cardRect.x), y: Math.round(cardRect.y), w: Math.round(cardRect.width), h: Math.round(cardRect.height) },
        header: headerImage,
        contentLeft,
        avatar: rect(avatar),
        name: name ? { text: name.textContent.trim(), fontSize: cs(name).fontSize, color: rgb(cs(name).color), fontWeight: cs(name).fontWeight } : null,
        date: time ? { text: time.textContent.trim(), fontSize: cs(time).fontSize, color: rgb(cs(time).color) } : null,
        iconCount: svgs.length,
        iconSizes: svgs.slice(0, 6).map((s) => { const r = s.getBoundingClientRect(); return `${Math.round(r.width)}x${Math.round(r.height)}`; }),
        iconColor: svgs.length ? rgb(cs(svgs[0]).color) : null,
        body: body ? { left: Math.round(body.getBoundingClientRect().x), fontSize: cs(body).fontSize, color: rgb(cs(body).color), lineHeight: cs(body).lineHeight } : null,
        quote: quoteBar ? { ...quoteBar, text: quoteText.slice(0, 60) } : null,
        vote: voteEl ? { text: voteEl.textContent.replace(/\s+/g, ' ').trim(), color: rgb(voteEl ? cs(voteEl).color : '') } : null,
      };
    }),
  };
});

const metricsPath = path.join(OUT_DIR, `metrics-${TAG}.json`);
fs.writeFileSync(
  metricsPath,
  JSON.stringify({ tag: TAG, base: BASE, viewport: { width: VIEWPORT, height: 900 }, dsf: DSF, cascadeBox: box, notes, ...metrics }, null, 2),
);

// ------------------------------------------------------------------ compose
fs.copyFileSync(REF, path.join(OUT_DIR, 'reference.png'));
let composed = null;
try {
  const out = execFileSync(
    'python3',
    [path.join(HERE, 'compose-compare.py'), '--reference', REF, '--ours', rawPath, '--out-dir', OUT_DIR, '--tag', TAG, '--width', String(REF_WIDTH)],
    { encoding: 'utf8' },
  );
  composed = JSON.parse(out.trim().split('\n').pop());
  console.log(`python: ${JSON.stringify(composed)}`);
} catch (e) {
  note(`PIL compose failed: ${e.message}`);
  console.error(`PIL compose failed: ${e.message}`);
  if (e.stdout) console.error(String(e.stdout));
  if (e.stderr) console.error(String(e.stderr));
}

console.log(JSON.stringify({
  raw: rawPath,
  page: path.join(OUT_DIR, `page-${TAG}.png`),
  reference: path.join(OUT_DIR, 'reference.png'),
  metrics: metricsPath,
  cascadeSelector: cascade.sel,
  commentCards: cards,
  cascadeBox: box,
  composed,
  notes,
}, null, 2));

await browser.close();
server?.close();
process.exit(cards >= 3 ? 0 : 1);
