/**
 * Одноразовый снимок вёрстки: отдаёт собранный Angular статикой,
 * подменяет API фикстурами и снимает скриншоты. Не часть тестов.
 */
import { chromium } from 'playwright';

const BASE = process.env.BASE ?? 'http://127.0.0.1:8099';
const OUT = process.env.OUT ?? '/tmp/ui';

const now = Date.now();
const iso = (min) => new Date(now - min * 60000).toISOString();

const mk = (id, parentId, userName, email, homePage, text, createdAt, replies = []) => ({
  id,
  parentId,
  userName,
  email,
  homePage,
  text,
  textPlain: text.replace(/<[^>]+>/g, ''),
  createdAt,
  clientIp: `203.0.113.${id}`,
  userAgent: 'Mozilla/5.0 (X11; Linux x86_64)',
  attachment: null,
  replyCount: replies.length,
  replies,
});

// Форма близка к образцу: ~10 ответов на корень, глубина 3.
const tree = [
  mk(1, null, 'Anonym', 'anon@example.com', 'https://example.com/',
    '<p>Каждый из нас понимает очевидную вещь: семантический разбор внешних противодействий предоставляет широкие возможности.</p>' +
    '<p>Безусловно, постоянное информационно-пропагандистское обеспечение нашей деятельности предопределяет высокую востребованность позиций, занимаемых участниками в отношении поставленных задач.</p>',
    iso(90), [
      mk(2, 1, 'Rum_8', 'rum8@example.com', 'https://rum8.dev/',
        '<p>Внезапно, тщательные исследования конкурентов, которые представляют собой <strong>яркий пример</strong> континентально-европейского типа политической культуры, будут ассоциативно распределены по отраслям.</p>',
        iso(60), [
          mk(3, 2, 'Anonym', 'anon@example.com', null,
            '<p>Идейные соображения высшего порядка, а также понимание сути <i>ресурсосберегающих</i> технологий играет определяющее значение для новых принципов формирования материально-технической и кадровой базы!</p>',
            iso(40), [
              mk(4, 3, 'Rum_8', 'rum8@example.com', null,
                '<p>А ещё интерактивные прототипы являются только методом политического.</p>',
                iso(15), []),
            ]),
        ]),
    ]),
  mk(5, null, 'Guest_42', 'guest42@example.com', null,
    '<p>Противоположная точка зрения подразумевает, что <code>const x = 1;</code> и репликация в чистом виде.</p>',
    iso(200), []),
  mk(6, null, 'Nina', 'nina@example.com', 'https://nina.example/',
    '<p>Сложно сказать, почему <a href="https://example.com/" title="пример">некоторые особенности</a> призывают нас к новым свершениям.</p>',
    iso(300), []),
];

const pageDto = {
  items: tree,
  page: 1,
  pageSize: 25,
  totalItems: 6,
  totalPages: 1,
  sortBy: 'createdAt',
  sortDir: 'desc',
  nextCursor: null,
};

const PNG_1PX =
  'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==';

const browser = await chromium.launch();
const ctx = await browser.newContext({
  viewport: { width: 1000, height: 860 },
  deviceScaleFactor: 2,
});
const page = await ctx.newPage();

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
  if (u.includes('/api/captcha')) {
    return json(route, { captchaId: 'fixture', image: PNG_1PX, expiresInSeconds: 300 });
  }
  if (u.includes('/api/preview')) {
    return json(route, { valid: true, html: '<strong>ok</strong>', plain: 'ok', errors: [] });
  }
  if (/\/api\/comments\/\d+/.test(u)) return json(route, tree[0]);
  if (u.includes('/api/comments')) return json(route, pageDto);
  return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' });
});

await page.goto(BASE, { waitUntil: 'domcontentloaded' });
await page.waitForTimeout(2500);

const rows = await page.locator('[data-testid="comment-row"]').count();
console.log(`строк в таблице корней: ${rows}`);

// Раскрываем каскад первого корня — именно он сравнивается с образцом.
const toggle = page.locator('[data-testid="reply-toggle"]').first();
if (await toggle.count()) {
  await toggle.click();
  await page.waitForTimeout(600);
}

const cards = await page.locator('[data-testid="comment-card"]').count();
console.log(`карточек каскада после раскрытия: ${cards}`);

const section = page.locator('.comments-card').first();
await section.screenshot({ path: `${OUT}-cascade.png` });
await page.screenshot({ path: `${OUT}-full.png`, fullPage: true });

// Заголовки таблицы — проверить колонки дословно.
const heads = await page.locator('.comments-table thead th').allInnerTexts();
console.log(`колонки: ${heads.map((h) => h.trim()).join(' | ')}`);
await browser.close();
