/**
 * api.service.ts — типизированная обёртка над REST API v2 (docs/API-v2.md §2, §3.1).
 * Один origin: nginx проксирует /api/* в контейнер api.
 */

import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';
import {
  ApiError,
  CaptchaResponse,
  CommentDto,
  CommentFormFields,
  CommentPageDto,
  CreateCommentResponse,
  HealthResponse,
  PreviewResponse,
  SortBy,
  SortDir,
} from './models';

export const DEFAULT_PAGE_SIZE = 25;
export const DEFAULT_SORT_BY: SortBy = 'createdAt';
export const DEFAULT_SORT_DIR: SortDir = 'desc';
export const SORT_FIELDS: readonly SortBy[] = Object.freeze(['createdAt', 'userName', 'email']);

export interface ListCommentsParams {
  page?: number;
  pageSize?: number;
  sortBy?: SortBy;
  sortDir?: SortDir;
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  getHealth(): Observable<HealthResponse> {
    return this.http
      .get<HealthResponse>('/api/health')
      .pipe(catchError((error: unknown) => this.fail(error)));
  }

  getCaptcha(): Observable<CaptchaResponse> {
    return this.http
      .get<CaptchaResponse>('/api/captcha')
      .pipe(catchError((error: unknown) => this.fail(error)));
  }

  listComments(params: ListCommentsParams = {}): Observable<CommentPageDto> {
    const query = new HttpParams()
      .set('page', String(params.page ?? 1))
      .set('pageSize', String(params.pageSize ?? DEFAULT_PAGE_SIZE))
      .set('sortBy', params.sortBy ?? DEFAULT_SORT_BY)
      .set('sortDir', params.sortDir ?? DEFAULT_SORT_DIR);
    return this.http
      .get<CommentPageDto>('/api/comments', { params: query })
      .pipe(catchError((error: unknown) => this.fail(error)));
  }

  getComment(id: number): Observable<CommentDto> {
    return this.http
      .get<CommentDto>(`/api/comments/${encodeURIComponent(String(id))}`)
      .pipe(catchError((error: unknown) => this.fail(error)));
  }

  previewText(text: string): Observable<PreviewResponse> {
    return this.http
      .post<PreviewResponse>('/api/preview', { text: text ?? '' })
      .pipe(catchError((error: unknown) => this.fail(error)));
  }

  /** multipart/form-data, имена полей строго по docs/API-v2.md §2.5. */
  createComment(fields: CommentFormFields, file: File | null): Observable<CreateCommentResponse> {
    const body = new FormData();
    body.append('userName', fields.userName ?? '');
    body.append('email', fields.email ?? '');
    body.append('homePage', fields.homePage ?? '');
    body.append('text', fields.text ?? '');
    body.append('captchaId', fields.captchaId ?? '');
    body.append('captchaAnswer', fields.captchaAnswer ?? '');
    if (fields.parentId != null) {
      body.append('parentId', String(fields.parentId));
    }
    if (file) {
      body.append('attachment', file, file.name || 'attachment');
    }
    return this.http
      .post<CreateCommentResponse>('/api/comments', body)
      .pipe(catchError((error: unknown) => this.fail(error)));
  }

  /** URL вложения / превью (docs/API-v2.md §2.7). */
  attachmentUrl(id: number): string {
    return `/api/attachments/${encodeURIComponent(String(id))}`;
  }

  attachmentThumbUrl(id: number): string {
    return `/api/attachments/${encodeURIComponent(String(id))}/thumb`;
  }

  private fail(error: unknown): Observable<never> {
    return throwError(() => toApiError(error));
  }
}

export function toApiError(error: unknown): ApiError {
  if (error instanceof ApiError) {
    return error;
  }
  if (error instanceof HttpErrorResponse) {
    return new ApiError(error.status, error.error, error.statusText);
  }
  const message = error instanceof Error ? error.message : String(error ?? '');
  return new ApiError(0, null, message);
}
