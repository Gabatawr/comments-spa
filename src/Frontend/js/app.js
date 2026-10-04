/**
 * app.js — точка входа SPA «Комментарии».
 * Связывает форму, валидацию, CAPTCHA, вложения, предпросмотр, список и WebSocket.
 */

import * as api from './api.js';
import { validateAll, LIMITS } from './validate.js';
import { initToolbar } from './toolbar.js';
import { initCaptcha } from './captcha.js';
import { initPreview } from './preview.js';
import { validateAttachmentFile, initAttachmentField, initAttachmentViewers, formatBytes } from './attachments.js';
import { createCommentsView } from './comments.js';
import { createRealtime } from './ws.js';

const $ = (id) => document.getElementById(id);
const $$ = (selector, root) => Array.prototype.slice.call((root || document).querySelectorAll(selector));

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text != null) node.textContent = String(text);
  return node;
}

const FIELD_INPUTS = {
  userName: 'userName',
  email: 'email',
  homePage: 'homePage',
  text: 'text',
  captcha: 'captchaAnswer',
  parentId: null,
  attachment: 'attachment',
};

const MESSAGES = {
  network: 'Сервер недоступен. Проверьте соединение и повторите попытку.',
  generic: 'Произошла ошибка. Попробуйте ещё раз.',
};

/* ------------------------------- toasts ------------------------------- */

function toast(message, type, options) {
  const opts = options || {};
  const host = $('toasts');
  if (!host) return { remove() {} };
  const item = el('div', `toast toast-${type || 'info'}`);
  item.setAttribute('role', type === 'error' ? 'alert' : 'status');
  item.appendChild(el('span', 'toast-text', message));

  if (opts.actionLabel && typeof opts.action === 'function') {
    const action = el('button', 'toast-action', opts.actionLabel);
    action.type = 'button';
    action.addEventListener('click', () => {
      opts.action();
      remove();
    });
    item.appendChild(action);
  }

  const close = el('button', 'toast-close', '×');
  close.type = 'button';
  close.setAttribute('aria-label', 'Закрыть');
  close.addEventListener('click', () => remove());
  item.appendChild(close);

  host.appendChild(item);
  requestAnimationFrame(() => item.classList.add('is-visible'));

  let timer = null;
  function remove() {
    if (timer) clearTimeout(timer);
    item.classList.remove('is-visible');
    setTimeout(() => item.remove(), 250);
  }
  const timeout = opts.timeout == null ? (type === 'error' ? 8000 : 4500) : opts.timeout;
  if (timeout > 0) timer = setTimeout(remove, timeout);
  return { remove };
}

/* --------------------------- form error state --------------------------- */

function setFieldError(name, message) {
  const holders = $$(`[data-error-for="${name}"]`);
  const inputId = FIELD_INPUTS[name];
  const input = inputId ? $(inputId) : null;
  const text = message ? String(message) : '';
  if (holders.length) {
    holders.forEach((holder) => {
      holder.textContent = text;
      holder.hidden = !text;
    });
  }
  if (input) input.classList.toggle('is-invalid', Boolean(text));
}

function clearFieldErrors() {
  $$('[data-error-for]').forEach((holder) => {
    holder.textContent = '';
    holder.hidden = true;
  });
  $$('.is-invalid').forEach((node) => {
    if (node.closest('#comment-form')) node.classList.remove('is-invalid');
  });
  const banner = $('form-general-error');
  if (banner) {
    banner.textContent = '';
    banner.hidden = true;
  }
}

function renderServerErrors(errors) {
  let firstField = null;
  Object.keys(errors || {}).forEach((key) => {
    const raw = errors[key];
    const message = Array.isArray(raw) ? raw.join(' ') : String(raw == null ? '' : raw);
    if (!message) return;
    if (key === 'parentId' || !Object.prototype.hasOwnProperty.call(FIELD_INPUTS, key)) {
      const banner = $('form-general-error');
      if (banner) {
        banner.hidden = false;
        banner.textContent = banner.textContent
          ? `${banner.textContent} ${message}`
          : message;
      }
    } else {
      setFieldError(key, message);
      if (!firstField) firstField = FIELD_INPUTS[key];
    }
  });
  if (firstField && $(firstField)) $(firstField).focus();
}

/* ------------------------------- captcha ------------------------------- */

