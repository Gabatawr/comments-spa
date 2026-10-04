/**
 * bookmarks.service.ts — клиентские закладки комментариев (DESIGN-v2.1 §2.1, иконка 2).
 *
 * Состояние живёт только в localStorage (ключ `comments:bookmarks`), на бэкенд
 * не уходит. Сигнал общий для всех экземпляров каскада, поэтому переключение
 * закладки в одном дереве сразу видно и в остальных.
 */

import { Injectable, signal } from '@angular/core';

const STORAGE_KEY = 'comments:bookmarks';

@Injectable({ providedIn: 'root' })
export class BookmarkService {
  /** Множество id отмеченных комментариев. */
  readonly ids = signal<ReadonlySet<number>>(readBookmarks());

  has(id: number): boolean {
    return this.ids().has(id);
  }

  toggle(id: number): boolean {
    const next = new Set(this.ids());
    const active = !next.has(id);
    if (active) {
      next.add(id);
    } else {
      next.delete(id);
    }
    this.ids.set(next);
    writeBookmarks(next);
    return active;
  }
}

function readBookmarks(): ReadonlySet<number> {
  if (typeof localStorage === 'undefined') {
    return new Set<number>();
  }
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) {
      return new Set<number>();
    }
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) {
      return new Set<number>();
    }
    const ids = parsed
      .map((value) => Number(value))
      .filter((value) => Number.isFinite(value));
    return new Set(ids);
  } catch {
    return new Set<number>();
  }
}

function writeBookmarks(ids: ReadonlySet<number>): void {
  if (typeof localStorage === 'undefined') {
    return;
  }
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify([...ids]));
  } catch {
    /* приватный режим/квота — закладки просто не сохранятся */
  }
}
