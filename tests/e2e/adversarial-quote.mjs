#!/usr/bin/env node
/**
 * Adversarial real-browser + client-sanitizer verification for v2.1 quotes
 * (task-4; docs/DESIGN-v2.1-decisions.md §1.1/§1.2, docs/DESIGN-v2.md §3/§4).
 *
 * Two independent parts:
 *   A. Runs the actually-changed src/Frontend/src/app/core/sanitize.ts under
 *      jsdom and tries to smuggle dangerous HTML through it (regression risk of
 *      the newly added <br> block-boundary handling).
 *   B. Drives the real Angular SPA in headless Chromium:
 *      quote rendered as TEXT (no script/img/iframe inside .comment-quote),
 *      voting is inert + sends no request, author e-mail/IP/site are not text
 *      in the header but are reachable through the 4th icon.
 *
 * Fixture data is produced by tests/e2e/adversarial-quote.sh and passed via
 * Q_FIXTURE. Exit code 0 = all checks passed, 1 = at least one FAIL.
 *
 * Written by the independent verifier, not by the feature authors.
 */
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';

const ROOT = process.env.QROOT || process.cwd();
const BASE = (process.env.BASE || 'http://localhost:8080').replace(/\/$/, '');
const FIXTURE_PATH = process.env.Q_FIXTURE;

let pass = 0;
let fail = 0;
const check = (name, ok, detail = '') => {
  if (ok) { pass++; console.log(`  PASS  ${name}${detail ? ' :: ' + detail : ''}`); }
  else { fail++; console.log(`  FAIL  ${name} :: ${detail}`); }
};

// =========================================================== A. sanitize.ts
console.log('\n== A. client sanitize.ts under jsdom (regression probes) ==');
const require = createRequire(path.join(ROOT, 'tests/frontend/package.json'));
const { JSDOM } = require('jsdom');
const dom = new JSDOM('<!doctype html><html><body></body></html>', { url: `${BASE}/` });
globalThis.document = dom.window.document;
globalThis.window = dom.window;
const sanitize = await import(
  pathToFileURL(path.join(ROOT, 'src/Frontend/src/app/core/sanitize.ts')).href
);

const DANGEROUS_TAGS =
  'script,img,iframe,svg,object,embed,style,link,meta,form,input,button,math,template';
function inspect(html) {
  const box = dom.window.document.createElement('div');
  box.innerHTML = html;
  const badTags = Array.from(box.querySelectorAll(DANGEROUS_TAGS)).map((e) => e.tagName.toLowerCase());
  const badAttrs = [];
  for (const el of Array.from(box.querySelectorAll('*'))) {
    for (const attr of Array.from(el.attributes)) {
      if (/^on/i.test(attr.name)) badAttrs.push(`${el.tagName.toLowerCase()}[${attr.name}]`);
      if (attr.name === 'href' && /(javascript:|data:|vbscript:)/i.test(attr.value.replace(/\s+/g, ''))) {
        badAttrs.push(`${el.tagName.toLowerCase()}[href=${attr.value.slice(0, 40)}]`);
      }
    }
  }
  return { badTags, badAttrs };
}

const probes = [
  ['svg onload', '<svg onload=alert(1)>x</svg>'],
  ['svg/onload no space', '<svg/onload=alert(1)>'],
  ['iframe javascript:', '<iframe src="javascript:alert(1)"></iframe>'],
  ['img onerror', '<img src=x onerror=alert(1)>'],
  ['img onerror slash', '<img/src=x/onerror=alert(1)>'],
  ['a javascript: href', '<a href="javascript:alert(1)">click</a>'],
  ['a mixed-case javascript:', '<a href="JaVaScRiPt:alert(1)">click</a>'],
  ['a whitespace javascript:', '<a href="  java\tscript:alert(1)  ">click</a>'],
  ['a hex-entity javascript:', '<a href="&#x6a;avascript:alert(1)">click</a>'],
  ['a data:text/html', '<a href="data:text/html,<script>alert(1)</script>">x</a>'],
  ['object/embed', '<object data="x"><embed src="javascript:1"></object>'],
  ['style expression', '<style>body{background:url(javascript:alert(1))}</style>'],
  ['form/input', '<form action="javascript:alert(1)"><input onfocus=alert(1)></form>'],
  ['body onload', '<body onload=alert(1)>'],
  ['strong with handler', '<strong onclick=alert(1)>b</strong>'],
  ['code with handler', '<code onmouseover=alert(1)>c</code>'],
  ['block tags with handler', '<div onclick=alert(1)><p onmouseover=alert(1)>t</p></div>'],
  ['table injection', '<table background="javascript:alert(1)"><tr><td>x</td></tr></table>'],
  ['script mixed case', '<ScRiPt>alert(1)</sCrIpT>'],
  ['encoded script text', '&lt;script&gt;alert(1)&lt;/script&gt;'],
];
for (const [name, payload] of probes) {
  let out;
  try { out = sanitize.sanitizeHtml(payload); }
  catch (e) { check(`A sanitize ${name}`, false, `threw ${e.message}`); continue; }
  const { badTags, badAttrs } = inspect(out);
  const sink = /<script|<svg|<iframe|<img|<object|<embed|<style|onerror\s*=|onload\s*=|javascript\s*:/i.test(out);
  check(
    `A sanitize ${name}`,
    badTags.length === 0 && badAttrs.length === 0 && !sink,
    `out=${JSON.stringify(out).slice(0, 100)} badTags=[${badTags}] badAttrs=[${badAttrs}]`,
  );
}

