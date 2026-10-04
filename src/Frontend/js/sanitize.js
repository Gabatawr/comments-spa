/**
 * sanitize.js — defense-in-depth allowlist sanitizer.
 *
 * Единственный модуль, который имеет право готовить HTML для innerHTML.
 * Белый список строго соответствует ТЗ/API.md: `a[href,title]`, `code`, `i`, `strong`.
 * Всё остальное либо разворачивается (теги снимаются, текст остаётся), либо
 * выбрасывается целиком (опасные поддеревья: script/style/svg/... ).
 */

export const ALLOWED_TAGS = Object.freeze({
  a: Object.freeze(['href', 'title']),
  code: Object.freeze([]),
  i: Object.freeze([]),
  strong: Object.freeze([]),
});

/** Поддеревья, которые нельзя даже разворачивать — их содержимое не показываем. */
const DROP_SUBTREE = new Set([
  'script', 'style', 'iframe', 'frame', 'frameset', 'object', 'embed', 'applet',
  'svg', 'math', 'template', 'noscript', 'link', 'meta', 'base', 'form', 'input',
  'button', 'textarea', 'select', 'option', 'audio', 'video', 'canvas', 'portal',
]);

const TAG_RE = /<(\/?)([a-zA-Z][a-zA-Z0-9:-]*)((?:"[^"]*"|'[^']*'|[^"'>])*?)(\/?)>/g;

/** Экранирование текста для безопасной вставки в HTML/атрибут. */
export function escapeHtml(value) {
  return String(value == null ? '' : value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

/**
 * Декодирование HTML-сущностей (числовых и основных именованных) — нужно, чтобы
 * `&#x6a;avascript:` не обошёл проверку схемы. DOM-парсер декодирует атрибуты сам,
 * но валидация текста выполняется по исходной строке.
 */
const NAMED_ENTITIES = Object.freeze({
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

export function decodeEntities(value) {
  return String(value == null ? '' : value)
    .replace(/&#x([0-9a-f]+);/gi, (match, hex) => {
      try {
        return String.fromCodePoint(parseInt(hex, 16));
      } catch {
        return match;
      }
    })
    .replace(/&#(\d+);/g, (match, dec) => {
      try {
        return String.fromCodePoint(Number(dec));
      } catch {
        return match;
      }
    })
    .replace(/&([a-z][a-z0-9]+);/gi, (match, name) => {
      const key = name.toLowerCase();
      return Object.prototype.hasOwnProperty.call(NAMED_ENTITIES, key)
        ? NAMED_ENTITIES[key]
        : match;
    });
}

/**
 * Проверка href — зеркало серверного HtmlSanitizer.IsSafeHref:
 * декодируем сущности, убираем пробелы/управляющие символы, приводим к нижнему
 * регистру и запрещаем только javascript:, data: и vbscript:. Пустой href запрещён.
 */
export function isSafeHref(raw) {
  if (typeof raw !== 'string') return false;
  const decoded = decodeEntities(raw);
  let normalized = '';
  for (const ch of decoded) {
    const code = ch.codePointAt(0);
    if (/\s/.test(ch) || code === 0x7f || (code >= 0x80 && code <= 0x9f)) continue;
    normalized += ch.toLowerCase();
  }
  if (!normalized) return false;
  return !(
    normalized.startsWith('javascript:') ||
    normalized.startsWith('data:') ||
    normalized.startsWith('vbscript:')
  );
}

/** Обратная совместимость: то же правило, что и серверный IsSafeHref. */
export const isSafeUrl = isSafeHref;

/** Достаёт значение href из строки открывающего тега. null — атрибута нет. */
export function extractHref(tagString) {
  const match = /(?:^|\s)href\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'>=`]+))/i.exec(
    String(tagString || '')
  );
  if (!match) return null;
  if (match[1] !== undefined) return match[1];
  if (match[2] !== undefined) return match[2];
  return match[3] === undefined ? '' : match[3];
}

function copyChildren(source, dest, doc) {
  const children = Array.prototype.slice.call(source.childNodes || []);
  for (const child of children) {
    if (child.nodeType === 3 /* TEXT_NODE */) {
      dest.appendChild(doc.createTextNode(child.nodeValue));
      continue;
    }
    if (child.nodeType !== 1 /* ELEMENT_NODE */) continue; // comments, CDATA, PI — выбросить
    const tag = String(child.tagName || '').toLowerCase();
    if (!tag) continue;
    if (DROP_SUBTREE.has(tag)) continue;

    const allowedAttrs = Object.prototype.hasOwnProperty.call(ALLOWED_TAGS, tag)
      ? ALLOWED_TAGS[tag]
      : null;

    if (!allowedAttrs) {
      // Неизвестный, но не опасный тег — разворачиваем, сохраняя безопасных детей.
      copyChildren(child, dest, doc);
      continue;
    }

    const el = doc.createElement(tag);
    for (const attr of allowedAttrs) {
      if (!child.hasAttribute(attr)) continue;
      let value = child.getAttribute(attr) || '';
      if (attr === 'href' && !isSafeUrl(value)) continue;
      if (attr === 'title') value = value.slice(0, 300);
      el.setAttribute(attr, value);
    }
    copyChildren(child, el, doc);
    dest.appendChild(el);
  }
}

/**
 * Возвращает безопасный HTML-фрагмент. Может использоваться для любого
 * ненадёжного HTML перед вставкой в innerHTML.
 */
export function sanitizeHtml(html) {
  if (html == null) return '';
  const source = String(html);
  if (!source) return '';
  if (typeof document === 'undefined' || typeof document.createElement !== 'function') {
    return escapeHtml(source);
  }
  const input = document.createElement('template');
  input.innerHTML = source;
  const output = document.createElement('template');
  copyChildren(input.content, output.content, document);
  return output.innerHTML;
}

/** Текст без тегов (для анонсов/подписей). */
export function htmlToPlain(html) {
  const safe = sanitizeHtml(html);
  if (typeof document === 'undefined') return safe;
  const div = document.createElement('div');
  div.innerHTML = safe;
  return (div.textContent || '').replace(/\s+/g, ' ').trim();
}

/**
 * Клиентское зеркало серверной проверки XHTML:
 *  - тег не из белого списка → ошибка;
 *  - у <a> должен быть непустой и безопасный href (javascript:/data:/vbscript: запрещены);
 *  - несбалансированные/незакрытые теги → ошибка;
 *  - «голый» символ `<` → ошибка.
 */
export function findTagErrors(text) {
  const errors = [];
  if (typeof text !== 'string' || text === '') return errors;
  const stack = [];
  TAG_RE.lastIndex = 0;
  let m;
  while ((m = TAG_RE.exec(text)) !== null) {
    const closing = m[1] === '/';
    const name = m[2].toLowerCase();
    const selfClosing = m[4] === '/';
    if (!Object.prototype.hasOwnProperty.call(ALLOWED_TAGS, name)) {
      errors.push(
        `Тег <${name}> не разрешён. Разрешены только <a href title>, <code>, <i>, <strong>.`
      );
      continue;
    }
    if (name === 'a' && !closing) {
      const href = extractHref(m[0]);
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
  for (const name of stack) errors.push(`Тег <${name}> не закрыт.`);
  const leftover = text.replace(TAG_RE, '');
  if (leftover.indexOf('<') !== -1) {
    errors.push("Символ '<' должен быть частью корректного тега.");
  }
  return errors;
}
