/** toast.service.ts — стек всплывающих уведомлений (Angular-сигналы). */

import { Injectable, signal } from '@angular/core';

export type ToastKind = 'info' | 'success' | 'error';

export interface ToastItem {
  id: number;
  kind: ToastKind;
  message: string;
  visible: boolean;
  actionLabel?: string;
  action?: () => void;
  /** Стабильный hook для e2e (например 'ws-toast'). */
  testId?: string;
}

export interface ToastOptions {
  kind?: ToastKind;
  timeout?: number;
  actionLabel?: string;
  action?: () => void;
  testId?: string;
}

@Injectable({ providedIn: 'root' })
export class ToastService {
  private counter = 0;
  readonly items = signal<readonly ToastItem[]>([]);

  info(message: string, options: ToastOptions = {}): void {
    this.push(message, { ...options, kind: 'info' });
  }

  success(message: string, options: ToastOptions = {}): void {
    this.push(message, { ...options, kind: 'success' });
  }

  error(message: string, options: ToastOptions = {}): void {
    this.push(message, { ...options, kind: 'error' });
  }

  push(message: string, options: ToastOptions = {}): void {
    const kind = options.kind ?? 'info';
    const id = ++this.counter;
    const item: ToastItem = {
      id,
      kind,
      message,
      visible: false,
      actionLabel: options.actionLabel,
      action: options.action,
      testId: options.testId,
    };
    this.items.update((list) => [...list, item]);
    // Даём браузеру кадр отрисовать стартовое состояние — потом включаем анимацию.
    setTimeout(() => {
      this.items.update((list) =>
        list.map((entry) => (entry.id === id ? { ...entry, visible: true } : entry)),
      );
    }, 16);
    const timeout = options.timeout ?? (kind === 'error' ? 8000 : 4500);
    if (timeout > 0) {
      setTimeout(() => this.dismiss(id), timeout);
    }
  }

  dismiss(id: number): void {
    this.items.update((list) =>
      list.map((entry) => (entry.id === id ? { ...entry, visible: false } : entry)),
    );
    setTimeout(() => {
      this.items.update((list) => list.filter((entry) => entry.id !== id));
    }, 250);
  }

  runAction(item: ToastItem): void {
    item.action?.();
    this.dismiss(item.id);
  }
}
