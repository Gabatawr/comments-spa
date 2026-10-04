/**
 * Functional DOM harness for the «Комментарии» SPA.
 *
 * Loads the real index.html and boots the real ES modules (src/Frontend/js/*.js) inside jsdom,
 * with fetch pointed at a live server and a fake WebSocket so the WS path can be driven.
 *
 * Usage:  BASE=http://127.0.0.1:5080 node dom-harness.mjs
 * Exits non-zero if any check fails.
 */
import { JSDOM } from 'jsdom';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const BASE = (process.env.BASE || 'http://127.0.0.1:5080').replace(/\/$/, '');
const FRONTEND = path.resolve(process.cwd(), '..', '..', 'src', 'Frontend');

let pass = 0, fail = 0;
const failures = [];
function check(name, cond, detail = '') {
  if (cond) { pass++; console.log(`  PASS  ${name}`); }
  else { fail++; failures.push(`${name} :: ${detail}`); console.log(`  FAIL  ${name} :: ${detail}`); }
}
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
async function waitFor(fn, timeout = 8000, step = 100) {
  const start = Date.now();
  for (;;) {
    try { if (fn()) return true; } catch { /* keep polling */ }
    if (Date.now() - start > timeout) return false;
    await wait(step);
  }
}

const NativeFormData = globalThis.FormData;
const NativeBlob = globalThis.Blob;
const NativeFetch = globalThis.fetch;

// ------------------------------------------------------------------ jsdom setup
const dom = await JSDOM.fromURL(`${BASE}/`, {
  runScripts: 'outside-only',
  pretendToBeVisual: true,
});
const { window } = dom;

window.scrollTo = () => {};
window.HTMLElement.prototype.scrollIntoView = () => {};

const openedSockets = [];
class FakeWebSocket {
  static CONNECTING = 0; static OPEN = 1; static CLOSING = 2; static CLOSED = 3;
  constructor(url) {
    this.url = url;
    this.readyState = 1;
    this.onopen = this.onmessage = this.onerror = this.onclose = null;
    openedSockets.push(this);
    setTimeout(() => { if (this.onopen) this.onopen(); }, 0);
  }
  send() {}
  close() { this.readyState = 3; if (this.onclose) this.onclose(); }
}

global.window = window;
global.document = window.document;
global.location = window.location;
try {
  Object.defineProperty(globalThis, 'navigator', { value: window.navigator, configurable: true, writable: true });
} catch { /* Node's navigator getter is read-only; modules under test do not need it */ }
global.HTMLElement = window.HTMLElement;
global.Element = window.Element;
global.Node = window.Node;
global.Event = window.Event;
global.CustomEvent = window.CustomEvent;
global.FormData = window.FormData;
global.File = window.File;
global.Blob = window.Blob;
global.WebSocket = FakeWebSocket;
window.WebSocket = FakeWebSocket;
global.requestAnimationFrame = window.requestAnimationFrame?.bind(window) || ((cb) => setTimeout(() => cb(Date.now()), 0));
global.cancelAnimationFrame = window.cancelAnimationFrame?.bind(window) || clearTimeout;

const recorded = [];
global.fetch = window.fetch = (input, init) => {
  let url = input;
  if (typeof input === 'string' && input.startsWith('/')) url = BASE + input;
  recorded.push(String(url));
  return NativeFetch(url, init);
};

process.on('unhandledRejection', (e) => console.log('  (unhandled rejection)', e?.message || e));

// ------------------------------------------------------------------ seed data
// Enough roots for pagination (>25) plus one reply thread, created via curl so we do not
// depend on Node's FormData/undici interop.
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
const RUNTIME = path.resolve(process.cwd(), '..', '.runtime');
fs.mkdirSync(RUNTIME, { recursive: true });

function curlJson(args) {
  return JSON.parse(execFileSync('curl', ['-sS', ...args], { encoding: 'utf8' }));
}
function solveCaptcha() {
  const captcha = curlJson([`${BASE}/api/captcha`]);
  return curlJson([`${BASE}/api/dev/captcha/${captcha.captchaId}`]);
}
function createComment(userName, text, parentId, extraCurlArgs = []) {
  const cap = solveCaptcha();
  const args = ['-sS', '-X', 'POST', `${BASE}/api/comments`,
    '--form-string', `userName=${userName}`,
    '--form-string', `email=${userName.toLowerCase()}@example.com`,
    '--form-string', `text=${text}`,
    '--form-string', `captchaId=${cap.captchaId}`,
    '--form-string', `captchaAnswer=${cap.code}`];
  if (parentId != null) args.push('--form-string', `parentId=${parentId}`);
  args.push(...extraCurlArgs);
  return curlJson(args);
}

