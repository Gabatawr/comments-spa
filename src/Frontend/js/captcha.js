/**
 * captcha.js — загрузка GET /api/captcha в <img> и кнопка обновления.
 */

import { getCaptcha } from './api.js';

/**
 * @param {{img:HTMLImageElement, refreshButton?:HTMLButtonElement,
 *          statusEl?:HTMLElement, onLoad?:Function, onError?:Function}} opts
 */
export function initCaptcha(opts) {
  const { img } = opts;
  let captchaId = null;
  let loading = false;

  function setStatus(text, kind) {
    if (!opts.statusEl) return;
    opts.statusEl.textContent = text || '';
    opts.statusEl.className = `captcha-status${kind ? ` is-${kind}` : ''}`;
  }

  async function load() {
    if (!img || loading) return null;
    loading = true;
    if (opts.refreshButton) opts.refreshButton.disabled = true;
    img.classList.add('is-loading');
    setStatus('Загрузка CAPTCHA…');
    try {
      const data = await getCaptcha();
      captchaId = data && data.captchaId ? data.captchaId : null;
      if (!captchaId || !data.image) throw new Error('Некорректный ответ CAPTCHA');
      if (opts.hiddenInput) opts.hiddenInput.value = captchaId;
      img.src = data.image;
      img.alt = 'CAPTCHA';
      setStatus(`Код действует ${Math.round((data.expiresInSeconds || 300) / 60)} мин.`, 'ok');
      if (opts.onLoad) opts.onLoad(captchaId, data);
      return captchaId;
    } catch (error) {
      captchaId = null;
      if (opts.hiddenInput) opts.hiddenInput.value = '';
      img.removeAttribute('src');
      setStatus('Не удалось загрузить CAPTCHA. Нажмите «Обновить».', 'error');
      if (opts.onError) opts.onError(error);
      return null;
    } finally {
      loading = false;
      img.classList.remove('is-loading');
      if (opts.refreshButton) opts.refreshButton.disabled = false;
    }
  }

  if (opts.refreshButton) {
    opts.refreshButton.addEventListener('click', (event) => {
      event.preventDefault();
      load();
    });
  }
  if (opts.onChange) opts.onChange(load);

  return {
    load,
    getCaptchaId: () => captchaId,
    hasCaptcha: () => Boolean(captchaId),
    reset: () => {
      captchaId = null;
      if (opts.hiddenInput) opts.hiddenInput.value = '';
    },
  };
}

export default { initCaptcha };
