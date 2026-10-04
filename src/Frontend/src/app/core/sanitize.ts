/**
 * sanitize.ts — defense-in-depth allow-list sanitizer (зеркало
 * src/Backend/.../HtmlSanitizer.cs).
 *
 * Единственное место, готовящее HTML для [innerHTML]. Белый список строго
 * `a[href,title]`, `code`, `i`, `strong`. Всё остальное либо разворачивается
 * (неизвестные безопасные теги), либо выбрасывается вместе с поддеревом.
 */

export const ALLOWED_TAGS: Readonly<Record<string, readonly string[]>> = Object.freeze({
  a: Object.freeze(['href', 'title']),
  code: Object.freeze([]),
  i: Object.freeze([]),
  strong: Object.freeze([]),
});

/** Поддеревья, содержимое которых не показываем даже как текст. */
const DROP_SUBTREE = new Set([
  'script', 'style', 'iframe', 'frame', 'frameset', 'object', 'embed', 'applet',
  'svg', 'math', 'template', 'noscript', 'link', 'meta', 'base', 'form', 'input',
  'button', 'textarea', 'select', 'option', 'audio', 'video', 'canvas', 'portal',
]);

/**
 * Блочные теги, которые не входят в allow-list, но при разворачивании должны
 * сохранить границу абзаца. Разрешённых тегов это не добавляет: в выходной
 * фрагмент попадает только `<br>` (безопасный presentational-элемент).
 */
const BLOCK_BOUNDARY_TAGS = new Set([
  'p', 'div', 'section', 'article', 'header', 'footer', 'main', 'aside', 'nav',
  'ul', 'ol', 'li', 'dl', 'dt', 'dd', 'blockquote', 'pre', 'figure', 'figcaption',
  'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'address', 'hr',
  'table', 'thead', 'tbody', 'tfoot', 'tr', 'td', 'th',
]);

const TAG_RE = /<(\/?)([a-zA-Z][a-zA-Z0-9:-]*)((?:"[^"]*"|'[^']*'|[^"'>])*?)(\/?)>/g;

/** Экранирование текста для безопасной вставки в HTML/атрибут. */
export function escapeHtml(value: unknown): string {
  return String(value == null ? '' : value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

const NAMED_ENTITIES: Readonly<Record<string, string>> = Object.freeze({
  amp: '&',
  lt: '<',
  gt: '>',
  quot: '"',
  apos: "'",
  colon: ':',
  tab: '\t',
  newline: '\n',
  nbsp: '\u00a0',
});

/** Декодирование HTML-сущностей: `&#x6a;avascript:` не должен обойти проверку схемы. */
export function decodeEntities(value: unknown): string {
  return String(value == null ? '' : value)
    .replace(/&#x([0-9a-f]+);/gi, (match, hex: string) => {
      try {
        return String.fromCodePoint(parseInt(hex, 16));
      } catch {
        return match;
      }
    })
    .replace(/&#(\d+);/g, (match, dec: string) => {
      try {
        return String.fromCodePoint(Number(dec));
      } catch {
        return match;
      }
    })
    .replace(/&([a-z][a-z0-9]+);/gi, (match, name: string) => {
      const key = name.toLowerCase();
      return Object.prototype.hasOwnProperty.call(NAMED_ENTITIES, key)
        ? NAMED_ENTITIES[key]
        : match;
    });
}

/** Проверка href — зеркало серверного HtmlSanitizer.IsSafeHref. */
export function isSafeHref(raw: unknown): boolean {
  if (typeof raw !== 'string') {
    return false;
  }
  const decoded = decodeEntities(raw);
  let normalized = '';
  for (const ch of decoded) {
    const code = ch.codePointAt(0) ?? 0;
    if (/\s/.test(ch) || code === 0x7f || (code >= 0x80 && code <= 0x9f)) {
      continue;
    }
    normalized += ch.toLowerCase();
  }
  if (!normalized) {
    return false;
  }
  return !(
    normalized.startsWith('javascript:') ||
    normalized.startsWith('data:') ||
    normalized.startsWith('vbscript:')
  );
}

export const isSafeUrl = isSafeHref;

/** Значение href из строки открывающего тега; null — атрибута нет. */
export function extractHref(tagString: unknown): string | null {
  const match = /(?:^|\s)href\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'>=`]+))/i.exec(
    String(tagString ?? ''),
  );
  if (!match) {
    return null;
  }
  if (match[1] !== undefined) {
    return match[1];
  }
  if (match[2] !== undefined) {
    return match[2];
  }
  return match[3] === undefined ? '' : match[3];
}

function appendBoundaryBreak(dest: Node, doc: Document): void {
  const last = dest.lastChild;
  if (!last) {
    return;
  }
  if (last.nodeType === 1 && String((last as Element).tagName || '').toLowerCase() === 'br') {
    return;
  }
  dest.appendChild(doc.createElement('br'));
}