console.log(`Seeding harness data at ${BASE} ...`);
const SEED = `Seed${Date.now().toString().slice(-7)}`;
for (let i = 0; i < 26; i++) {
  createComment(`${SEED}R${String(i).padStart(2, '0')}`, `seed comment ${i}`);
}
const thread = createComment(`${SEED}Thread`, 'thread root <strong>bold</strong>');
createComment(`${SEED}Rep1`, 'reply one', thread.comment.id);
createComment(`${SEED}Rep2`, 'reply two', thread.comment.id);
console.log(`Seeded 26 roots + 1 thread (${thread.comment.id}) with 2 replies.`);

// ------------------------------------------------------------------ boot the SPA
console.log(`DOM harness against ${BASE}\n== boot ==`);
try {
  await import(pathToFileURL(path.join(FRONTEND, 'js', 'app.js')).href);
} catch (error) {
  console.log('  boot threw:', error);
}
const booted = await waitFor(() => Boolean(window.__commentsApp), 5000);
check('app.js booted and exposed window.__commentsApp', booted, 'boot did not finish');

// ------------------------------------------------------------------ health + table
console.log('\n== health / list ==');
await wait(500);
const healthText = (document.getElementById('health-badge')?.textContent || '').trim();
check('health badge reflects /api/health', /ok/i.test(healthText), `badge="${healthText}"`);

const rowsLoaded = await waitFor(() => document.querySelectorAll('#comments-tbody tr').length > 0, 8000);
const rowCount = document.querySelectorAll('#comments-tbody tr').length;
check('root comments table rendered from GET /api/comments', rowsLoaded && rowCount > 0, `rows=${rowCount}`);
check('table shows at most 25 rows (default page size)', rowCount <= 25, `rows=${rowCount}`);

const pager = document.getElementById('pagination');
const pageButtons = pager ? pager.querySelectorAll('button[data-page]').length : 0;
check('pagination control rendered with page numbers', pageButtons > 1, `pageButtons=${pageButtons}`);

// ------------------------------------------------------------------ sorting
console.log('\n== sortable headers ==');
const thUser = document.querySelector('th[data-sort="userName"]');
const thDate = document.querySelector('th[data-sort="createdAt"]');
check('User Name header is sortable', Boolean(thUser));
check('Date header is sortable', Boolean(thDate));

thUser?.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
await wait(600);
const userAsc = thUser?.getAttribute('aria-sort');
const ascReq = recorded.some((u) => u.includes('sortBy=userName') && u.includes('sortDir=asc'));
check('first click on User Name sorts ascending (aria-sort + request)', userAsc === 'ascending' && ascReq, `aria-sort=${userAsc} ascReq=${ascReq}`);

thUser?.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
await wait(600);
const userDesc = thUser?.getAttribute('aria-sort');
const descReq = recorded.some((u) => u.includes('sortBy=userName') && u.includes('sortDir=desc'));
check('second click on User Name sorts descending (aria-sort + request)', userDesc === 'descending' && descReq, `aria-sort=${userDesc} descReq=${descReq}`);

thDate?.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
await wait(600);
const dateDesc = thDate?.getAttribute('aria-sort');
check('Date header defaults to descending (LIFO)', dateDesc === 'descending', `aria-sort=${dateDesc}`);

// ------------------------------------------------------------------ cascade
console.log('\n== cascade render ==');
let toggle = document.querySelector('#comments-tbody button[data-action="toggle"]');
if (!toggle) {
  // page 1 may not contain a reply thread: go to the newest page / refresh
  await wait(500);
  toggle = document.querySelector('#comments-tbody button[data-action="toggle"]');
}
if (toggle) {
  toggle.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
  await wait(400);
  const cascade = document.querySelector('#comments-tbody .cascade');
  const children = cascade?.querySelector('.comment-children');
  const card = cascade?.querySelector('.comment-card, .comment-body');
  check('reply thread expands into a nested cascade', Boolean(cascade), 'no .cascade after toggle');
  check('cascade has indented children container', Boolean(children), 'no .comment-children');
  check('cascade renders nested comment body', Boolean(card), 'no comment body in cascade');
} else {
  check('reply thread expand control present on page 1', false, 'no [data-action=toggle] button found');
}

