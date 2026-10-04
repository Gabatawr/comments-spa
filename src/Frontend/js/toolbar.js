/**
 * toolbar.js — панель кнопок [i] [strong] [code] [a].
 * Кнопки оборачивают выделение в textarea; [a] спрашивает href и title
 * и вставляет <a href="..." title="...">...</a>.
 */

import { isSafeHref } from './sanitize.js';

const WRAP = {
  i: { open: '<i>', close: '</i>' },
  strong: { open: '<strong>', close: '</strong>' },
  code: { open: '<code>', close: '</code>' },
};

function attrEscape(value) {
  return String(value == null ? '' : value).replace(/&/g, '&amp;').replace(/"/g, '&quot;');
}

/** Заменяет выделение на open+selection+close, сохраняя фокус и позицию курсора. */
export function wrapSelection(textarea, open, close) {
  const value = textarea.value;
  const start = textarea.selectionStart == null ? value.length : textarea.selectionStart;
  const end = textarea.selectionEnd == null ? start : textarea.selectionEnd;
  const selected = value.slice(start, end);
  textarea.value = value.slice(0, start) + open + selected + close + value.slice(end);
  if (selected) {
    textarea.selectionStart = start + open.length;
    textarea.selectionEnd = start + open.length + selected.length;
  } else {
    const pos = start + open.length;
    textarea.selectionStart = textarea.selectionEnd = pos;
  }
  textarea.focus();
  textarea.dispatchEvent(new Event('input', { bubbles: true }));
}

/** [a]: спрашивает URL и title, собирает тег; опасные схемы отклоняет. */
export function insertLink(textarea, askHref, askTitle, onError) {
  const selected = textarea.value.slice(textarea.selectionStart, textarea.selectionEnd);
  let href = askHref('Адрес ссылки (href):', 'https://');
  if (href == null) return false; // отмена
  href = String(href).trim();
  if (!href) {
    if (typeof onError === 'function') onError('Адрес ссылки не может быть пустым.');
    return false;
  }
  if (!isSafeHref(href)) {
    if (typeof onError === 'function') {
      onError('Недопустимый адрес: запрещены схемы javascript:, data:, vbscript:.');
    }
    return false;
  }
  let title = askTitle('Заголовок ссылки (title, можно оставить пустым):', '');
  title = title == null ? '' : String(title);
  const open = title
    ? `<a href="${attrEscape(href)}" title="${attrEscape(title)}">`
    : `<a href="${attrEscape(href)}">`;
  wrapSelection(textarea, open, '</a>');
  return true;
}

/**
 * Навешивает обработчики на панель.
 * @param {{toolbar:HTMLElement, textarea:HTMLTextAreaElement,
 *          askHref?:Function, askTitle?:Function, onError?:Function}} opts
 */
export function initToolbar(opts) {
  const { toolbar, textarea } = opts;
  if (!toolbar || !textarea) return null;
  const askHref = opts.askHref || ((message, def) => window.prompt(message, def));
  const askTitle = opts.askTitle || ((message, def) => window.prompt(message, def));

  function handle(tag) {
    if (tag === 'a') {
      // insertLink сам сообщает причину через onError (пустой/опасный href).
      insertLink(textarea, askHref, askTitle, opts.onError);
      return;
    }
    const spec = WRAP[tag];
    if (spec) wrapSelection(textarea, spec.open, spec.close);
  }

  toolbar.addEventListener('click', (event) => {
    const button = event.target.closest('button[data-tag]');
    if (!button || !toolbar.contains(button)) return;
    event.preventDefault();
    handle(button.dataset.tag);
  });

  textarea.addEventListener('keydown', (event) => {
    if (!(event.ctrlKey || event.metaKey) || event.altKey) return;
    const key = event.key.toLowerCase();
    if (key === 'b') {
      event.preventDefault();
      handle('strong');
    } else if (key === 'i') {
      event.preventDefault();
      handle('i');
    } else if (key === 'k') {
      event.preventDefault();
      handle('a');
    }
  });

  return { handle };
}

export default { initToolbar, wrapSelection, insertLink };