// The new <br> boundary must not reintroduce elements other than <br>.
const blockCases = [
  ['p p', '<p>one</p><p>two</p>', ['one', 'two']],
  ['div div', '<div>one</div><div>two</div>', ['one', 'two']],
  ['table', '<table><tr><td>x</td></tr></table>', ['x']],
  ['ul li', '<ul><li>a</li><li>b</li></ul>', ['a', 'b']],
];
for (const [name, payload, needles] of blockCases) {
  const out = sanitize.sanitizeHtml(payload);
  const box = dom.window.document.createElement('div');
  box.innerHTML = out;
  const nonBr = Array.from(box.querySelectorAll('*')).filter((e) => e.tagName.toLowerCase() !== 'br');
  const text = (box.textContent || '').replace(/\s+/g, ' ').trim();
  check(
    `A block-boundary ${name} keeps only <br>`,
    nonBr.length === 0 && needles.every((n) => text.includes(n)) && !/<(script|img|iframe|svg)/i.test(out),
    `out=${JSON.stringify(out)} nonBr=[${nonBr.map((e) => e.tagName)}] text=${JSON.stringify(text)}`,
  );
}
const okHref = sanitize.sanitizeHtml('<a href="https://example.com/x" title="t">ok</a>');
check('A safe href preserved', /href="https:\/\/example\.com\/x"/.test(okHref), `out=${okHref}`);
// findTagErrors is an input validator (tags + href scheme). Event attributes on
// allow-listed tags are stripped by sanitizeHtml (checked above), so they are not
// a findTagErrors concern; only tag/href violations are asserted here.
const validatorProbes = probes.filter(([name]) =>
  /svg|iframe|img|object|style|form|body|script mixed/.test(name) || /javascript:|data:text/.test(name),
);
for (const [name, payload] of validatorProbes) {
  const errs = sanitize.findTagErrors(payload);
  check(`A findTagErrors rejects ${name}`, Array.isArray(errs) && errs.length > 0, `errors=${JSON.stringify(errs).slice(0, 100)}`);
}
for (const [name, payload] of [['strong onclick', '<strong onclick=alert(1)>b</strong>'], ['code onmouseover', '<code onmouseover=alert(1)>c</code>']]) {
  const out = sanitize.sanitizeHtml(payload);
  check(`A sanitize strips handler from allow-listed tag (${name})`, !/on\w+\s*=/i.test(out), `out=${out}`);
}

// =========================================================== B. Playwright
console.log('\n== B. real-browser DOM checks ==');
if (!FIXTURE_PATH || !fs.existsSync(FIXTURE_PATH)) {
  if (process.env.Q_REQUIRE_BROWSER === '1') {
    check('B fixture available', false, `Q_FIXTURE=${FIXTURE_PATH}`);
  } else {
    console.log(`  SKIP  B real-browser DOM checks :: Q_FIXTURE not set`);
  }
  console.log(`\nRESULT pass=${pass} fail=${fail}`);
  process.exit(fail ? 1 : 0);
}
const fixture = JSON.parse(fs.readFileSync(FIXTURE_PATH, 'utf8'));

