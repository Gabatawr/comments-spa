import { NgTemplateOutlet } from '@angular/common';
import { Component, inject, input, output, signal } from '@angular/core';
import { BookmarkService } from '../../core/bookmarks.service';
import { avatarHue, formatBytes, formatDate, initials, safeHomePage } from '../../core/format';
import { AttachmentDto, CommentDto } from '../../core/models';
import { sanitizeHtml } from '../../core/sanitize';
import { AttachmentViewerService } from '../../core/viewer.service';

/**
 * Каскадный узел дерева комментариев: карточка с шапкой (аватар-инициалы,
 * имя, дата, 4 иконки, декоративное голосование), телом, врезкой-цитатой
 * (DESIGN-v2.1 §1.2) и рекурсивным блоком дочерних ответов (отступ 32px,
 * линия вложенности 2px).
 *
 * Все узлы дерева рендерятся одним экземпляром компонента через
 * `ngTemplateOutlet`, поэтому состояние (открытая карточка автора) хранится
 * не булевым флагом, а id текущего комментария.
 */
@Component({
  selector: 'app-comment-node',
  imports: [NgTemplateOutlet],
  templateUrl: './comment-node.html',
})
export class CommentNodeComponent {
  readonly comment = input.required<CommentDto>();
  readonly depth = input<number>(0);
  readonly reply = output<CommentDto>();

  readonly MAX_DEPTH = 50;
  readonly formatDate = formatDate;
  readonly formatBytes = formatBytes;
  readonly safeHomePage = safeHomePage;
  readonly avatarHue = avatarHue;
  readonly initials = initials;

  /** Ид комментария, у которого открыта всплывающая карточка автора (иконка 4). */
  readonly openInfoId = signal<number | null>(null);

  private readonly viewer = inject(AttachmentViewerService);
  private readonly bookmarkService = inject(BookmarkService);

  /** Закладки: сигнал сервиса, чтобы Angular отслеживал изменения в шаблоне. */
  readonly bookmarks = this.bookmarkService.ids;

  /** Единственное место рендера HTML — уже санитизированный сервером текст. */
  safeText(html: string): string {
    return sanitizeHtml(html);
  }

  thumbSrc(att: AttachmentDto): string {
    return att.thumbUrl || `/api/attachments/${att.id}/thumb`;
  }

  fullSrc(att: AttachmentDto): string {
    return att.url || `/api/attachments/${att.id}`;
  }

  openImage(att: AttachmentDto): void {
    this.viewer.openLightbox(this.fullSrc(att), att.fileName || '');
  }

  openText(att: AttachmentDto): void {
    void this.viewer.openText(this.fullSrc(att), att.fileName || 'file.txt');
  }

  onReply(comment: CommentDto): void {
    this.reply.emit(comment);
  }

  /** Иконка 1 (`#`): якорь `#comment-<id>` в URL без перезагрузки + скролл. */
  onAnchor(id: number): void {
    const target = typeof document === 'undefined' ? null : document.getElementById(`comment-${id}`);
    target?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    if (typeof history !== 'undefined' && typeof history.replaceState === 'function') {
      history.replaceState(null, '', `#comment-${id}`);
    }
  }

  /** Иконка 2: клиентская закладка в localStorage. */
  toggleBookmark(id: number): void {
    this.bookmarkService.toggle(id);
  }

  /** Иконка 4: открыть/закрыть всплывающую карточку автора. */
  toggleInfo(id: number): void {
    this.openInfoId.update((current) => (current === id ? null : id));
  }
}
