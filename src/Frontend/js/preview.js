/**
 * preview.js — AJAX-предпросмотр сообщения без перезагрузки страницы.
 * POST /api/preview {text} → {valid, html, plain, errors}.
 * Ответ рендерится через sanitizeHtml (defense-in-depth).
 */

import { previewText } from './api.js';
import { sanitizeHtml } from './sanitize.js';

/**
 * @param {{button?:HTMLElement, textarea:HTMLTextAreaElement,
 *          panel:HTMLElement, errorsEl?:HTMLElement, badgeEl?:HTMLElement,
 *          onError?:Function}} opts
 */
export function initPreview(opts) {
  const { button, textarea, panel } = opts;
  let inFlight = null;
  let runToken = 0;

  function setBadge(valid, hasErrors) {
    if (!opts.badgeEl) return;
    opts.badgeEl.classList.remove('is-valid', 'is-invalid', 'is-empty');
    if (valid) {
      opts.badgeEl.textContent = 'Корректно';
      opts.badgeEl.classList.add('is-valid');
    } else if (hasErrors) {
      opts.badgeEl.textContent = 'Есть ошибки';
      opts.badgeEl.classList.add('is-invalid');
    } else {
      opts.badgeEl.textContent = '';
      opts.badgeEl.classList.add('is-empty');
    }
  }

  function renderErrors(errors, fallback) {
    if (!opts.errorsEl) return;
    opts.errorsEl.textContent = '';
    const list = Array.isArray(errors) ? errors.slice() : [];
    if (!list.length && fallback) list.push(fallback);
    if (!list.length) {
      opts.errorsEl.hidden = true;
      return;
    }
    opts.errorsEl.hidden = false;
    const ul = document.createElement('ul');
    for (const item of list) {
      const li = document.createElement('li');
      li.textContent = String(item);
      ul.appendChild(li);
    }
    opts.errorsEl.appendChild(ul);
  }

  function renderEmpty() {
    panel.classList.add('is-empty');
    panel.textContent = 'Здесь появится предпросмотр сообщения.';
    panel.removeAttribute('data-state');
    setBadge(false, false);
    renderErrors([], null);
  }

  async function run() {
    const text = textarea ? textarea.value : '';
    const token = ++runToken;
    if (inFlight && inFlight.abort) inFlight.abort();
    const controller = typeof AbortController !== 'undefined' ? new AbortController() : null;
    inFlight = controller;
    if (button) {
      button.disabled = true;
      button.classList.add('is-loading');
    }
    panel.classList.remove('is-empty');
    panel.classList.add('is-loading');
    panel.textContent = 'Загрузка предпросмотра…';
    renderErrors([], null);
    try {
      const data = await previewText(text, controller ? controller.signal : undefined);
      if (token !== runToken) return null; // устаревший ответ
      const html = data && typeof data.html === 'string' ? data.html : '';
      panel.innerHTML = sanitizeHtml(html);
      if (!panel.innerHTML.trim()) {
        panel.classList.add('is-empty');
        panel.textContent = 'Предпросмотр пуст.';
      }
      const valid = Boolean(data && data.valid);
      setBadge(valid, Boolean(data && data.errors && data.errors.length));
      renderErrors((data && data.errors) || [], null);
      panel.dataset.state = valid ? 'valid' : 'invalid';
      return data;
    } catch (error) {
      if (error && error.name === 'AbortError') return null;
      if (token !== runToken) return null;
      panel.classList.add('is-empty');
      panel.textContent = 'Не удалось получить предпросмотр.';
      setBadge(false, true);
      renderErrors([], (error && (error.detail || error.message)) || 'Ошибка предпросмотра');
      if (opts.onError) opts.onError(error);
      return null;
    } finally {
      if (token === runToken) {
        inFlight = null;
        panel.classList.remove('is-loading');
        if (button) {
          button.disabled = false;
          button.classList.remove('is-loading');
        }
      }
    }
  }

  if (button) {
    button.addEventListener('click', (event) => {
      event.preventDefault();
      run();
    });
  }
  if (textarea) {
    textarea.addEventListener('keydown', (event) => {
      if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
        event.preventDefault();
        run();
      }
    });
  }
  renderEmpty();

  return { run, clear: renderEmpty };
}

export default { initPreview };
