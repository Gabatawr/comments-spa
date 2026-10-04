import { Component, ElementRef, effect, inject, signal, viewChild } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { CommentsStore } from '../../core/comments.store';
import { formatBytes } from '../../core/format';
import { ApiError, CommentDto, ValidationErrors } from '../../core/models';
import { isSafeHref, sanitizeHtml } from '../../core/sanitize';
import { ToastService } from '../../core/toast.service';
import { AttachmentViewerService } from '../../core/viewer.service';
import {
  AttachmentCheck,
  LIMITS,
  validateAll,
  validateAttachmentFile,
} from '../../core/validation';

type FieldKey = 'userName' | 'email' | 'homePage' | 'text' | 'captcha' | 'attachment';

const SERVER_FIELD_MAP: Readonly<Record<string, FieldKey>> = Object.freeze({
  userName: 'userName',
  email: 'email',
  homePage: 'homePage',
  text: 'text',
  captcha: 'captcha',
  attachment: 'attachment',
});

const FIELD_INPUT_IDS: Readonly<Record<string, string>> = Object.freeze({
  userName: 'userName',
  email: 'email',
  homePage: 'homePage',
  text: 'text',
  captcha: 'captchaAnswer',
  attachment: 'attachment',
});

/** Форма добавления комментария/ответа: CAPTCHA, вложения, toolbar, AJAX-предпросмотр. */
@Component({
  selector: 'app-comment-form',
  templateUrl: './comment-form.html',
})
export class CommentFormComponent {
  private readonly api = inject(ApiService);
  private readonly store = inject(CommentsStore);
  private readonly toast = inject(ToastService);
  private readonly viewer = inject(AttachmentViewerService);
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly textArea = viewChild<ElementRef<HTMLTextAreaElement>>('textArea');
  private readonly fileInput = viewChild<ElementRef<HTMLInputElement>>('fileInput');

  readonly LIMITS = LIMITS;
  readonly formatBytes = formatBytes;
  readonly replyTarget = this.store.replyTarget;

  readonly userName = signal('');
  readonly email = signal('');
  readonly homePage = signal('');
  readonly text = signal('');
  readonly captchaAnswer = signal('');

  readonly captchaId = signal<string | null>(null);
  readonly captchaImage = signal('');
  readonly captchaStatus = signal('');
  readonly captchaError = signal(false);
  readonly captchaLoading = signal(false);

  readonly attachment = signal<File | null>(null);
  readonly attachmentCheck = signal<AttachmentCheck>({ ok: true, kind: null, error: null });
  readonly attachmentPreviewUrl = signal<string | null>(null);
  readonly attachmentDim = signal<{ width: number; height: number } | null>(null);

  readonly fieldErrors = signal<Record<string, string>>({});
  readonly generalError = signal('');
  readonly submitting = signal(false);

  readonly previewHtml = signal('');
  readonly previewValid = signal(false);
  readonly previewErrors = signal<readonly string[]>([]);
  readonly previewLoading = signal(false);
  readonly previewTouched = signal(false);

  private attachmentGeneration = 0;

  constructor() {
    // Reply prefill: прокрутка к форме и фокус в textarea при клике «Ответить».
    effect(() => {
      const target = this.store.replyTarget();
      if (!target) {
        return;
      }
      queueMicrotask(() => {
        this.host.nativeElement.scrollIntoView({ behavior: 'smooth', block: 'start' });
        this.textArea()?.nativeElement.focus();
      });
    });
    void this.loadCaptcha();
  }

  /* ------------------------------ fields ------------------------------ */

  hasError(field: string): boolean {
    return Boolean(this.fieldErrors()[field]);
  }

  error(field: string): string {
    return this.fieldErrors()[field] ?? '';
  }

  onFieldInput(field: FieldKey, event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    switch (field) {
      case 'userName':
        this.userName.set(value);
        break;
      case 'email':
        this.email.set(value);
        break;
      case 'homePage':
        this.homePage.set(value);
        break;
      case 'attachment':
        break;
      default:
        break;
    }
    this.clearError(field);
  }