function appendNode(source: Node, dest: Node, doc: Document): void {
  if (source.nodeType === 3 /* text */) {
    dest.appendChild(doc.createTextNode(source.nodeValue ?? ''));
    return;
  }
  if (source.nodeType !== 1 /* element */) {
    return; // comments, CDATA, PI — выбросить
  }
  const element = source as Element;
  const tag = String(element.tagName || '').toLowerCase();
  if (!tag || DROP_SUBTREE.has(tag)) {
    return;
  }
  const allowedAttrs = Object.prototype.hasOwnProperty.call(ALLOWED_TAGS, tag)
    ? ALLOWED_TAGS[tag]
    : null;
  if (!allowedAttrs) {
    // Неизвестный, но не опасный тег — разворачиваем, сохраняя безопасных детей.
    // Границу абзаца сохраняем одним <br>, чтобы текст не «склеивался».
    if (tag === 'br') {
      appendBoundaryBreak(dest, doc);
      return;
    }
    if (BLOCK_BOUNDARY_TAGS.has(tag)) {
      appendBoundaryBreak(dest, doc);
    }
    for (const child of Array.from(element.childNodes)) {
      appendNode(child, dest, doc);
    }
    return;
  }
  const el = doc.createElement(tag);
  for (const attr of allowedAttrs) {
    if (!element.hasAttribute(attr)) {
      continue;
    }
    let value = element.getAttribute(attr) ?? '';
    if (attr === 'href' && !isSafeUrl(value)) {
      continue;
    }
    if (attr === 'title') {
      value = value.slice(0, 300);
    }
    el.setAttribute(attr, value);
  }
  for (const child of Array.from(element.childNodes)) {
    appendNode(child, el, doc);
  }
  dest.appendChild(el);
}

/**
 * Возвращает безопасный HTML-фрагмент. Вызывается ТОЛЬКО для уже
 * санитизированного сервером `comment.text` (double defense).
 */
export function sanitizeHtml(html: unknown): string {
  if (html == null) {
    return '';
  }
  const source = String(html);
  if (!source) {
    return '';
  }
  if (typeof document === 'undefined' || typeof document.createElement !== 'function') {
    return escapeHtml(source);
  }
  const input = document.createElement('template');
  input.innerHTML = source;
  const output = document.createElement('template');
  for (const child of Array.from(input.content.childNodes)) {
    appendNode(child, output.content, document);
  }
  return output.innerHTML;
}

/** Текст без тегов. */
export function htmlToPlain(html: unknown): string {
  const safe = sanitizeHtml(html);
  if (typeof document === 'undefined') {
    return safe;
  }
  const div = document.createElement('div');
  div.innerHTML = safe;
  return (div.textContent || '').replace(/\s+/g, ' ').trim();
}

/**
 * Клиентское зеркало серверной проверки XHTML (HtmlSanitizer.Sanitize):
 *  - тег вне allow-list → ошибка;
 *  - у <a> обязателен непустой безопасный href;
 *  - несбалансированные/незакрытые теги → ошибка;
 *  - «голый» символ `<` → ошибка.
 */
export function findTagErrors(text: unknown): string[] {
  const errors: string[] = [];
  if (typeof text !== 'string' || text === '') {
    return errors;
  }
  const stack: string[] = [];
  TAG_RE.lastIndex = 0;
  let match: RegExpExecArray | null;
  while ((match = TAG_RE.exec(text)) !== null) {
    const closing = match[1] === '/';
    const name = match[2].toLowerCase();
    const selfClosing = match[4] === '/';
    if (!Object.prototype.hasOwnProperty.call(ALLOWED_TAGS, name)) {
      errors.push(`Тег <${name}> не разрешён.`);
      continue;
    }
    if (name === 'a' && !closing) {
      const href = extractHref(match[0]);
      if (href === null) {
        errors.push('У тега <a> должен быть непустой атрибут href.');
      } else if (!isSafeHref(href)) {
        errors.push('Недопустимая схема URL в href (запрещены javascript:, data:, vbscript:).');
      }
    }
    if (closing) {
      if (stack.length === 0) {
        errors.push(`Лишний закрывающий тег </${name}>.`);
      } else if (stack[stack.length - 1] !== name) {
        errors.push(`Ожидался </${stack[stack.length - 1]}>, но найден </${name}>.`);
      } else {
        stack.pop();
      }
    } else if (!selfClosing) {
      stack.push(name);
    }
  }
  for (const name of stack) {
    errors.push(`Тег <${name}> не закрыт.`);
  }
  const leftover = text.replace(TAG_RE, '');
  if (leftover.indexOf('<') !== -1) {
    errors.push("Символ '<' должен быть частью корректного тега.");
  }
  return errors;
}