const { chromium } = require('playwright');
const browser = await chromium.launch({ headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await context.newPage();

const dialogs = [];
const pageErrors = [];
page.on('dialog', async (d) => { dialogs.push(d.message()); await d.dismiss().catch(() => {}); });
page.on('pageerror', (e) => pageErrors.push(String(e.message || e)));
const requests = [];
page.on('request', (r) => requests.push(r.url()));

await page.goto(BASE, { waitUntil: 'domcontentloaded' });
await page
  .waitForSelector('[data-testid="comments-status"][data-state="ok"], [data-testid="comments-table"]', {
    timeout: 20000,
  })
  .catch(() => {});
await page.waitForTimeout(800);

async function goFirstPage() {
  const first = page.locator('[data-testid="page-num-1"]');
  if ((await first.count()) > 0) {
    const disabled = await first.first().isDisabled().catch(() => false);
    if (!disabled) {
      await first.first().click().catch(() => {});
      await page.waitForTimeout(500);
    }
  }
}
async function locateRow(rootId) {
  await goFirstPage();
  for (let attempt = 0; attempt < 10; attempt++) {
    const row = page.locator(`[data-testid="comment-row"][data-comment-id="${rootId}"]`);
    if ((await row.count()) > 0) return row.first();
    const next = page.locator('[data-testid="page-next"]');
    if ((await next.count()) === 0 || (await next.first().isDisabled().catch(() => true))) break;
    await next.first().click().catch(() => {});
    await page.waitForTimeout(500);
  }
  return null;
}
async function expandRoot(rootId) {
  const row = await locateRow(rootId);
  if (!row) return null;
  let card = page.locator(`article[data-comment-id="${rootId}"]`);
  if ((await card.count()) === 0) {
    const toggle = row.locator('[data-testid="reply-toggle"]');
    if ((await toggle.count()) > 0) {
      await toggle.first().click().catch(() => {});
      await page.waitForTimeout(400);
    }
    card = page.locator(`article[data-comment-id="${rootId}"]`);
  }
  return (await card.count()) > 0 ? card.first() : null;
}

// ---- B1. quote snapshot rendered as text -----------------------------------
for (const c of fixture.cases || []) {
  const card = await expandRoot(c.rootId);
  if (!card) {
    check(`B1 cascade ${c.label}`, false, `root ${c.rootId} not found on any page`);
    continue;
  }
  const replyCard = card.locator(`article[data-comment-id="${c.replyId}"]`);
  check(`B1 cascade ${c.label} reply card`, (await replyCard.count()) > 0, `reply ${c.replyId}`);
  const quote = replyCard.locator('.comment-quote');
  check(`B1 ${c.label} .comment-quote rendered`, (await quote.count()) === 1, `count=${await quote.count()}`);
  const dangerous = await replyCard
    .locator('.comment-quote script, .comment-quote img, .comment-quote iframe, .comment-quote svg, .comment-quote a, .comment-quote style, .comment-quote object, .comment-quote embed')
    .count();
  check(`B1 ${c.label} no elements inside .comment-quote`, dangerous === 0, `dangerous=${dangerous}`);
  const text = await replyCard.locator('.comment-quote-text').textContent().catch(() => null);
  check(
    `B1 ${c.label} quote text is the exact literal`,
    text === c.expectedQuote,
    `got=${JSON.stringify((text || '').slice(0, 120))} want=${JSON.stringify(c.expectedQuote).slice(0, 120)}`,
  );
  const html = await quote.first().innerHTML().catch(() => '');
  check(`B1 ${c.label} quote innerHTML has no raw tag`, !/<script|<img|<iframe|<svg/i.test(html), `html=${JSON.stringify(html).slice(0, 120)}`);
  if (/[<>]/.test(c.expectedQuote)) {
    check(`B1 ${c.label} angle brackets escaped in DOM`, html.includes('&lt;') && html.includes('&gt;'), `html=${JSON.stringify(html).slice(0, 120)}`);
  }
  const childTags = await quote
    .first()
    .evaluate((el) => Array.from(el.querySelectorAll('*')).map((e) => e.tagName.toLowerCase()));
  check(
    `B1 ${c.label} quote children are text-bearing only`,
    childTags.every((t) => ['span', 'em', 'strong', 'code', 'a', 'br'].includes(t)),
    `children=[${childTags}]`,
  );
  check(`B1 ${c.label} no script in reply card`, (await replyCard.locator('script').count()) === 0, '');
}
const globalDanger = await page
  .locator('.comment-quote script, .comment-quote img, .comment-quote iframe, .comment-quote svg, .comment-quote object, .comment-quote embed')
  .count();
check('B1 no dangerous element anywhere inside .comment-quote', globalDanger === 0, `count=${globalDanger}`);
check('B1 no alert()/dialog fired by quote rendering', dialogs.length === 0, `dialogs=${JSON.stringify(dialogs)}`);

// ---- B2. decorative voting --------------------------------------------------
{
  const card = await expandRoot(fixture.voteCommentId);
  if (!card) {
    check('B2 voting card', false, `root ${fixture.voteCommentId} not found`);
  } else {
    // Scope to the card's own header: nested reply cards also carry a vote block.
    const vote = card.locator('.comment-head').first().locator('[data-testid="comment-vote"]');
    check('B2 vote element present', (await vote.count()) === 1, `count=${await vote.count()}`);
    check('B2 aria-hidden=true', (await vote.getAttribute('aria-hidden')) === 'true', '');
    const vt = ((await vote.innerText().catch(() => '')) || '').replace(/\s+/g, '');
    check('B2 text is ↑0↓', vt === '↑0↓', `text=${JSON.stringify(vt)}`);
    const interactive = await vote
      .locator('button,a,input,select,textarea,[role="button"],[tabindex]')
      .count();
    check('B2 no interactive descendants', interactive === 0, `count=${interactive}`);
    check(
      'B2 pointer-events none',
      (await vote.evaluate((el) => getComputedStyle(el).pointerEvents)) === 'none',
      `pointer-events=${await vote.evaluate((el) => getComputedStyle(el).pointerEvents)}`,
    );
    check('B2 no onclick attribute', (await vote.evaluate((el) => el.getAttribute('onclick'))) === null, '');
    requests.length = 0;
    await vote.dispatchEvent('click');
    await page.waitForTimeout(800);
    const voteReqs = requests.filter((u) => /vote|rating|score/i.test(u) || /\/api\//.test(u));
    check('B2 click sends no request', voteReqs.length === 0, `requests=${JSON.stringify(voteReqs.slice(0, 5))}`);
    const vt2 = ((await vote.innerText().catch(() => '')) || '').replace(/\s+/g, '');
    check('B2 state unchanged after click', vt2 === '↑0↓', `text=${JSON.stringify(vt2)}`);
    let realClickBlocked = false;
    try { await vote.click({ timeout: 1500 }); } catch { realClickBlocked = true; }
    console.log(`  INFO  B2 real mouse click intercepted by pointer-events:none = ${realClickBlocked}`);
  }
}

// ---- B3. author info: not in header, reachable through icon 4 ---------------
{
  const a = fixture.author;
  const card = a ? await expandRoot(a.rootId) : null;
  if (!a || !card) {
    check('B3 author card', false, `author fixture/root missing (${a && a.rootId})`);
  } else {
    const ownHead = card.locator('.comment-head').first();
    const headerText = (await ownHead.innerText().catch(() => '')) || '';
    check('B3 e-mail absent from header text', !headerText.includes(a.email) && !headerText.includes('@'), `header=${JSON.stringify(headerText).slice(0, 160)}`);
    check('B3 IP absent from header text', !a.clientIp || !headerText.includes(a.clientIp), `header=${JSON.stringify(headerText).slice(0, 160)}`);
    check(
      'B3 home page absent from header text',
      !headerText.includes(a.homePage) && !/https?:\/\//i.test(headerText) && !headerText.includes('example.com'),
      `header=${JSON.stringify(headerText).slice(0, 160)}`,
    );
    check('B3 popover closed by default', (await card.locator('[data-testid="author-info"]').count()) === 0, '');
    const infoBtn = ownHead.locator('[data-testid="author-info-button"]');
    check('B3 icon #4 exists with SVG', (await infoBtn.count()) === 1 && (await infoBtn.locator('svg').count()) === 1, '');
    await infoBtn.click().catch(() => {});
    await page.waitForTimeout(300);
    const pop = card.locator('[data-testid="author-info"]');
    check('B3 icon #4 opens the author popover', (await pop.count()) === 1 && (await pop.first().isVisible()), `count=${await pop.count()}`);
    const email = await card.locator('[data-testid="author-email"]').textContent().catch(() => null);
    check('B3 popover shows e-mail', email === a.email, `got=${JSON.stringify(email)} want=${JSON.stringify(a.email)}`);
    if (a.clientIp) {
      const ip = await card.locator('[data-testid="author-ip"]').textContent().catch(() => null);
      check('B3 popover shows IP', ip === a.clientIp, `got=${JSON.stringify(ip)} want=${JSON.stringify(a.clientIp)}`);
    }
    const home = card.locator('[data-testid="author-home"]');
    if ((await home.count()) > 0) {
      check('B3 popover home link href is the stored URL', (await home.getAttribute('href')) === a.homePage, `href=${await home.getAttribute('href')} want=${a.homePage}`);
      check('B3 home link target is safe', (await home.getAttribute('rel') || '').includes('noopener'), `rel=${await home.getAttribute('rel')}`);
    } else {
      check('B3 popover home link present', false, 'author-home not found');
    }
  }
}

check('B3 no uncaught page errors', pageErrors.length === 0, `errors=${JSON.stringify(pageErrors.slice(0, 3))}`);
check('B no alert() dialog fired at all', dialogs.length === 0, `dialogs=${JSON.stringify(dialogs)}`);

await browser.close();
console.log(`\nRESULT pass=${pass} fail=${fail}`);
process.exit(fail ? 1 : 0);
