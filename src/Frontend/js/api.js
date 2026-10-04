/**
 * api.js — тонкая обёртка над fetch для всех эндпоинтов docs/API.md.
 * Один origin, base = '' (SPA отдаётся backend-ом).
 */

export const DEFAULT_PAGE_SIZE = 25;
export const DEFAULT_SORT_BY = 'createdAt';
export const DEFAULT_SORT_DIR = 'desc';
export const SORT_FIELDS = Object.freeze(['createdAt', 'userName', 'email']);

export class ApiError extends Error {
  constructor(status, body, statusText) {
    const detail =
      (body && (body.detail || body.title)) || statusText || `HTTP ${status}`;
    super(detail);
    this.name = 'ApiError';
    this.status = status;
    this.statusText = statusText || '';
    this.body = body;
    this.title = body && body.title;
    this.detail = body && body.detail;
    this.errors = (body && body.errors) || null;
  }
}

async function request(url, options = {}) {
  let response;
  try {
    response = await fetch(url, options);
  } catch (cause) {
    const err = new ApiError(0, null, 'Сеть недоступна');
    err.cause = cause;
    throw err;
  }

  const contentType = response.headers.get('content-type') || '';
  let body = null;
  if (response.status !== 204 && response.status !== 205) {
    try {
      if (contentType.indexOf('application/json') !== -1) {
        body = await response.json();
      } else {
        const text = await response.text();
        body = text ? { detail: text } : null;
      }
    } catch {
      body = null;
    }
  }

  if (!response.ok) throw new ApiError(response.status, body, response.statusText);
  return body;
}

function withSignal(options, signal) {
  return signal ? Object.assign({}, options, { signal }) : options;
}

/** POST/GET JSON справка. */
export function buildQuery(params = {}) {
  const qs = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value === undefined || value === null || value === '') continue;
    qs.set(key, String(value));
  }
  const s = qs.toString();
  return s ? `?${s}` : '';
}

/** GET /api/health */
export function getHealth(signal) {
  return request('/api/health', withSignal({ method: 'GET' }, signal));
}

/** GET /api/captcha → {captchaId, image, expiresInSeconds} */
export function getCaptcha(signal) {
  return request('/api/captcha', withSignal({ method: 'GET' }, signal));
}

/**
 * GET /api/comments
 * @param {{page?:number,pageSize?:number,sortBy?:string,sortDir?:string}} params
 */
export function listComments(params = {}, signal) {
  const query = buildQuery({
    page: params.page == null ? 1 : params.page,
    pageSize: params.pageSize == null ? DEFAULT_PAGE_SIZE : params.pageSize,
    sortBy: params.sortBy || DEFAULT_SORT_BY,
    sortDir: params.sortDir || DEFAULT_SORT_DIR,
  });
  return request(`/api/comments${query}`, withSignal({ method: 'GET' }, signal));
}

/** GET /api/comments/{id} */
export function getComment(id, signal) {
  return request(`/api/comments/${encodeURIComponent(id)}`, withSignal({ method: 'GET' }, signal));
}

/** POST /api/preview → {valid, html, plain, errors} */
export function previewText(text, signal) {
  return request(
    '/api/preview',
    withSignal(
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ text: text == null ? '' : String(text) }),
      },
      signal
    )
  );
}

/**
 * POST /api/comments (multipart/form-data).
 * Имена полей строго по docs/API.md:
 * userName, email, homePage, text, captchaId, captchaAnswer, parentId, attachment.
 */
export function createComment(fields = {}, file = null, signal) {
  const fd = new FormData();
  fd.append('userName', fields.userName == null ? '' : String(fields.userName));
  fd.append('email', fields.email == null ? '' : String(fields.email));
  fd.append('homePage', fields.homePage == null ? '' : String(fields.homePage));
  fd.append('text', fields.text == null ? '' : String(fields.text));
  fd.append('captchaId', fields.captchaId == null ? '' : String(fields.captchaId));
  fd.append('captchaAnswer', fields.captchaAnswer == null ? '' : String(fields.captchaAnswer));
  if (fields.parentId != null && fields.parentId !== '') {
    fd.append('parentId', String(fields.parentId));
  }
  if (file) fd.append('attachment', file, file.name || 'attachment');
  return request('/api/comments', withSignal({ method: 'POST', body: fd }, signal));
}

/** URL вложения/превью. */
export function attachmentUrl(id) {
  return `/api/attachments/${encodeURIComponent(id)}`;
}

export function attachmentThumbUrl(id) {
  return `/api/attachments/${encodeURIComponent(id)}/thumb`;
}

export const api = {
  DEFAULT_PAGE_SIZE,
  DEFAULT_SORT_BY,
  DEFAULT_SORT_DIR,
  ApiError,
  getHealth,
  getCaptcha,
  listComments,
  getComment,
  previewText,
  createComment,
  attachmentUrl,
  attachmentThumbUrl,
};

export default api;
