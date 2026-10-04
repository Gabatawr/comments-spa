/**
 * validation.ts — клиентская валидация, зеркалящая серверные правила
 * (`CommentValidator.cs` + `HtmlSanitizer.cs`, docs/API-v2.md §2.5).
 *
 * Сообщения на русском (их видит пользователь); серверные ошибки приходят на
 * английском и показываются как есть.
 */

import { findTagErrors } from './sanitize';

export const LIMITS = Object.freeze({
  userNameMin: 3,
  userNameMax: 50,
  emailMax: 100,
  homePageMax: 200,
  textMin: 1,
  textMax: 5000,
  imageMaxBytes: 5 * 1024 * 1024,
  txtMaxBytes: 100 * 1024,
  imageMaxWidth: 320,
  imageMaxHeight: 240,
});

/** `^[A-Za-z0-9]+$` — как в CommentValidator. */
export const USER_NAME_RE = /^[A-Za-z0-9]+$/;

/** Тот же anchored RFC-lite шаблон, что и серверный EmailRegex. */
export const EMAIL_RE =
  /^[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+$/;

export const IMAGE_TYPES: readonly string[] = Object.freeze([
  'image/jpeg',
  'image/jpg',
  'image/png',
  'image/gif',
]);
export const IMAGE_EXT_RE = /\.(jpe?g|png|gif)$/i;
export const TXT_EXT_RE = /\.txt$/i;
export const TXT_TYPE = 'text/plain';

export type FieldKey =
  | 'userName'
  | 'email'
  | 'homePage'
  | 'text'
  | 'captcha'
  | 'attachment'
  | 'parentId';

export function validateUserName(value: string | null | undefined): string | null {
  // Сервер валидирует исходное значение (без trim): ведущий/хвостовой пробел — ошибка.
  const v = value == null ? '' : String(value);
  if (!v.trim()) {
    return 'Укажите User Name.';
  }
  if (v.length < LIMITS.userNameMin || v.length > LIMITS.userNameMax) {
    return `User Name должен быть длиной от ${LIMITS.userNameMin} до ${LIMITS.userNameMax} символов.`;
  }
  return USER_NAME_RE.test(v) ? null : 'User Name может содержать только латинские буквы и цифры.';
}

export function validateEmail(value: string | null | undefined): string | null {
  const v = value == null ? '' : String(value);
  if (!v.trim()) {
    return 'Укажите E-mail.';
  }
  if (v.length > LIMITS.emailMax) {
    return `E-mail не длиннее ${LIMITS.emailMax} символов.`;
  }
  if (v !== v.trim()) {
    return 'E-mail не должен содержать пробелов в начале или конце.';
  }
  return EMAIL_RE.test(v) ? null : 'Некорректный формат E-mail.';
}

export function validateHomePage(value: string | null | undefined): string | null {
  const v = (value == null ? '' : String(value)).trim();
  if (!v) {
    return null; // необязательное поле
  }
  if (v.length > LIMITS.homePageMax) {
    return `Home page не длиннее ${LIMITS.homePageMax} символов.`;
  }
  let url: URL;
  try {
    url = new URL(v);
  } catch {
    return 'Home page должен быть абсолютным URL, например https://example.com.';
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    return 'Home page должен начинаться с http:// или https://.';
  }
  return url.hostname ? null : 'Home page должен содержать домен.';
}

export function validateText(value: string | null | undefined): string | null {
  const v = value == null ? '' : String(value);
  if (!v.trim()) {
    return 'Введите текст комментария.';
  }
  if (v.length > LIMITS.textMax) {
    return `Текст не длиннее ${LIMITS.textMax} символов (сейчас ${v.length}).`;
  }
  const tagErrors = findTagErrors(v);
  return tagErrors.length ? tagErrors.join(' ') : null;
}

export function validateCaptchaAnswer(value: string | null | undefined): string | null {
  return (value == null ? '' : String(value)).trim() ? null : 'Введите код с картинки.';
}

export interface AttachmentCheck {
  ok: boolean;
  kind: 'image' | 'text' | null;
  error: string | null;
}

/** Клиентская предварительная проверка файла (docs/API-v2.md §2.5). */
export function validateAttachmentFile(file: File | null | undefined): AttachmentCheck {
  if (!file) {
    return { ok: true, kind: null, error: null };
  }
  const name = file.name || '';
  const type = (file.type || '').toLowerCase();
  const isImage =
    IMAGE_TYPES.indexOf(type) !== -1 ||
    (type.startsWith('image/') && IMAGE_EXT_RE.test(name)) ||
    (type === '' && IMAGE_EXT_RE.test(name));
  const isTxt =
    type === TXT_TYPE ||
    type === 'text/txt' ||
    (type === '' && TXT_EXT_RE.test(name)) ||
    (TXT_EXT_RE.test(name) && !isImage);

  if (isImage && !IMAGE_EXT_RE.test(name)) {
    return { ok: false, kind: null, error: 'Разрешены изображения только JPG, GIF или PNG.' };
  }
  if (isImage) {
    if (file.size > LIMITS.imageMaxBytes) {
      return {
        ok: false,
        kind: 'image',
        error: `Изображение больше 5 МБ (${formatBytesClient(file.size)}).`,
      };
    }
    return { ok: true, kind: 'image', error: null };
  }
  if (isTxt) {
    if (file.size > LIMITS.txtMaxBytes) {
      return {
        ok: false,
        kind: 'text',
        error: `Текстовый файл больше 100 КБ (${formatBytesClient(file.size)}).`,
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

function formatBytesClient(bytes: number): string {
  const n = Number(bytes) || 0;
  if (n < 1024) {
    return `${n} Б`;
  }
  if (n < 1024 * 1024) {
    return `${(n / 1024).toFixed(1)} КБ`;
  }
  return `${(n / (1024 * 1024)).toFixed(2)} МБ`;
}

export interface FormValidationInput {
  userName: string;
  email: string;
  homePage: string;
  text: string;
  captchaAnswer: string;
}

/** Валидирует все поля формы разом. Возвращает карту field → message. */
export function validateAll(data: Partial<FormValidationInput>): Partial<Record<FieldKey, string>> {
  const errors: Partial<Record<FieldKey, string>> = {};
  const userName = validateUserName(data.userName);
  if (userName) {
    errors.userName = userName;
  }
  const email = validateEmail(data.email);
  if (email) {
    errors.email = email;
  }
  const homePage = validateHomePage(data.homePage);
  if (homePage) {
    errors.homePage = homePage;
  }
  const text = validateText(data.text);
  if (text) {
    errors.text = text;
  }
  const captcha = validateCaptchaAnswer(data.captchaAnswer);
  if (captcha) {
    errors.captcha = captcha;
  }
  return errors;
}