function initCaptchaSection() {
  const img = $('captcha-image');
  if (!img) return { getCaptchaId: () => null, load: () => {}, reset: () => {} };
  const controller = initCaptcha({
    img,
    refreshButton: $('captcha-reload'),
    hiddenInput: $('captchaId'),
    statusEl: $('captcha-status'),
    onError: () => toast('Не удалось загрузить CAPTCHA. Попробуйте ещё раз.', 'error'),
  });
  // Клик по самой картинке тоже обновляет CAPTCHA.
  const imageButton = $('captcha-refresh');
  if (imageButton) {
    imageButton.addEventListener('click', (event) => {
      event.preventDefault();
      controller.load();
    });
  }
  return controller;
}

/* ------------------------------ comments ------------------------------ */

function initComments(onReply) {
  return createCommentsView({
    table: $('comments-table'),
    tbody: $('comments-tbody'),
    emptyEl: $('comments-empty'),
    loadingEl: $('comments-loading'),
    paginationEl: $('pagination'),
    statusEl: $('comments-status'),
    pageSize: api.DEFAULT_PAGE_SIZE,
    onReply,
    onError: (message) => {
      if (message) toast(message, 'error');
    },
  });
}

/* -------------------------------- health -------------------------------- */

function initHealth() {
  const badge = $('health-badge');
  async function check() {
    if (!badge) return;
    badge.classList.remove('is-ok', 'is-error');
    try {
      const data = await api.getHealth();
      const ok = data && data.status === 'ok';
      badge.classList.add(ok ? 'is-ok' : 'is-error');
      badge.textContent = ok ? 'API: ok' : `API: ${data && data.status}`;
      badge.title = JSON.stringify(data);
    } catch (error) {
      badge.classList.add('is-error');
      badge.textContent = 'API: недоступен';
      badge.title = (error && error.message) || '';
    }
  }
  check();
  const timer = setInterval(check, 60000);
  return { check, stop: () => clearInterval(timer) };
}

/* -------------------------------- app -------------------------------- */

