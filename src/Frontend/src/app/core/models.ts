/**
 * DTO-контракт SPA ↔ API v2 (docs/API-v2.md §1, §2, §3, §5).
 * Типы повторяют JSON-схему сервера без изменений.
 */

export type SortBy = 'createdAt' | 'userName' | 'email';
export type SortDir = 'asc' | 'desc';
export type AttachmentKind = 'image' | 'text';

export interface AttachmentDto {
  id: number;
  fileName: string;
  contentType: string;
  kind: AttachmentKind;
  sizeBytes: number;
  width: number | null;
  height: number | null;
  url: string;
  thumbUrl: string | null;
}

export interface CommentDto {
  id: number;
  parentId: number | null;
  userName: string;
  email: string;
  homePage: string | null;
  /** Уже санитизированный сервером HTML (allow-list: a[href,title], code, i, strong). */
  text: string;
  textPlain: string;
  /**
   * Снимок плоского текста родителя на момент создания ответа
   * (DESIGN-v2.1 §1, аддитивное поле; у корней и старых записей — null).
   * Выводится ТОЛЬКО интерполяцией, без innerHTML.
   */
  quotedText: string | null;
  createdAt: string;
  clientIp: string | null;
  userAgent: string | null;
  attachment: AttachmentDto | null;
  replyCount: number;
  replies: CommentDto[];
}

export interface CommentPageDto {
  items: CommentDto[];
  page: number;
  pageSize: number;
  totalItems: number;
  totalPages: number;
  sortBy: SortBy;
  sortDir: SortDir;
  nextCursor?: string | null;
}

export interface CreateCommentResponse {
  comment: CommentDto | null;
}

export interface PreviewResponse {
  valid: boolean;
  html: string;
  plain: string;
  errors: string[];
}

export interface CaptchaResponse {
  captchaId: string;
  /** data:image/png;base64,… */
  image: string;
  expiresInSeconds: number;
}

export interface HealthResponse {
  status?: string;
  database?: string;
  cache?: string;
  redis?: string;
  broker?: string;
  search?: string;
  storage?: string;
  version?: string;
  queue?: { pending: number; processed: number };
  websocket?: { clients: number };
}

/** Поля multipart-формы POST /api/comments (docs/API-v2.md §2.5). */
export interface CommentFormFields {
  userName: string;
  email: string;
  homePage: string;
  text: string;
  captchaId: string;
  captchaAnswer: string;
  parentId: number | null;
}

/** Тело ошибки валидации v1/v2: { title, status, errors } (docs/API-v2.md §0). */
export type ValidationErrors = Readonly<Record<string, readonly string[]>>;

/** Нормализованная ошибка API одинаковой формы для 400/404/5xx/сети. */
export class ApiError extends Error {
  readonly status: number;
  readonly title: string | null;
  readonly detail: string | null;
  readonly errors: ValidationErrors | null;

  constructor(status: number, body: unknown, statusText = '') {
    const parsed = ApiError.parseBody(body);
    super(parsed.detail || parsed.title || statusText || `HTTP ${status}`);
    this.name = 'ApiError';
    this.status = status;
    this.title = parsed.title;
    this.detail = parsed.detail;
    this.errors = parsed.errors;
  }

  private static parseBody(body: unknown): {
    title: string | null;
    detail: string | null;
    errors: ValidationErrors | null;
  } {
    if (body == null) {
      return { title: null, detail: null, errors: null };
    }
    if (typeof body === 'string') {
      const text = body.trim();
      return { title: null, detail: text || null, errors: null };
    }
    if (typeof body === 'object') {
      const record = body as Record<string, unknown>;
      const title = typeof record['title'] === 'string' ? record['title'] : null;
      const detail = typeof record['detail'] === 'string' ? record['detail'] : null;
      const rawErrors = record['errors'];
      let errors: ValidationErrors | null = null;
      if (rawErrors && typeof rawErrors === 'object') {
        const map: Record<string, string[]> = {};
        for (const key of Object.keys(rawErrors as Record<string, unknown>)) {
          const value = (rawErrors as Record<string, unknown>)[key];
          if (Array.isArray(value)) {
            map[key] = value.map((item) => String(item));
          } else if (value != null) {
            map[key] = [String(value)];
          }
        }
        errors = Object.keys(map).length ? map : null;
      }
      return { title, detail, errors };
    }
    return { title: null, detail: String(body), errors: null };
  }
}
