#!/usr/bin/env node
/**
 * Real-browser functional check of the Angular SPA served by nginx.
 *
 * Replaces the old jsdom DOM-harness: launches headless Chromium through
 * Playwright, drives the actual built Angular app at BASE (default
 * http://localhost:8080), and asserts the v2 UI requirements from
 * docs/target-middle-plus.md §2.6.
 *
 * Usage:
 *   BASE=http://localhost:8080 node angular-check.mjs
 *   PLAYWRIGHT_BROWSERS_PATH=<repo>/.playwright node angular-check.mjs
 *
 * Prints PASS/FAIL counts and exits non-zero on any failure. Set
 * QA_ARTIFACTS=<dir> to keep screenshots of failures.
 *
 * Requires: `playwright` + a Chromium browser (see run-browser-check.sh).
 * Seed data is created through the live API (curl) so the checks exercise the
 * same data the UI shows.
 */
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';

const BASE = (process.env.BASE || 'http://localhost:8080').replace(/\/$/, '');
const ARTIFACTS = process.env.QA_ARTIFACTS || path.join(os.tmpdir(), 'comments-ui-artifacts');
fs.mkdirSync(ARTIFACTS, { recursive: true });

let pass = 0, fail = 0, skip = 0;
const failures = [];
const warnings = [];
const check = (name, cond, detail = '') => {
  if (cond) { pass++; console.log(`  PASS  ${name}`); }
  else { fail++; failures.push(`${name} :: ${detail}`); console.log(`  FAIL  ${name} :: ${detail}`); }
};
const skipCheck = (name, why) => { skip++; console.log(`  SKIP  ${name} :: ${why}`); };
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

// ------------------------------------------------------------------ API seeding
const REDIS_CONTAINER = process.env.REDIS_CONTAINER || 'comments-spa-redis';
let peekMode = null;

function curlJson(args) {
  const out = execFileSync('curl', ['-sS', '--max-time', '30', ...args], { encoding: 'utf8' });
  return JSON.parse(out);
}
function extractCode(raw) {
  const cands = [];
  const walk = (x) => {
    if (typeof x === 'string') cands.push(x);
    else if (Array.isArray(x)) x.forEach(walk);
    else if (x && typeof x === 'object') Object.values(x).forEach(walk);
  };
  try { walk(JSON.parse(raw)); } catch { cands.push(raw); }
  const six = cands.map((s) => s.trim()).find((s) => /^[A-Za-z0-9]{6}$/.test(s));
  if (six) return six;
  return cands.map((s) => s.trim()).find((s) => /^[A-Za-z0-9]{4,12}$/.test(s)) || null;
}
function solveCaptcha() {
  const c = curlJson([`${BASE}/api/captcha`]);
  if (peekMode === null) {
    try {
      const peek = curlJson([`${BASE}/api/dev/captcha/${c.captchaId}`]);
      if (peek && peek.code) peekMode = 'dev';
    } catch { /* dev endpoint disabled */ }
    if (peekMode === null) peekMode = 'redis';
    console.log(`  captcha peek mode: ${peekMode}${peekMode === 'redis' ? ` (redis container ${REDIS_CONTAINER})` : ''}`);
  }
  if (peekMode === 'dev') {
    const peek = curlJson([`${BASE}/api/dev/captcha/${c.captchaId}`]);
    if (peek && peek.code) return { captchaId: c.captchaId, code: peek.code };
  }
  let raw = '';
  try {
    raw = execFileSync('docker', ['exec', REDIS_CONTAINER, 'redis-cli', '--raw', 'GET', `captcha:${c.captchaId}`], { encoding: 'utf8' }).trim();
  } catch (e) {
    throw new Error(`captcha: dev peek disabled and docker/redis unavailable: ${e.message}`);
  }
  const code = extractCode(raw);
  if (!code) throw new Error(`captcha: no code for ${c.captchaId} (redis raw="${raw.slice(0, 120)}")`);
  return { captchaId: c.captchaId, code };
}
function createComment(userName, text, { parentId = null, extra = [] } = {}) {
  const cap = solveCaptcha();
  const args = ['-X', 'POST', `${BASE}/api/comments`,
    '--form-string', `userName=${userName}`,
    '--form-string', `email=${userName.toLowerCase()}@example.com`,
    '--form-string', `text=${text}`,
    '--form-string', `captchaId=${cap.captchaId}`,
    '--form-string', `captchaAnswer=${cap.code}`];
  if (parentId != null) args.push('--form-string', `parentId=${parentId}`);
  args.push(...extra);
  return curlJson(args);
}