function boot() {
  const form = $('comment-form');
  const submitButton = $('submit-btn');
  const submitLabel = submitButton ? submitButton.textContent : '';
  const text = $('text');
  const captchaIdInput = $('captchaId');
  const parentIdInput = $('parentId');

  const captcha = initCaptchaSection();

  const attachmentField = initAttachmentField({
    input: $('attachment'),
    preview: $('attachment-preview'),
    errorEl: $('attachment-error'),
    onError: () => {},
  });

  if ($('tag-toolbar') && text) {
    initToolbar({
      toolbar: $('tag-toolbar'),
      textarea: text,
      onError: (message) => toast(message, 'info', { timeout: 2500 }),
    });
  }

  const textCounter = $('text-counter');
  function updateCounter() {
    if (textCounter && text) textCounter.textContent = `${text.value.length} / ${LIMITS.textMax}`;
  }
  if (text) {
    text.addEventListener('input', updateCounter);
    updateCounter();
  }

  const preview = initPreview({
    button: $('preview-btn'),
    textarea: text,
    panel: $('preview-panel'),
    errorsEl: $('preview-errors'),
    badgeEl: $('preview-badge'),
  });

  let parentId = null;

  function setReplyTarget(comment) {
    parentId = comment && comment.id != null ? comment.id : null;
    const banner = $('reply-banner');
    const bannerText = $('reply-banner-text');
    if (parentIdInput) parentIdInput.value = parentId == null ? '' : String(parentId);
    if (banner) {
      banner.hidden = parentId == null;
      if (bannerText) {
        bannerText.textContent = parentId == null
          ? ''
          : `Ответ на комментарий #${parentId}${comment && comment.userName ? ` — ${comment.userName}` : ''}`;
      }
    }
  }

  const comments = initComments((comment) => {
    setReplyTarget(comment);
    if (form) {
      form.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }
    if (text) text.focus();
    toast(`Ответ на комментарий #${comment.id}`, 'info', { timeout: 2500 });
  });

  const cancelReply = $('reply-cancel');
  if (cancelReply) {
    cancelReply.addEventListener('click', (event) => {
      event.preventDefault();
      setReplyTarget(null);
    });
  }

  function collect() {
    return {
      userName: $('userName') ? $('userName').value : '',
      email: $('email') ? $('email').value : '',
      homePage: $('homePage') ? $('homePage').value : '',
      text: text ? text.value : '',
      // captchaId живёт в контроллере CAPTCHA; hidden-поле синхронизируется ниже.
      captchaId: captcha.getCaptchaId() || (captchaIdInput ? captchaIdInput.value : ''),
      captchaAnswer: $('captchaAnswer') ? $('captchaAnswer').value : '',
      parentId,
    };
  }

  function setSubmitting(value) {
    if (!submitButton) return;
    submitButton.disabled = value;
    submitButton.classList.toggle('is-loading', value);
    submitButton.textContent = value ? 'Отправка…' : submitLabel || 'Отправить';
  }

  // Снимаем ошибку при исправлении поля.
  $$('#comment-form input, #comment-form textarea').forEach((input) => {
    const eventName = input.tagName === 'SELECT' ? 'change' : 'input';
    input.addEventListener(eventName, () => {
      const name = input.name;
      if (!name) return;
      const key = name === 'captchaAnswer' ? 'captcha' : name;
      setFieldError(key, '');
      if (name === 'attachment') setFieldError('attachment', '');
    });
  });

  async function onSubmit(event) {
    event.preventDefault();
    clearFieldErrors();

    const data = collect();
    const result = validateAll(data);
    const errors = Object.assign({}, result.errors);
    const attachmentResult = attachmentField.validate();
    if (!attachmentResult.ok && attachmentResult.error) errors.attachment = attachmentResult.error;

    if (!captcha.getCaptchaId() && !errors.captcha) {
      errors.captcha = 'Обновите CAPTCHA.';
    }

    if (Object.keys(errors).length) {
      renderServerErrors(errors);
      toast('Проверьте поля формы.', 'error');
      return;
    }

    setSubmitting(true);
    try {
      const response = await api.createComment(data, attachmentField.getFile());
      const created = response && response.comment ? response.comment : null;
      suppressUntil = Date.now() + 2000;
      toast(
        created && created.parentId
          ? 'Ответ добавлен.'
          : 'Комментарий добавлен.',
        'success'
      );
      if (form) form.reset();
      setReplyTarget(null);
      attachmentField.clear();
      preview.clear();
      captcha.reset();
      await captcha.load();
      if (!created || created.parentId == null) {
        const state = comments.getState();
        if (state.page === 1) await comments.refresh();
        else await comments.goToPage(1);
      } else {
        await comments.refresh();
      }
    } catch (error) {
      if (error && error.status === 400 && error.errors) {
        renderServerErrors(error.errors);
        toast('Сервер отклонил данные: исправьте ошибки в форме.', 'error');
      } else if (error && error.status === 0) {
        toast(MESSAGES.network, 'error');
      } else {
        toast((error && (error.detail || error.message)) || MESSAGES.generic, 'error');
      }
    } finally {
      setSubmitting(false);
    }
  }

  if (form) form.addEventListener('submit', onSubmit);

  const refreshButton = $('comments-refresh');
  if (refreshButton) {
    refreshButton.addEventListener('click', (event) => {
      event.preventDefault();
      comments.refresh();
    });
  }

  /* ----------------------------- realtime ----------------------------- */

  let suppressUntil = 0;
  const realtimeBadge = $('realtime-status');

  function setRealtimeStatus(state) {
    if (!realtimeBadge) return;
    const labels = {
      connecting: 'WS: подключение…',
      online: 'WS: online',
      offline: 'WS: offline',
      reconnecting: 'WS: переподключение…',
      error: 'WS: ошибка',
      stopped: 'WS: остановлен',
      unsupported: 'WS: не поддерживается',
    };
    realtimeBadge.textContent = labels[state] || `WS: ${state}`;
    realtimeBadge.className = `realtime-status is-${state}`;
  }

  const realtime = createRealtime({
    onStatus: setRealtimeStatus,
    onCommentCreated: (comment) => {
      if (!comment) return;
      if (Date.now() < suppressUntil) return; // собственный комментарий уже обработан
      const state = comments.getState();
      const isReply = comment.parentId != null;
      const shouldRefresh =
        (!isReply && state.page === 1 && comments.isDefaultSort()) ||
        (isReply && comment.parentId != null && comments.containsComment(comment.parentId));

      toast(`Новый комментарий от ${comment.userName || 'пользователя'}`, 'info', {
        actionLabel: 'Показать',
        action: () => {
          if (!isReply) comments.goToPage(1);
          else comments.refresh();
        },
      });

      if (shouldRefresh) comments.refresh();
    },
  });

  initAttachmentViewers(document);
  realtime.start();
  comments.load().catch(() => {});
  captcha.load();
  const health = initHealth();

  window.addEventListener('beforeunload', () => {
    realtime.stop();
    health.stop();
  });

  // Экспорт для отладки/тестов (не является частью контракта).
  window.__commentsApp = {
    api,
    comments,
    captcha,
    attachmentField,
    preview,
    realtime,
    toast,
    LIMITS,
    formatBytes,
  };
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', boot);
} else {
  boot();
}

export { toast, validateAttachmentFile, formatBytes };
