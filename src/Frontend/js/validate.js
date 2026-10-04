/**
 * validate.js — клиентская валидация, зеркалящая серверные правила из docs/API.md.
 * Все сообщения на русском (их видит пользователь); серверные — на английском,
 * они показываются как есть.
 */

import { findTagErrors } from './sanitize.js';

export const LIMITS = Object.freeze({
  userNameMin: 3,
  userNameMax: 50,
  emailMax: 100,
  homePageMax: 200,
  textMin: 1,
  textMax: 5000,
  imageMaxBytes: 5 * 1024 * 1024, // 5 MB
  txtMaxBytes: 100 * 1024, // 100 KB
  imageMaxWidth: 320,
  imageMaxHeight: 240,
});

export const USER_NAME_RE = /^[A-Za-z0-9]+$/;
export const EMAIL_RE =
  /^[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+$/;
export const IMAGE_TYPES = Object.freeze(['image/jpeg', 'image/jpg', 'image/png', 'image/gif']);
export const IMAGE_EXT_RE = /\.(jpe?g|png|gif)$/i;
export const TXT_EXT_RE = /\.txt$/i;
export const TXT_TYPE = 'text/plain';

export function validateUserName(value) {
  // Сервер валидирует исходное значение (без trim): ведущий/хвостовой пробел — ошибка.
  const v = value == null ? '' : String(value);
  if (!v.trim()) return 'Укажите User Name.';
  if (v.length < LIMITS.userNameMin || v.length > LIMITS.userNameMax) {
    return `User Name должен быть длиной от ${LIMITS.userNameMin} до ${LIMITS.userNameMax} символов.`;
  }
  if (!USER_NAME_RE.test(v)) return 'User Name может содержать только латинские буквы и цифры.';
  return null;
}

export function validateEmail(value) {
  const v = value == null ? '' : String(value);
  if (!v.trim()) return 'Укажите E-mail.';
  if (v.length > LIMITS.emailMax) return `E-mail не длиннее ${LIMITS.emailMax} символов.`;
  if (v !== v.trim()) return 'E-mail не должен содержать пробелов в начале или конце.';
  if (!EMAIL_RE.test(v)) return 'Некорректный формат E-mail.';
  return null;
}

export function validateHomePage(value) {
  const v = (value == null ? '' : String(value)).trim();
  if (!v) return null; // необязательное поле
  if (v.length > LIMITS.homePageMax) return `Home page не длиннее ${LIMITS.homePageMax} символов.`;
  let url;
  try {
    url = new URL(v);
  } catch {
    return 'Home page должен быть абсолютным URL, например https://example.com.';
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    return 'Home page должен начинаться с http:// или https://.';
  }
  if (!url.hostname) return 'Home page должен содержать домен.';
  return null;
}

export function validateText(value) {
  const v = value == null ? '' : String(value);
  // Сервер: string.IsNullOrWhiteSpace(value) → "text required".
  if (!v.trim()) return 'Введите текст комментария.';
  if (v.length > LIMITS.textMax) {
    return `Текст не длиннее ${LIMITS.textMax} символов (сейчас ${v.length}).`;
  }
  const tagErrors = findTagErrors(v);
  if (tagErrors.length) return tagErrors.join(' ');
  return null;
}

export function validateCaptchaAnswer(value) {
  const v = (value == null ? '' : String(value)).trim();
  if (!v) return 'Введите код с картинки.';
  return null;
}

/**
 * Валидирует все поля формы разом.
 * @returns {{valid:boolean, errors:Object<string,string>}}
 */
export function validateAll(data = {}) {
  const errors = {};
  const userName = validateUserName(data.userName);
  if (userName) errors.userName = userName;
  const email = validateEmail(data.email);
  if (email) errors.email = email;
  const homePage = validateHomePage(data.homePage);
  if (homePage) errors.homePage = homePage;
  const text = validateText(data.text);
  if (text) errors.text = text;
  const captcha = validateCaptchaAnswer(data.captchaAnswer);
  if (captcha) errors.captcha = captcha;
  return { valid: Object.keys(errors).length === 0, errors };
}

/** Возвращает список ошибок для тега в тексте (для блока предпросмотра). */
export function validateTagClosure(text) {
  return findTagErrors(text == null ? '' : String(text));
}