  onCaptchaInput(event: Event): void {
    this.captchaAnswer.set((event.target as HTMLInputElement).value);
    this.clearError('captcha');
  }

  onTextInput(event: Event): void {
    this.text.set((event.target as HTMLTextAreaElement).value);
    this.clearError('text');
  }

  onTextKeydown(event: KeyboardEvent): void {
    if (!(event.ctrlKey || event.metaKey) || event.altKey) {
      return;
    }
    const key = event.key.toLowerCase();
    if (key === 'b') {
      event.preventDefault();
      this.applyTag('strong');
    } else if (key === 'i') {
      event.preventDefault();
      this.applyTag('i');
    } else if (key === 'k') {
      event.preventDefault();
      this.applyTag('a');
    } else if (event.key === 'Enter') {
      event.preventDefault();
      void this.runPreview();
    }
  }

  /* ------------------------------ toolbar ------------------------------ */

  applyTag(tag: 'i' | 'strong' | 'code' | 'a'): void {
    if (tag === 'a') {
      this.insertLink();
      return;
    }
    const spec: Record<'i' | 'strong' | 'code', [string, string]> = {
      i: ['<i>', '</i>'],
      strong: ['<strong>', '</strong>'],
      code: ['<code>', '</code>'],
    };
    const [open, close] = spec[tag];
    this.wrapSelection(open, close);
  }

  private insertLink(): void {
    const textarea = this.textArea()?.nativeElement;
    if (!textarea) {
      return;
    }
    const selected = textarea.value.slice(textarea.selectionStart ?? 0, textarea.selectionEnd ?? 0);
    const rawHref = window.prompt('Адрес ссылки (href):', 'https://');
    if (rawHref == null) {
      return; // отмена
    }
    const href = rawHref.trim();
    if (!href) {
      this.toast.info('Адрес ссылки не может быть пустым.', { timeout: 2500 });
      return;
    }
    if (!isSafeHref(href)) {
      this.toast.info('Недопустимый адрес: запрещены схемы javascript:, data:, vbscript:.', {
        timeout: 4000,
      });
      return;
    }
    const rawTitle = window.prompt('Заголовок ссылки (title, можно оставить пустым):', '');
    const title = rawTitle == null ? '' : String(rawTitle);
    const open = title
      ? `<a href="${escapeAttr(href)}" title="${escapeAttr(title)}">`
      : `<a href="${escapeAttr(href)}">`;
    this.wrapSelection(open, '</a>');
    void selected;
  }

  private wrapSelection(open: string, close: string): void {
    const textarea = this.textArea()?.nativeElement;
    if (!textarea) {
      return;
    }
    const value = textarea.value;
    const start = textarea.selectionStart ?? value.length;
    const end = textarea.selectionEnd ?? start;
    const selected = value.slice(start, end);
    textarea.value = value.slice(0, start) + open + selected + close + value.slice(end);
    if (selected) {
      textarea.selectionStart = start + open.length;
      textarea.selectionEnd = start + open.length + selected.length;
    } else {
      const position = start + open.length;
      textarea.selectionStart = position;
      textarea.selectionEnd = position;
    }
    textarea.focus();
    this.text.set(textarea.value);
    this.clearError('text');
  }

  /* ------------------------------ captcha ------------------------------ */

  async loadCaptcha(): Promise<void> {
    if (this.captchaLoading()) {
      return;
    }
    this.captchaLoading.set(true);
    this.captchaImage.set('');
    this.captchaId.set(null);
    try {
      const data = await firstValueFrom(this.api.getCaptcha());
      if (!data?.captchaId || !data.image) {
        throw new Error('Некорректный ответ CAPTCHA');
      }
      this.captchaId.set(data.captchaId);
      this.captchaImage.set(data.image);
      this.captchaError.set(false);
      this.captchaStatus.set(
        `Код действует ${Math.round((data.expiresInSeconds || 300) / 60)} мин.`,
      );
    } catch {
      this.captchaError.set(true);
      this.captchaStatus.set('Не удалось загрузить CAPTCHA. Нажмите «Обновить».');
      this.toast.error('Не удалось загрузить CAPTCHA. Попробуйте ещё раз.');
    } finally {
      this.captchaLoading.set(false);
    }
  }

