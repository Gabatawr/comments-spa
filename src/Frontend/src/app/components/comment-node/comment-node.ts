import { NgTemplateOutlet } from '@angular/common';
import { Component, inject, input, output } from '@angular/core';
import { avatarHue, formatBytes, formatDate, initials, safeHomePage } from '../../core/format';
import { AttachmentDto, CommentDto } from '../../core/models';
import { sanitizeHtml } from '../../core/sanitize';
import { AttachmentViewerService } from '../../core/viewer.service';

/**
 * Каскадный узел дерева комментариев: карточка с аватаром, телом и
 * рекурсивным блоком дочерних ответов (отступ + левая полоса-цитата, любая глубина).
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

  private readonly viewer = inject(AttachmentViewerService);

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
}
