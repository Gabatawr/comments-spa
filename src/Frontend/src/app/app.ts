import { Component, OnDestroy, OnInit, computed, effect, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { CommentFormComponent } from './components/comment-form/comment-form';
import { CommentsListComponent } from './components/comments-list/comments-list';
import { ApiService } from './core/api.service';
import { CommentsStore } from './core/comments.store';
import { RealtimeService } from './core/realtime.service';
import { ToastService } from './core/toast.service';
import { AttachmentViewerService } from './core/viewer.service';

/** Корневой shell: шапка со статусами, форма, таблица, lightbox/модалка, тосты. */
@Component({
  selector: 'app-root',
  imports: [CommentFormComponent, CommentsListComponent],
  templateUrl: './app.html',
})
export class App implements OnInit, OnDestroy {
  readonly toasts = inject(ToastService);
  readonly viewer = inject(AttachmentViewerService);

  private readonly api = inject(ApiService);
  private readonly store = inject(CommentsStore);
  private readonly realtime = inject(RealtimeService);

  readonly healthLabel = signal('API: …');
  readonly healthOk = signal(false);
  readonly healthTitle = signal('');
  readonly realtimeState = this.realtime.status;
  readonly realtimeClass = computed(() => `realtime-status is-${this.realtimeState()}`);

  private healthTimer: ReturnType<typeof setInterval> | null = null;
  private readonly onKeydown = (event: KeyboardEvent) => {
    if (event.key === 'Escape') {
      this.viewer.closeLightbox();
      this.viewer.closeTextModal();
    }
  };

  readonly realtimeLabel = computed(() => {
    const labels: Record<string, string> = {
      connecting: 'WS: подключение…',
      online: 'WS: online',
      offline: 'WS: offline',
      reconnecting: 'WS: переподключение…',
      error: 'WS: ошибка',
      stopped: 'WS: остановлен',
      unsupported: 'WS: не поддерживается',
    };
    return labels[this.realtimeState()] ?? `WS: ${this.realtimeState()}`;
  });

  constructor() {
    // Блокируем прокрутку фона, пока открыт lightbox или модалка.
    effect(() => {
      const open = this.viewer.lightbox() !== null || this.viewer.textModal() !== null;
      if (typeof document !== 'undefined') {
        document.documentElement.classList.toggle('is-modal-open', open);
      }
    });
  }

  ngOnInit(): void {
    void this.checkHealth();
    this.healthTimer = setInterval(() => void this.checkHealth(), 60000);
    this.realtime.commentCreated$.subscribe((comment) => this.store.handleRealtime(comment));
    this.realtime.start();
    void this.store.load();
    if (typeof window !== 'undefined') {
      window.addEventListener('keydown', this.onKeydown);
    }
  }

  ngOnDestroy(): void {
    if (this.healthTimer) {
      clearInterval(this.healthTimer);
      this.healthTimer = null;
    }
    if (typeof window !== 'undefined') {
      window.removeEventListener('keydown', this.onKeydown);
    }
    this.realtime.stop();
  }

  onLightboxBackdrop(event: MouseEvent): void {
    if (event.target === event.currentTarget) {
      this.viewer.closeLightbox();
    }
  }

  onModalBackdrop(event: MouseEvent): void {
    if (event.target === event.currentTarget) {
      this.viewer.closeTextModal();
    }
  }

  private async checkHealth(): Promise<void> {
    try {
      const data = await firstValueFrom(this.api.getHealth());
      const ok = data?.status === 'ok';
      this.healthOk.set(ok);
      this.healthLabel.set(ok ? 'API: ok' : `API: ${data?.status ?? 'unknown'}`);
      this.healthTitle.set(JSON.stringify(data));
    } catch {
      this.healthOk.set(false);
      this.healthLabel.set('API: недоступен');
      this.healthTitle.set('');
    }
  }
}