  /* ---------------------------- attachment ---------------------------- */

  onAttachmentChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files && input.files[0] ? input.files[0] : null;
    this.attachmentGeneration += 1;
    const generation = this.attachmentGeneration;
    this.revokePreviewUrl();
    this.attachmentPreviewUrl.set(null);
    this.attachmentDim.set(null);
    this.attachment.set(file);
    const check = validateAttachmentFile(file);
    this.attachmentCheck.set(check);
    if (check.error) {
      this.setError('attachment', check.error);
    } else {
      this.clearError('attachment');
    }
    if (file && !check.error && check.kind === 'image') {
      const url = URL.createObjectURL(file);
      this.attachmentPreviewUrl.set(url);
      void readImageSize(file).then((dim) => {
        if (generation === this.attachmentGeneration && dim) {
          this.attachmentDim.set(dim);
        }
      });
    }
  }

  private revokePreviewUrl(): void {
    const url = this.attachmentPreviewUrl();
    if (url) {
      URL.revokeObjectURL(url);
    }
  }

  /** Клик по превью до отправки открывает lightbox (docs/API-v2.md §2.7). */
  openAttachmentPreview(): void {
    const url = this.attachmentPreviewUrl();
    if (url) {
      this.viewer.openLightbox(url, this.attachment()?.name || '');
    }
  }

  /* ----------------------------- preview ----------------------------- */

  async runPreview(): Promise<void> {
    this.previewTouched.set(true);
    this.previewLoading.set(true);
    this.previewErrors.set([]);
    try {
      const data = await firstValueFrom(this.api.previewText(this.text()));
      this.previewHtml.set(sanitizeHtml(data?.html ?? ''));
      this.previewValid.set(Boolean(data?.valid));
      this.previewErrors.set(data?.errors ?? []);
    } catch (error) {
      const message = error instanceof ApiError ? error.detail || error.message : String(error);
      this.previewHtml.set('');
      this.previewValid.set(false);
      this.previewErrors.set([message || 'Ошибка предпросмотра']);
    } finally {
      this.previewLoading.set(false);
    }
  }

  /* ------------------------------ submit ------------------------------ */

  async onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    this.fieldErrors.set({});
    this.generalError.set('');

    const errors: Record<string, string> = {};
    const clientErrors = validateAll({
      userName: this.userName(),
      email: this.email(),
      homePage: this.homePage(),
      text: this.text(),
      captchaAnswer: this.captchaAnswer(),
    });
    for (const [key, value] of Object.entries(clientErrors)) {
      if (value) {
        errors[key] = value;
      }
    }
    const check = validateAttachmentFile(this.attachment());
    this.attachmentCheck.set(check);
    if (!check.ok && check.error) {
      errors['attachment'] = check.error;
    }
    if (!this.captchaId() && !errors['captcha']) {
      errors['captcha'] = 'Обновите CAPTCHA.';
    }
    if (Object.keys(errors).length) {
      this.fieldErrors.set(errors);
      this.toast.error('Проверьте поля формы.');
      return;
    }

    const captchaId = this.captchaId();
    if (!captchaId) {
      return;
    }

    this.submitting.set(true);
    try {
      const response = await firstValueFrom(
        this.api.createComment(
          {
            userName: this.userName(),
            email: this.email(),
            homePage: this.homePage(),
            text: this.text(),
            captchaId,
            captchaAnswer: this.captchaAnswer(),
            parentId: this.store.replyTarget()?.id ?? null,
          },
          this.attachment(),
        ),
      );
      const created: CommentDto | null = response?.comment ?? null;
      this.toast.success(created?.parentId != null ? 'Ответ добавлен.' : 'Комментарий добавлен.');
      this.resetForm();
      await this.store.afterCreate(created);
    } catch (error) {
      await this.handleSubmitError(error);
    } finally {
      this.submitting.set(false);
    }
  }

  private async handleSubmitError(error: unknown): Promise<void> {
    const apiError = error instanceof ApiError ? error : null;
    if (apiError?.status === 400 && apiError.errors) {
      this.applyServerErrors(apiError.errors);
      this.toast.error('Сервер отклонил данные: исправьте ошибки в форме.');
      if (apiError.errors['captcha']) {
        await this.loadCaptcha();
      }
      return;
    }
    if (apiError?.status === 0) {
      this.toast.error('Сервер недоступен. Проверьте соединение и повторите попытку.');
      return;
    }
    const message = apiError?.detail || apiError?.message || 'Произошла ошибка. Попробуйте ещё раз.';
    this.generalError.set(message);
    this.toast.error(message);
  }

  private applyServerErrors(errors: ValidationErrors): void {
    const mapped: Record<string, string> = {};
    let general: string[] = [];
    let firstField: string | null = null;
    for (const key of Object.keys(errors)) {
      const raw = errors[key];
      const message = Array.isArray(raw) ? raw.join(' ') : String(raw ?? '');
      if (!message) {
        continue;
      }
      const field = SERVER_FIELD_MAP[key];
      if (field) {
        mapped[field] = mapped[field] ? `${mapped[field]} ${message}` : message;
        if (!firstField) {
          firstField = field;
        }
      } else {
        general = [...general, message];
      }
    }
    this.fieldErrors.set(mapped);
    if (general.length) {
      this.generalError.set(general.join(' '));
    }
    if (firstField) {
      const inputId = FIELD_INPUT_IDS[firstField] ?? firstField;
      const element = document.getElementById(inputId);
      (element ?? this.textArea()?.nativeElement)?.focus();
    }
  }

  private resetForm(): void {
    this.userName.set('');
    this.email.set('');
    this.homePage.set('');
    this.text.set('');
    this.captchaAnswer.set('');
    const textarea = this.textArea()?.nativeElement;
    if (textarea) {
      textarea.value = '';
    }
    const fileInput = this.fileInput()?.nativeElement;
    if (fileInput) {
      fileInput.value = '';
    }
    this.revokePreviewUrl();
    this.attachment.set(null);
    this.attachmentCheck.set({ ok: true, kind: null, error: null });
    this.attachmentPreviewUrl.set(null);
    this.attachmentDim.set(null);
    this.attachmentGeneration += 1;
    this.fieldErrors.set({});
    this.generalError.set('');
    this.previewHtml.set('');
    this.previewValid.set(false);
    this.previewErrors.set([]);
    this.previewTouched.set(false);
    this.store.cancelReply();
    void this.loadCaptcha();
  }

  private setError(field: string, message: string): void {
    this.fieldErrors.update((current) => ({ ...current, [field]: message }));
  }

  private clearError(field: string): void {
    if (!this.fieldErrors()[field]) {
      return;
    }
    this.fieldErrors.update((current) => {
      const next = { ...current };
      delete next[field];
      return next;
    });
  }

  cancelReply(): void {
    this.store.cancelReply();
  }
}

function escapeAttr(value: string): string {
  return String(value ?? '')
    .replace(/&/g, '&amp;')
    .replace(/"/g, '&quot;');
}

function readImageSize(file: File): Promise<{ width: number; height: number } | null> {
  return new Promise((resolve) => {
    const url = URL.createObjectURL(file);
    const probe = new Image();
    probe.onload = () => {
      URL.revokeObjectURL(url);
      resolve({ width: probe.naturalWidth, height: probe.naturalHeight });
    };
    probe.onerror = () => {
      URL.revokeObjectURL(url);
      resolve(null);
    };
    probe.src = url;
  });
}
