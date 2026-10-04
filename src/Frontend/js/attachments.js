/**
 * attachments.js — вложения: клиентские проверки, превью картинки до отправки,
 * lightbox для изображений и модальный просмотр TXT (строго textContent).
 *
 * Лимиты из docs/API.md: JPG/JPEG/PNG/GIF ≤ 5 МБ, TXT ≤ 100 КБ.
 */

import { LIMITS, IMAGE_TYPES, IMAGE_EXT_RE, TXT_EXT_RE, TXT_TYPE } from './validate.js';

export function formatBytes(bytes) {
  const n = Number(bytes) || 0;
  if (n < 1024) return `${n} Б`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} КБ`;
  return `${(n / (1024 * 1024)).toFixed(2)} МБ`;
}

/**
 * Клиентская предварительная проверка файла.
 * @returns {{ok:boolean, kind:'image'|'text'|null, error:string|null}}
 */
export function validateAttachmentFile(file) {
  if (!file) return { ok: true, kind: null, error: null };
  const name = file.name || '';
  const type = (file.type || '').toLowerCase();
  const isImage =
    IMAGE_TYPES.indexOf(type) !== -1 ||
    (type.startsWith('image/') && IMAGE_EXT_RE.test(name)) ||
    (type === '' && IMAGE_EXT_RE.test(name));
  const isTxt =
    type === TXT_TYPE || type === 'text/txt' || (type === '' && TXT_EXT_RE.test(name)) ||
    (TXT_EXT_RE.test(name) && !isImage);

  if (isImage && !IMAGE_EXT_RE.test(name)) {
    return { ok: false, kind: null, error: 'Разрешены изображения только JPG, GIF или PNG.' };
  }
  if (isImage) {
    if (file.size > LIMITS.imageMaxBytes) {
      return {
        ok: false,
        kind: 'image',
        error: `Изображение больше ${formatBytes(LIMITS.imageMaxBytes)} (${formatBytes(file.size)}).`,
      };
    }
    return { ok: true, kind: 'image', error: null };
  }
  if (isTxt) {
    if (file.size > LIMITS.txtMaxBytes) {
      return {
        ok: false,
        kind: 'text',
        error: `Текстовый файл больше ${formatBytes(LIMITS.txtMaxBytes)} (${formatBytes(file.size)}).`,
      };
    }
    return { ok: true, kind: 'text', error: null };
  }
  return {
    ok: false,
    kind: null,
    error: 'Можно приложить изображение JPG/JPEG/PNG/GIF ≤ 5 МБ или текстовый файл TXT ≤ 100 КБ.',
  };
}

function readImageSize(file) {
  return new Promise((resolve) => {
    const url = URL.createObjectURL(file);
    const probe = new Image();
    probe.onload = () => {
      resolve({ width: probe.naturalWidth, height: probe.naturalHeight, url });
    };
    probe.onerror = () => {
      URL.revokeObjectURL(url);
      resolve(null);
    };
    probe.src = url;
  });
}

/**
 * Поле вложения: проверки + превью до отправки.
 * @param {{input:HTMLInputElement, preview:HTMLElement, errorEl?:HTMLElement,
 *          onError?:Function, onChange?:Function}} opts
 */
export function initAttachmentField(opts) {
  const { input, preview } = opts;
  let file = null;
  let objectUrl = null;
  let lastResult = { ok: true, kind: null, error: null };
  let generation = 0;

  function revoke() {
    if (objectUrl) {
      URL.revokeObjectURL(objectUrl);
      objectUrl = null;
    }
  }

  function showError(message) {
    if (opts.errorEl) {
      opts.errorEl.textContent = message || '';
      opts.errorEl.hidden = !message;
    }
    if (input) input.classList.toggle('is-invalid', Boolean(message));
    if (message && opts.onError) opts.onError(message);
  }

  function clearPreview() {
    revoke();
    if (preview) {
      preview.textContent = '';
      preview.hidden = true;
    }
  }

  function renderPreview(current, result) {
    if (!preview) return;
    clearPreview();
    preview.hidden = false;
    if (!current) return;
    const gen = generation;

    const info = document.createElement('div');
    info.className = 'attachment-info';
    const name = document.createElement('span');
    name.className = 'attachment-name';
    name.textContent = current.name;
    const size = document.createElement('span');
    size.className = 'attachment-size';
    size.textContent = formatBytes(current.size);
    info.appendChild(name);
    info.appendChild(size);

    if (result.kind === 'image') {
      readImageSize(current).then((dim) => {
        if (!dim) return;
        if (gen !== generation) {
          URL.revokeObjectURL(dim.url); // файл успели заменить
          return;
        }
        objectUrl = dim.url;
        const img = document.createElement('img');
        img.className = 'attachment-thumb';
        img.src = dim.url;
        img.alt = `Превью ${current.name}`;
        img.title = 'Предпросмотр (нажмите, чтобы открыть)';
        img.dataset.lightbox = dim.url;
        img.dataset.caption = current.name;
        preview.insertBefore(img, info);
        const note = document.createElement('span');
        note.className = 'attachment-note';
        if (dim.width > LIMITS.imageMaxWidth || dim.height > LIMITS.imageMaxHeight) {
          note.textContent = `${dim.width}×${dim.height} → будет пропорционально уменьшено до ≤ ${LIMITS.imageMaxWidth}×${LIMITS.imageMaxHeight} px`;
        } else {
          note.textContent = `${dim.width}×${dim.height} px — уменьшение не потребуется`;
        }
        info.appendChild(note);
      });
    } else {
      const note = document.createElement('span');
      note.className = 'attachment-note';
      note.textContent = 'Текстовый файл — будет доступен для просмотра после отправки';
      info.appendChild(note);
    }
    preview.appendChild(info);
  }

  async function handleChange() {
    revoke();
    generation += 1;
    const selected = input && input.files && input.files[0] ? input.files[0] : null;
    file = selected;
    lastResult = validateAttachmentFile(selected);
    showError(lastResult.error);
    if (!selected || lastResult.error) {
      clearPreview();
      if (selected && lastResult.error) renderPreview(selected, { kind: null });
      if (opts.onChange) opts.onChange(null, lastResult);
      return lastResult;
    }
    renderPreview(selected, lastResult);
    if (opts.onChange) opts.onChange(selected, lastResult);
    return lastResult;
  }

  if (input) input.addEventListener('change', handleChange);

  return {
    handleChange,
    getFile: () => file,
    getResult: () => lastResult,
    validate: () => {
      lastResult = validateAttachmentFile(file);
      showError(lastResult.error);
      return lastResult;
    },
    clear: () => {
      file = null;
      generation += 1;
      lastResult = { ok: true, kind: null, error: null };
      if (input) {
        input.value = '';
        input.classList.remove('is-invalid');
      }
      showError(null);
      clearPreview();
    },
  };
}

/* ------------------------------ Lightbox ------------------------------ */

let lightboxKeyHandler = null;

function lockScroll(lock) {
  if (typeof document === 'undefined') return;
  document.documentElement.classList.toggle('is-modal-open', Boolean(lock));
}

export function closeLightbox() {
  const box = document.getElementById('lightbox');
  if (!box) return;
  box.classList.remove('is-open');
  box.setAttribute('aria-hidden', 'true');
  const img = document.getElementById('lightbox-img');
  if (img) img.removeAttribute('src');
  if (lightboxKeyHandler) {
    document.removeEventListener('keydown', lightboxKeyHandler);
    lightboxKeyHandler = null;
  }
  lockScroll(false);
}

/** Открывает изображение в лайтбоксе (overlay + fade/zoom, Esc/клик по фону). */
export function openLightbox(src, caption) {
  const box = document.getElementById('lightbox');
  if (!box || !src) {
    if (src) window.open(src, '_blank', 'noopener');
    return;
  }
  const img = document.getElementById('lightbox-img');
  const cap = document.getElementById('lightbox-caption');
  if (img) img.src = src;
  if (cap) cap.textContent = caption || '';
  box.classList.add('is-open');
  box.setAttribute('aria-hidden', 'false');
  lockScroll(true);
  lightboxKeyHandler = (event) => {
    if (event.key === 'Escape') closeLightbox();
  };
  document.addEventListener('keydown', lightboxKeyHandler);
}

/* ---------------------------- TXT modal ---------------------------- */

let textModalKeyHandler = null;

export function closeTextModal() {
  const modal = document.getElementById('text-modal');
  if (!modal) return;
  modal.classList.remove('is-open');
  modal.setAttribute('aria-hidden', 'true');
  const body = document.getElementById('text-modal-body');
  if (body) body.textContent = '';
  if (textModalKeyHandler) {
    document.removeEventListener('keydown', textModalKeyHandler);
    textModalKeyHandler = null;
  }
  lockScroll(false);
}

/**
 * Открывает TXT-файл в модальном окне и вставляет содержимое ТОЛЬКО через
 * textContent (innerHTML не используется — защита от XSS).
 */
export async function openTextModal(url, fileName) {
  const modal = document.getElementById('text-modal');
  if (!modal) return;
  const title = document.getElementById('text-modal-title');
  const body = document.getElementById('text-modal-body');
  const download = document.getElementById('text-modal-download');
  if (title) title.textContent = fileName || 'Текстовый файл';
  if (body) {
    body.textContent = 'Загрузка…';
    body.classList.add('is-loading');
  }
  if (download) {
    download.href = url;
    download.setAttribute('download', fileName || 'file.txt');
  }
  modal.classList.add('is-open');
  modal.setAttribute('aria-hidden', 'false');
  lockScroll(true);
  textModalKeyHandler = (event) => {
    if (event.key === 'Escape') closeTextModal();
  };
  document.addEventListener('keydown', textModalKeyHandler);

  try {
    const response = await fetch(url, { headers: { Accept: 'text/plain, */*' } });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const text = await response.text();
    if (body) body.textContent = text;
  } catch (error) {
    if (body) {
      body.textContent = `Не удалось загрузить файл: ${(error && error.message) || 'ошибка'}`;
    }
  } finally {
    if (body) body.classList.remove('is-loading');
  }
}

/** Делегирование кликов по миниатюрам и TXT-чипам. */
export function initAttachmentViewers(root) {
  const host = root || document;
  host.addEventListener('click', (event) => {
    const imageTrigger = event.target.closest('[data-lightbox]');
    if (imageTrigger) {
      event.preventDefault();
      openLightbox(imageTrigger.dataset.lightbox, imageTrigger.dataset.caption || '');
      return;
    }
    const textTrigger = event.target.closest('[data-text-viewer]');
    if (textTrigger) {
      event.preventDefault();
      openTextModal(textTrigger.dataset.textViewer, textTrigger.dataset.fileName || 'file.txt');
    }
  });

  const box = document.getElementById('lightbox');
  if (box) {
    box.addEventListener('click', (event) => {
      if (event.target === box || event.target.closest('[data-lightbox-close]')) closeLightbox();
    });
  }
  const modal = document.getElementById('text-modal');
  if (modal) {
    modal.addEventListener('click', (event) => {
      if (event.target === modal || event.target.closest('[data-modal-close]')) closeTextModal();
    });
  }
}

export default {
  initAttachmentField,
  validateAttachmentFile,
  openLightbox,
  closeLightbox,
  openTextModal,
  closeTextModal,
  initAttachmentViewers,
  formatBytes,
};