// «Ответить» must prefill the hidden parentId and show the reply banner.
const replyButton = document.querySelector('#comments-tbody button[data-action="reply"]');
if (replyButton) {
  replyButton.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
  await wait(150);
  const parentField = document.getElementById('parentId');
  const banner = document.getElementById('reply-banner');
  check('«Ответить» prefills parentId and shows the reply banner',
    Boolean(parentField?.value) && banner && !banner.hidden,
    `parentId="${parentField?.value}" bannerHidden=${banner?.hidden}`);
} else {
  check('reply button present on page 1', false, 'no [data-action=reply] button found');
}

// Pagination navigation requests the selected page.
const page2Button = document.querySelector('#pagination button[data-page="2"]');
if (page2Button) {
  page2Button.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
  const page2Requested = await waitFor(() => recorded.some((u) => /[?&]page=2(&|$)/.test(u)), 5000);
  check('pagination page button requests page=2', page2Requested, 'no page=2 request recorded');
} else {
  check('page 2 pagination button present', false, 'no [data-page="2"] button');
}

// ------------------------------------------------------------------ toolbar
console.log('\n== tag toolbar ==');
const textarea = document.getElementById('text');
textarea.value = 'hello world';
textarea.focus();
textarea.setSelectionRange(6, 11); // "world"
document.querySelector('button[data-tag="strong"]')?.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
check('toolbar [strong] wraps the selection', textarea.value.includes('<strong>world</strong>'), `value="${textarea.value}"`);

textarea.value = 'code here';
textarea.setSelectionRange(0, 4);
document.querySelector('button[data-tag="code"]')?.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
check('toolbar [code] wraps the selection', textarea.value.includes('<code>code</code>'), `value="${textarea.value}"`);

textarea.value = 'italic';
textarea.setSelectionRange(0, 6);
document.querySelector('button[data-tag="i"]')?.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
check('toolbar [i] wraps the selection', textarea.value.includes('<i>italic</i>'), `value="${textarea.value}"`);

// [a] uses window.prompt; stub it
window.prompt = (msg, def) => (String(msg).toLowerCase().includes('title') ? 'T' : 'https://example.com');
textarea.value = 'link';
textarea.setSelectionRange(0, 4);
document.querySelector('button[data-tag="a"]')?.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
check('toolbar [a] inserts href+title', /<a href="https:\/\/example\.com" title="T">link<\/a>/.test(textarea.value), `value="${textarea.value}"`);

// ------------------------------------------------------------------ client validation
console.log('\n== client-side validation ==');
document.getElementById('userName').value = '';
document.getElementById('email').value = 'not-an-email';
document.getElementById('text').value = '';
document.getElementById('captchaAnswer').value = '';
document.getElementById('captchaId').value = '';
document.getElementById('comment-form').dispatchEvent(new window.Event('submit', { bubbles: true, cancelable: true }));
await wait(200);
const userNameErr = document.querySelector('[data-error-for="userName"]');
const emailErr = document.querySelector('[data-error-for="email"]');
const textErr = document.querySelector('[data-error-for="text"]');
const visible = (el) => el && !el.hidden && (el.textContent || '').trim().length > 0;
check('empty User Name shows an inline error', visible(userNameErr), `text="${userNameErr?.textContent}"`);
check('invalid E-mail shows an inline error', visible(emailErr), `text="${emailErr?.textContent}"`);
check('empty Text shows an inline error', visible(textErr), `text="${textErr?.textContent}"`);

// ------------------------------------------------------------------ preview (AJAX)
console.log('\n== AJAX preview ==');
const beforeHref = window.location.href;
document.getElementById('text').value = '<strong>hi</strong> <i>there</i>';
document.getElementById('preview-btn').dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
const previewOk = await waitFor(() => /<strong>hi<\/strong>/.test(document.getElementById('preview-panel')?.innerHTML || ''), 5000);
check('preview panel renders sanitized HTML without navigation', previewOk && window.location.href === beforeHref,
  `panel="${document.getElementById('preview-panel')?.innerHTML}" hrefChanged=${window.location.href !== beforeHref}`);

