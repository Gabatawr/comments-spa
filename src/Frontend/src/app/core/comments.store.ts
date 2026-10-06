/**
 * comments.store.ts — состояние списка корневых комментариев: сортировка, пагинация по 25,
 * дерево ответов, live-вставка событий WebSocket.
 *
 * Пагинация гибридная (docs/API-v2.md §4.5): переходы на соседние страницы идут по keyset-курсору,
 * а прыжок по номеру и возврат к первой странице — обычным OFFSET. На стотысячном наборе корней
 * `OFFSET 100000` заставляет PostgreSQL пройти и отсортировать всё до окна; курсор продолжает
 * выборку с последней строки предыдущей страницы, то есть по индексу.
 */

import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiService, DEFAULT_PAGE_SIZE } from './api.service';
import { CommentDto, CommentPageDto, SortBy, SortDir } from './models';
import { ToastService } from './toast.service';

@Injectable({ providedIn: 'root' })
export class CommentsStore {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);

  readonly pageSize = DEFAULT_PAGE_SIZE;
  readonly items = signal<readonly CommentDto[]>([]);
  readonly page = signal(1);
  readonly sortBy = signal<SortBy>('createdAt');
  readonly sortDir = signal<SortDir>('desc');
  readonly totalItems = signal(0);
  readonly totalPages = signal(0);
  readonly loading = signal(false);
  readonly loaded = signal(false);
  readonly error = signal('');
  /** Комментарий, на который сейчас отвечают; null — обычный корневой комментарий. */
  readonly replyTarget = signal<CommentDto | null>(null);

  /** Курсор текущей страницы; null — страница запрошена по OFFSET. */
  readonly cursor = signal<string | null>(null);

  /** Курсор начала следующей страницы, как его отдал сервер. */
  readonly nextCursor = signal<string | null>(null);

  /** Идёт ли текущая страница по курсору — состояние видно в разметке и в проверках. */
  readonly usingCursor = computed(() => this.cursor() !== null);

  /** Пройденные курсоры, чтобы «‹» возвращал ровно ту страницу, с которой пришли. */
  private cursorHistory: (string | null)[] = [];

  readonly statusText = computed(() => {
    if (this.error()) {
      return this.error();
    }
    const total = this.totalItems();
    if (!total) {
      return this.loaded() ? 'Комментариев пока нет.' : '';
    }
    const from = (this.page() - 1) * this.pageSize + 1;
    const to = Math.min(this.page() * this.pageSize, total);
    return `Показано ${from}–${to} из ${total}`;
  });

  private requestSeq = 0;
  private suppressRealtimeUntil = 0;

  isDefaultSort(): boolean {
    return this.sortBy() === 'createdAt' && this.sortDir() === 'desc';
  }

  async load(): Promise<void> {
    const seq = ++this.requestSeq;
    this.loading.set(true);
    this.error.set('');
    try {
      const data = await firstValueFrom(
        this.api.listComments({
          page: this.page(),
          pageSize: this.pageSize,
          sortBy: this.sortBy(),
          sortDir: this.sortDir(),
          cursor: this.cursor() ?? undefined,
        }),
      );
      if (seq !== this.requestSeq) {
        return; // устаревший ответ
      }
      this.applyPage(data);
      this.loaded.set(true);
    } catch (error) {
      if (seq !== this.requestSeq) {
        return;
      }
      const message = errorMessage(error, 'Не удалось загрузить комментарии');
      this.error.set(message);
      this.toast.error(message);
    } finally {
      if (seq === this.requestSeq) {
        this.loading.set(false);
      }
    }
  }

  refresh(): Promise<void> {
    return this.load();
  }

  setSort(field: SortBy): Promise<void> {
    if (field === this.sortBy()) {
      this.sortDir.set(this.sortDir() === 'asc' ? 'desc' : 'asc');
    } else {
      this.sortBy.set(field);
      this.sortDir.set(field === 'createdAt' ? 'desc' : 'asc');
    }
    this.resetPaging();
    return this.load();
  }

  /**
   * Навигация по страницам. Соседняя страница берётся по курсору, произвольный прыжок по номеру —
   * по OFFSET; после прыжка последовательная навигация начинается заново (docs/API-v2.md §4.5).
   */
  goToPage(page: number): Promise<void> {
    const target = Math.max(1, Number(page) || 1);
    if (target === this.page() && this.items().length) {
      return Promise.resolve();
    }

    if (target === this.page() + 1 && this.nextCursor()) {
      this.cursorHistory.push(this.cursor());
      this.cursor.set(this.nextCursor());
    } else if (target === this.page() - 1 && this.cursorHistory.length > 0) {
      this.cursor.set(this.cursorHistory.pop() ?? null);
    } else {
      this.resetPaging();
    }

    this.page.set(target);
    return this.load();
  }

  private resetPaging(): void {
    this.cursorHistory = [];
    this.cursor.set(null);
    this.nextCursor.set(null);
    this.page.set(1);
  }

  requestReply(comment: CommentDto): void {
    this.replyTarget.set(comment);
  }

  cancelReply(): void {
    this.replyTarget.set(null);
  }

  /** Вызывается формой после успешного POST /api/comments. */
  afterCreate(created: CommentDto | null): Promise<void> {
    this.suppressRealtimeUntil = Date.now() + 2000; // своё событие уже обработано
    if (!created || created.parentId == null) {
      if (this.page() !== 1) {
        return this.goToPage(1);
      }
      return this.refresh();
    }
    return this.refresh();
  }

  /** Live-вставка события `comment.created` (docs/API-v2.md §2.9). */
  handleRealtime(comment: CommentDto | null | undefined): void {
    if (!comment || typeof comment.id !== 'number') {
      return;
    }
    if (Date.now() < this.suppressRealtimeUntil) {
      return; // собственный комментарий уже добавлен через HTTP-ответ
    }

    const isReply = comment.parentId != null;
    const inserted = isReply ? this.insertReply(comment) : this.insertRoot(comment);

    if (!isReply) {
      this.toast.info(`Новый комментарий от ${comment.userName || 'пользователя'}`, {
        testId: 'ws-toast',
        actionLabel: 'Показать',
        action: () => {
          void this.goToPage(1);
        },
      });
      return;
    }
    this.toast.info(`Новый ответ от ${comment.userName || 'пользователя'}`, {
      testId: 'ws-toast',
      actionLabel: inserted ? undefined : 'Показать',
      action: inserted
        ? undefined
        : () => {
            void this.refresh();
          },
    });
  }

  findComment(id: number): CommentDto | null {
    let found: CommentDto | null = null;
    const walk = (nodes: readonly CommentDto[]) => {
      for (const node of nodes) {
        if (found) {
          return;
        }
        if (node.id === id) {
          found = node;
          return;
        }
        walk(node.replies ?? []);
      }
    };
    walk(this.items());
    return found;
  }

  containsComment(id: number): boolean {
    return this.findComment(id) !== null;
  }

  private applyPage(data: CommentPageDto): void {
    this.page.set(Number(data.page) || this.page());
    this.sortBy.set(data.sortBy ?? this.sortBy());
    this.sortDir.set(data.sortDir ?? this.sortDir());
    this.totalItems.set(Number(data.totalItems) || 0);
    this.totalPages.set(Number(data.totalPages) || 0);
    this.nextCursor.set(data.nextCursor ?? null);
    this.items.set(Array.isArray(data.items) ? data.items : []);
  }

  /** Вставляет новый корень, если он попадает в текущее представление. */
  private insertRoot(comment: CommentDto): boolean {
    if (this.page() !== 1 || !this.isDefaultSort()) {
      return false;
    }
    const list = [comment, ...this.items().filter((item) => item.id !== comment.id)];
    const pageSize = this.pageSize;
    this.items.set(list.slice(0, pageSize));
    const total = this.totalItems() + 1;
    this.totalItems.set(total);
    this.totalPages.set(Math.ceil(total / pageSize));
    return true;
  }

  /** Вставляет ответ в дерево, если родитель уже загружен. */
  private insertReply(comment: CommentDto): boolean {
    const parentId = comment.parentId;
    if (parentId == null) {
      return false;
    }
    if (!this.containsComment(parentId)) {
      return false;
    }
    this.items.update((roots) => roots.map((root) => this.insertIntoTree(root, comment, parentId)));
    return true;
  }

  private insertIntoTree(node: CommentDto, comment: CommentDto, parentId: number): CommentDto {
    if (node.id === parentId) {
      const replies = [...(node.replies ?? []), comment].sort(compareAsc);
      return { ...node, replies, replyCount: Math.max(node.replyCount ?? 0, replies.length) };
    }
    if (!node.replies?.length) {
      return node;
    }
    let changed = false;
    const replies = node.replies.map((child) => {
      const next = this.insertIntoTree(child, comment, parentId);
      if (next !== child) {
        changed = true;
      }
      return next;
    });
    return changed ? { ...node, replies } : node;
  }
}

function compareAsc(a: CommentDto, b: CommentDto): number {
  const at = new Date(a.createdAt).getTime();
  const bt = new Date(b.createdAt).getTime();
  if (at !== bt) {
    return at - bt;
  }
  return a.id - b.id;
}

function errorMessage(error: unknown, fallback: string): string {
  const detail =
    (error as { detail?: string; message?: string; title?: string } | null)?.detail ||
    (error as { message?: string } | null)?.message ||
    (error as { title?: string } | null)?.title;
  return detail || fallback;
}
