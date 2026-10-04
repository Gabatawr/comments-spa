import { Component, computed, inject } from '@angular/core';
import { CommentsStore } from '../../core/comments.store';
import { SortBy } from '../../core/models';
import { CommentNodeComponent } from '../comment-node/comment-node';

/**
 * Таблица корневых комментариев: сортировка asc/desc по User Name / E-mail / дате,
 * пагинация по 25.
 *
 * Строка таблицы — сам корневой комментарий, отрисованный карточкой по образцу ТЗ;
 * вложенные ответы раскрываются рекурсивно внутри него. Тумблера «свернуть» нет:
 * скрывать нечего, а дубль «строка таблицы + карточка того же корня» устранён.
 */
@Component({
  selector: 'app-comments-list',
  imports: [CommentNodeComponent],
  templateUrl: './comments-list.html',
})
export class CommentsListComponent {
  readonly store = inject(CommentsStore);

  readonly pages = computed<(number | 'gap')[]>(() => {
    const total = this.store.totalPages();
    const current = this.store.page();
    if (total <= 1) {
      return [];
    }
    const set = new Set<number>([1, total, current, current - 1, current + 1]);
    if (current <= 3) {
      [2, 3, 4].forEach((p) => set.add(p));
    }
    if (current >= total - 2) {
      [total - 1, total - 2, total - 3].forEach((p) => set.add(p));
    }
    const sorted = [...set].filter((p) => p >= 1 && p <= total).sort((a, b) => a - b);
    const out: (number | 'gap')[] = [];
    let previous = 0;
    for (const page of sorted) {
      if (previous && page - previous > 1) {
        out.push('gap');
      }
      out.push(page);
      previous = page;
    }
    return out;
  });

  isSorted(field: SortBy): boolean {
    return this.store.sortBy() === field;
  }

  sortArrow(field: SortBy): string {
    if (!this.isSorted(field)) {
      return '';
    }
    return this.store.sortDir() === 'asc' ? '▲' : '▼';
  }

  ariaSort(field: SortBy): 'ascending' | 'descending' | 'none' {
    if (!this.isSorted(field)) {
      return 'none';
    }
    return this.store.sortDir() === 'asc' ? 'ascending' : 'descending';
  }

  onHeadKey(event: KeyboardEvent, field: SortBy): void {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      void this.store.setSort(field);
    }
  }

  goPage(page: number | 'gap'): void {
    if (page === 'gap') {
      return;
    }
    void this.store.goToPage(page);
  }
}