document.getElementById('text').value = '<script>alert(1)</script>';
document.getElementById('preview-btn').dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
await wait(600);
const panelHtml = document.getElementById('preview-panel')?.innerHTML || '';
check('preview never injects <script>', !/<script/i.test(panelHtml), `panel="${panelHtml}"`);

// ------------------------------------------------------------------ captcha refresh
console.log('\n== CAPTCHA refresh ==');
const captchaIdBefore = document.getElementById('captchaId').value;
document.getElementById('captcha-reload').dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
const captchaOk = await waitFor(() => document.getElementById('captchaId').value && document.getElementById('captchaId').value !== captchaIdBefore, 5000);
const imgSrc = document.getElementById('captcha-image')?.src || '';
check('refresh loads a new CAPTCHA id and PNG image', captchaOk && imgSrc.startsWith('data:image/png;base64,'), `idChanged=${captchaOk} src=${imgSrc.slice(0, 30)}`);

// ------------------------------------------------------------------ lightbox
console.log('\n== lightbox / TXT modal ==');
const attachments = await import(pathToFileURL(path.join(FRONTEND, 'js', 'attachments.js')).href);
attachments.openLightbox('data:image/png;base64,iVBORw0KGgo=', 'demo.png');
await wait(50);
const lightbox = document.getElementById('lightbox');
check('lightbox opens as an overlay dialog', lightbox.classList.contains('is-open') && lightbox.getAttribute('aria-hidden') === 'false',
  `class="${lightbox.className}" aria-hidden=${lightbox.getAttribute('aria-hidden')}`);
attachments.closeLightbox();
check('lightbox closes', !lightbox.classList.contains('is-open'), `class="${lightbox.className}"`);

// create a TXT attachment via curl, then open it in the modal
fs.writeFileSync(path.join(RUNTIME, 'txtmodal.txt'), 'HARNESS-TXT-CONTENT');
const created = createComment(`HarnessTxt${Date.now() % 100000}`, 'txt modal check', null,
  ['-F', `attachment=@${path.join(RUNTIME, 'txtmodal.txt')};type=text/plain;filename=note.txt`]);
const attId = created?.comment?.attachment?.id;
check('TXT attachment created for modal test', Number.isInteger(attId), `attachment id=${attId}`);
if (attId) {
  await attachments.openTextModal(`${BASE}/api/attachments/${attId}`, 'note.txt');
  const modal = document.getElementById('text-modal');
  const modalBody = document.getElementById('text-modal-body');
  const modalOk = await waitFor(() => (modalBody?.textContent || '').includes('HARNESS-TXT-CONTENT'), 5000);
  check('TXT modal opens and renders file content as text', modalOk && modal.classList.contains('is-open') && modalBody.childElementCount === 0,
    `class="${modal.className}" body="${(modalBody?.textContent || '').slice(0, 40)}" children=${modalBody?.childElementCount}`);
  attachments.closeTextModal();
}

// ------------------------------------------------------------------ WebSocket toast
console.log('\n== WebSocket toast ==');
const sock = openedSockets[0];
check('WebSocket client connected to /ws', Boolean(sock) && /\/ws$/.test(sock.url), `sockets=${openedSockets.length}`);
if (sock && sock.onmessage) {
  const toastHost = document.getElementById('toasts');
  const beforeToasts = toastHost.querySelectorAll('.toast').length;
  sock.onmessage({ data: JSON.stringify({ type: 'comment.created', comment: { id: 999999, userName: 'WsHarness', text: 'hi', replies: [] } }) });
  const toastOk = await waitFor(() => toastHost.querySelectorAll('.toast').length > beforeToasts
    && /WsHarness/.test(toastHost.textContent || ''), 4000);
  check('comment.created triggers a toast in the UI', toastOk, `toasts="${(toastHost.textContent || '').slice(0, 80)}"`);
}

// ------------------------------------------------------------------ summary
console.log(`\n================ SUMMARY ================`);
console.log(`PASS: ${pass}  FAIL: ${fail}`);
if (fail > 0) {
  console.log('Failures:');
  for (const f of failures) console.log(`  - ${f}`);
  process.exit(1);
}
process.exit(0);
