/**
 * viewer.service.ts — lightbox для изображений и модальное окно просмотра TXT.
 * Содержимое TXT вставляется только через textContent (шаблон {{ }}),
 * никогда через innerHTML.
 */

import { Injectable, signal } from '@angular/core';

export interface LightboxState {
  src: string;
  caption: string;
}

export interface TextModalState {
  title: string;
  url: string;
  body: string;
  loading: boolean;
}

@Injectable({ providedIn: 'root' })
export class AttachmentViewerService {
  readonly lightbox = signal<LightboxState | null>(null);
  readonly textModal = signal<TextModalState | null>(null);

  openLightbox(src: string | null | undefined, caption = ''): void {
    if (!src) {
      return;
    }
    this.lightbox.set({ src, caption });
  }

  closeLightbox(): void {
    this.lightbox.set(null);
  }

  async openText(url: string | null | undefined, fileName = 'file.txt'): Promise<void> {
    if (!url) {
      return;
    }
    this.textModal.set({ title: fileName || 'Текстовый файл', url, body: 'Загрузка…', loading: true });
    try {
      const response = await fetch(url, { headers: { Accept: 'text/plain, */*' } });
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
      const text = await response.text();
      this.textModal.update((state) => (state ? { ...state, body: text, loading: false } : state));
    } catch (error) {
      const message = error instanceof Error ? error.message : 'ошибка';
      this.textModal.update((state) =>
        state ? { ...state, body: `Не удалось загрузить файл: ${message}`, loading: false } : state,
      );
    }
  }

  closeTextModal(): void {
    this.textModal.set(null);
  }
}