console.log(`Seeding UI data via ${BASE} ...`);
const TAG = `Ui${Date.now().toString().slice(-7)}`;
// Bulk roots so pagination (25/page) is exercised.
for (let i = 0; i < 30; i++) createComment(`${TAG}Bulk${String(i).padStart(2, '0')}`, `bulk root ${i}`);
// A reply thread: root + 2 direct replies + 1 nested reply.
const root = createComment(`${TAG}RootThread`, 'cascade <strong>root</strong>');
const r1 = createComment(`${TAG}ReplyOne`, 'first reply', { parentId: root.comment.id });
createComment(`${TAG}ReplyTwo`, 'second reply', { parentId: root.comment.id });
createComment(`${TAG}NestedReply`, 'nested reply', { parentId: r1.comment.id });
// Sort fixtures last so they are newest → guaranteed visible on page 1 under LIFO.
const SORT_USERS = [`${TAG}Alpha`, `${TAG}Bravo`, `${TAG}Charlie`];
for (const u of SORT_USERS) { createComment(u, `sort fixture ${u}`); await wait(1100); }
console.log(`  seeded 30 roots + thread + 3 sort users (tag ${TAG})`);

// ------------------------------------------------------------------- browser
const { chromium } = await import('playwright');
const browser = await chromium.launch({ headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
const page = await context.newPage();

// The [a] toolbar button uses window.prompt() for the href; answer it so the
// link-insertion path is exercised instead of being auto-dismissed.
page.on('dialog', (d) => d.accept('https://example.com'));

const pageErrors = [];
const badResponses = [];
page.on('pageerror', (e) => pageErrors.push(String(e.message || e)));
page.on('response', (r) => {
  const u = r.url();
  if (u.startsWith(BASE) && r.status() >= 500) badResponses.push(`${r.status()} ${u}`);
});

async function shot(name) {
  try { await page.screenshot({ path: path.join(ARTIFACTS, `${name}.png`), fullPage: false }); } catch { /* ignore */ }
}

// Wait for a locator among candidates to become visible; returns the locator or null.
async function firstVisible(candidates, timeout = 4000) {
  const deadline = Date.now() + timeout;
  for (;;) {
    for (const sel of candidates) {
      const loc = page.locator(sel).first();
      try { if (await loc.count() > 0 && await loc.isVisible()) return loc; } catch { /* keep trying */ }
    }
    if (Date.now() > deadline) return null;
    await wait(150);
  }
}
const SEL = {
  table: ['table', '[data-testid="comments-table"]', 'app-comment-table table'],
  row: ['[data-testid="comment-row"]', 'table tbody tr', 'tbody tr', '.comment-item', 'app-comment'],
  textarea: ['[data-testid="textarea"]', 'textarea[name="text"]', 'form textarea', 'textarea'],
  previewOut: ['[data-testid="preview-output"]', '.preview', '.preview-content', '[class*="preview"]'],
  captcha: ['[data-testid="captcha-image"]', 'img[src^="data:image/png"]'],
  cascade: ['[data-testid="reply-cascade"]', '.replies', '[class*="replies"]', '[class*="cascade"]', '[class*="children"]'],
};

console.log(`Opening ${BASE}/ in Chromium ...`);
let bootOk = false;
try {
  await page.goto(`${BASE}/`, { waitUntil: 'domcontentloaded', timeout: 45000 });
  await page.evaluate(() => { window.__qaNoReload = 1; });
  const rootEl = await firstVisible(['app-root', '[data-testid="app-root"]'], 20000);
  bootOk = !!rootEl;
  check('Angular app boots (app-root present)', bootOk, 'app-root not found');
  await page.waitForLoadState('networkidle', { timeout: 20000 }).catch(() => {});
} catch (e) {
  check('Angular app boots', false, String(e.message || e));
  await shot('boot-failure');
}
check('no uncaught page errors during boot', pageErrors.length === 0, pageErrors.join(' | ').slice(0, 400));

// 1. table renders
let tableOk = false;
try {
  await firstVisible(SEL.table, 10000);
  const rows = await firstVisible(SEL.row, 10000);
  const rowSel = (await page.locator('[data-testid="comment-row"]').count()) > 0 ? '[data-testid="comment-row"]' : 'tbody tr';
  const count = rows ? await page.locator(rowSel).count() : 0;
  tableOk = count > 0;
  check('root comments table renders rows', tableOk, `rows=${count}`);
  if (count > 25) warnings.push(`first page shows ${count} rows (>25) — check pagination page size`);
} catch (e) { check('table renders rows', false, String(e.message || e)); await shot('table'); }

// 2. sorting toggles both directions
async function clickSortHeader(testId) {
  const el = page.locator(`[data-testid="${testId}"]`).first();
  if (!(await el.count())) return null;
  await el.click();
  await wait(1200);
  return el;
}
// Put the list back into the default LIFO view (createdAt desc, page 1) without
// toggling the sort direction by mistake — only click if not already descending.
async function ensurePage1DefaultSort() {
  const createdHeader = page.locator('[data-testid="sort-created-at"]').first();
  if (await createdHeader.count()) {
    const aria = await createdHeader.getAttribute('aria-sort');
    if (aria !== 'descending') { await createdHeader.click(); await wait(1000); }
  }
  const p1 = page.locator('[data-testid="page-num-1"], [data-testid="page-prev"]').first();
  if ((await p1.count()) && !(await p1.isDisabled())) { await p1.click().catch(() => {}); await wait(800); }
}
async function visibleUserOrder() {
  const rows = page.locator('[data-testid="comment-row"]');
  const n = Math.min(await rows.count(), 25);
  const names = [];
  for (let i = 0; i < n; i++) {
    // Строка таблицы — это карточка целиком, поэтому имя берём строго по его
    // классу: иначе первым совпадением окажутся инициалы в аватаре.
    const el = rows.nth(i).locator('.comment-username').first();
    const t = (await el.innerText().catch(() => '')).replace(/\s+/g, ' ').trim();
    names.push(t || `row-${i}`);
  }
  return names;
}
try {
  const header = await clickSortHeader('sort-user-name');
  if (!header) {
    skipCheck('sort by User Name toggles asc/desc', 'sort-user-name header not found');
  } else {
    const asc = await visibleUserOrder();
    const ariaAsc = await header.getAttribute('aria-sort');
    await header.click();
    await wait(1200);
    const desc = await visibleUserOrder();
    const ariaDesc = await header.getAttribute('aria-sort');
    // Position-independent: the visible page must be monotonic in the chosen
    // direction (case-insensitive, matching the server's lower() ordering).
    const lower = (a) => a.map((s) => s.toLowerCase());
    const ascNames = lower(asc), descNames = lower(desc);
    const nonDecreasing = ascNames.every((v, i) => i === 0 || ascNames[i - 1] <= v);
    const nonIncreasing = descNames.every((v, i) => i === 0 || descNames[i - 1] >= v);
    const distinct = new Set(ascNames).size > 1 && new Set(descNames).size > 1;
    check('sorting by User Name: page is in ascending order', nonDecreasing && distinct, `order=${asc.slice(0, 8).join(',')}`);
    check('sorting by User Name: second click reverses to descending', nonIncreasing && distinct, `order=${desc.slice(0, 8).join(',')}`);
    check('aria-sort reflects ascending then descending', ariaAsc === 'ascending' && ariaDesc === 'descending', `asc=${ariaAsc} desc=${ariaDesc}`);
  }
} catch (e) { check('sorting toggles', false, String(e.message || e)); await shot('sort'); }

// 3. pagination
try {
  const rows = page.locator('[data-testid="comment-row"]');
  const n = await rows.count();
  check('pagination page size <= 25', n <= 25, `rows=${n}`);
  const pager = await firstVisible(['[data-testid="pagination"]', '.pagination'], 5000);
  if (!pager) {
    skipCheck('pagination navigation', 'no pagination control found');
  } else {
    const pageAttr = await pager.getAttribute('data-page');
    const sizeAttr = await pager.getAttribute('data-page-size');
    check('pagination advertises pageSize=25', String(sizeAttr) === '25', `data-page-size=${sizeAttr}`);
    const before = await rows.first().innerText().catch(() => '');
    const next = page.locator('[data-testid="page-next"]').first();
    if (await next.count() && !(await next.isDisabled())) {
      await next.click();
      await wait(1400);
      const after = await rows.first().innerText().catch(() => '');
      const pageAttr2 = await pager.getAttribute('data-page');
      check('pagination navigates to next page', before !== after && String(pageAttr2) !== String(pageAttr), `page ${pageAttr} -> ${pageAttr2}; before="${before.slice(0, 50)}" after="${after.slice(0, 50)}"`);
    } else {
      skipCheck('pagination navigation', 'page-next disabled (only one page)');
    }
  }
} catch (e) { check('pagination', false, String(e.message || e)); await shot('pagination'); }

// 4. root row is the comment card itself; replies are expanded by default
try {
  await page.evaluate(() => window.scrollTo(0, 0));
  // Reset to default LIFO so the newest thread root (our fixture) is on page 1.
  await ensurePage1DefaultSort();
  const rootRow = page.locator('[data-testid="comment-row"]').filter({ hasText: `${TAG}RootThread` }).first();
  if (!(await rootRow.count())) {
    skipCheck('reply cascade renders', 'root thread row not visible on page 1');
  } else {
    // Тумблера «свернуть» больше нет: строка таблицы И ЕСТЬ карточка корня,
    // вложенные ответы раскрыты внутри неё сразу.
    const rootCard = rootRow.locator('[data-testid="comment-card"][data-depth="0"]').first();
    check('root row renders the comment card itself', (await rootCard.count()) > 0, 'no comment-card depth=0 inside the row');
    check('root row has no collapse toggle', (await rootRow.locator('[data-testid="reply-toggle"]').count()) === 0, 'reply-toggle still present');
    const cascade = rootRow.locator('[data-testid="reply-cascade"]').first();
    check('reply cascade container renders', (await cascade.count()) > 0, 'no reply-cascade inside the row');
    const depth1 = rootRow.locator('[data-testid="comment-card"][data-depth="1"]').filter({ hasText: `${TAG}ReplyOne` }).first();
    check('nested reply (depth 1) renders expanded by default', (await depth1.count()) > 0, 'no comment-card depth=1 with ReplyOne');
    const children = rootRow.locator('[data-testid="reply-children"]').first();
    if (await children.count()) {
      const rootBox = await rootCard.boundingBox();
      const childBox = await children.boundingBox();
      if (rootBox && childBox) check('cascade is indented relative to root', childBox.x > rootBox.x, `root.x=${rootBox.x} children.x=${childBox.x}`);
      if (rootBox && childBox) {
        const indent = childBox.x - rootBox.x;
        check('nesting indent is 32px per level', Math.abs(indent - 32) <= 1, `indent=${indent}`);
      }
      const rail = await children.evaluate((el) => {
        const cs = getComputedStyle(el);
        return { left: cs.borderLeftWidth, right: cs.borderRightWidth };
      });
      check('no nesting rail (sample has none)', parseFloat(rail.left) === 0 && parseFloat(rail.right) === 0, JSON.stringify(rail));
    } else {
      skipCheck('cascade indent/quote-bar', 'no reply-children container');
    }
  }
} catch (e) { check('reply cascade', false, String(e.message || e)); await shot('cascade'); }

// 5. toolbar inserts tags
try {
  const ta = await firstVisible(SEL.textarea, 6000);
  if (!ta) {
    skipCheck('toolbar inserts tags', 'no textarea found');
  } else {
    await ta.click();
    await ta.fill('');
    await ta.type('toolbar sample text');
    await ta.evaluate((el) => { el.focus(); el.setSelectionRange(0, 7); });
    const buttons = ['[data-testid="toolbar-i"]', '[data-testid="toolbar-strong"]', '[data-testid="toolbar-code"]', '[data-testid="toolbar-a"]'];
    let clicked = 0;
    for (const b of buttons) {
      const loc = page.locator(b).first();
      if (await loc.count()) { await loc.click(); await wait(150); clicked++; }
    }
    if (clicked === 0) {
      // fallback: buttons with tag labels
      for (const label of ['i', 'strong', 'code', 'a']) {
        const loc = page.locator(`button:has-text("${label}")`).first();
        if (await loc.count()) { await loc.click(); await wait(150); clicked++; }
      }
    }
    const val = await ta.inputValue();
    if (clicked === 0) {
      skipCheck('toolbar inserts tags', 'no toolbar buttons found');
    } else {
      check('toolbar buttons modify the text field', val !== 'toolbar sample text' && val.length > 'toolbar sample text'.length, `value="${val}"`);
      check('toolbar inserts a tag marker', /<(i|em|strong|code|a)\b|\[(i|strong|code|a)\]/i.test(val) || /<a\s+href/i.test(val), `value="${val}"`);
    }
  }
} catch (e) { check('toolbar', false, String(e.message || e)); await shot('toolbar'); }

// 6. preview works without navigation
try {
  const ta = await firstVisible(SEL.textarea, 5000);
  if (!ta) {
    skipCheck('AJAX preview', 'no textarea');
  } else {
    const beforeUrl = page.url();
    await ta.fill('<strong>preview</strong> <i>ok</i>');
    const btn = await firstVisible([
      '[data-testid="preview-button"]',
      'button:has-text("Preview")',
      'button:has-text("Предпросмотр")',
      'button:has-text("Просмотр")',
      'button[type="button"]:has-text("Preview")',
    ], 5000);
    if (!btn) {
      skipCheck('AJAX preview', 'no preview button');
    } else {
      await btn.click();
      await wait(1200);
      const out = await firstVisible(SEL.previewOut, 5000);
      const urlUnchanged = page.url() === beforeUrl;
      const noReload = await page.evaluate(() => window.__qaNoReload === 1).catch(() => false);
      check('preview does not navigate/reload', urlUnchanged && noReload, `url=${page.url()} marker=${noReload}`);
      if (out) {
        const html = await out.innerHTML().catch(() => '');
        check('preview renders sanitized HTML', /<strong>\s*preview\s*<\/strong>|<strong>preview<\/strong>/i.test(html), `html=${html.slice(0, 200)}`);
        check('preview output contains no <script>', !/<script/i.test(html), html.slice(0, 120));
      } else {
        skipCheck('preview renders sanitized HTML', 'no preview output container');
      }
    }
  }
} catch (e) { check('preview', false, String(e.message || e)); await shot('preview'); }

// 7. captcha image loads
try {
  const img = await firstVisible(SEL.captcha, 6000);
  if (!img) {
    skipCheck('captcha image loads', 'no data:image/png captcha element');
  } else {
    let nat = await img.evaluate((el) => (el.tagName === 'IMG' ? { w: el.naturalWidth, h: el.naturalHeight } : { w: el.clientWidth, h: el.clientHeight }));
    for (let i = 0; i < 20 && (!nat.w || !nat.h); i++) { await wait(250); nat = await img.evaluate((el) => (el.tagName === 'IMG' ? { w: el.naturalWidth, h: el.naturalHeight } : { w: el.clientWidth, h: el.clientHeight })); }
    check('captcha image renders with pixels', nat.w > 0 && nat.h > 0, JSON.stringify(nat));
  }
} catch (e) { check('captcha image', false, String(e.message || e)); await shot('captcha'); }

// 8. WebSocket live update (comment created by another client appears without reload)
try {
  const beforeReloadMarker = await page.evaluate(() => window.__qaNoReload === 1).catch(() => false);
  // Return to default LIFO page 1 so the live root is both toasted and prepended.
  await ensurePage1DefaultSort();
  const wsUser = `${TAG}Live${Date.now().toString().slice(-4)}`;
  createComment(wsUser, 'live websocket update');
  const toast = page.locator('[data-testid="ws-toast"]').first();
  const liveRow = page.locator('[data-testid="comment-row"]').filter({ hasText: wsUser }).first();
  let toastSeen = false, rowSeen = false;
  for (let i = 0; i < 40; i++) {
    if (!toastSeen && (await toast.count()) && (await toast.innerText().catch(() => '')).includes(wsUser)) toastSeen = true;
    if (!rowSeen && (await liveRow.count()) && (await liveRow.isVisible().catch(() => false))) rowSeen = true;
    if (toastSeen && rowSeen) break;
    await wait(250);
  }
  const markerStill = await page.evaluate(() => window.__qaNoReload === 1).catch(() => false);
  check('WebSocket live update raises a ws-toast with the new comment', toastSeen, 'no ws-toast containing the new userName');
  check('WebSocket live update inserts the root row without reload', rowSeen && beforeReloadMarker && markerStill, `row=${rowSeen} noReload=${markerStill}`);
  if (!toastSeen) await shot('websocket');
} catch (e) { check('WebSocket live update', false, String(e.message || e)); }

// 9. no 5xx during the session
check('no API 5xx responses while using the UI', badResponses.length === 0, badResponses.join(' | '));
check('no uncaught page errors during the session', pageErrors.length === 0, pageErrors.join(' | ').slice(0, 400));

await shot('final');
await browser.close();

console.log(`\n================ UI SUMMARY ================`);
console.log(`PASS: ${pass}  FAIL: ${fail}  SKIP: ${skip}`);
console.log(`Artifacts: ${ARTIFACTS}`);
if (warnings.length) { console.log('Warnings:'); warnings.forEach((w) => console.log(`  - ${w}`)); }
if (failures.length) {
  console.log('Failures:');
  failures.forEach((f) => console.log(`  - ${f}`));
  process.exit(1);
}
process.exit(0);
