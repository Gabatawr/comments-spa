/**
 * comments.js — корневые комментарии таблицей с сортировкой и пагинацией,
 * каскадное дерево ответов (рекурсивно, с отступом и левой полосой-цитатой).
 */

import {
  listComments,
  DEFAULT_PAGE_SIZE,
  DEFAULT_SORT_BY,
  DEFAULT_SORT_DIR,
  SORT_FIELDS,
  attachmentUrl,
  attachmentThumbUrl,
} from './api.js';
import { sanitizeHtml } from './sanitize.js';
import { formatBytes } from './attachments.js';

const MAX_DEPTH = 30;

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text != null) node.textContent = String(text);
  return node;
}

export function formatDate(iso) {
  if (!iso) return '';
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return String(iso);
  const p = (n) => String(n).padStart(2, '0');
  // В образце: 22.05.22 в 22:30
  return `${p(date.getDate())}.${p(date.getMonth() + 1)}.${String(date.getFullYear()).slice(-2)} в ${p(date.getHours())}:${p(date.getMinutes())}`;
}

function hashString(value) {
  let hash = 0;
  const s = String(value || '');
  for (let i = 0; i < s.length; i += 1) hash = (hash * 31 + s.charCodeAt(i)) % 360;
  return hash;
}

function truncate(value, max) {
  const s = String(value == null ? '' : value).replace(/\s+/g, ' ').trim();
  return s.length > max ? `${s.slice(0, max - 1)}…` : s;
}

/** URL домашней страницы, только http(s) (defense-in-depth). */
function safeHomePage(value) {
  if (typeof value !== 'string' || !value.trim()) return null;
  try {
    const url = new URL(value);
    if (url.protocol === 'http:' || url.protocol === 'https:') return url.href;
  } catch {
    return null;
  }
  return null;
}

function buildAttachment(comment, onError) {
  const attachment = comment && comment.attachment;
  if (!attachment) return null;
  const wrap = el('div', 'comment-attachment');

  if (attachment.kind === 'image') {
    const src = attachment.thumbUrl || attachmentThumbUrl(attachment.id);
    const full = attachment.url || attachmentUrl(attachment.id);
    const img = document.createElement('img');
    img.className = 'comment-thumb';
    img.src = src;
    img.alt = attachment.fileName ? `Изображение ${attachment.fileName}` : 'Изображение';
    img.loading = 'lazy';
    img.dataset.lightbox = full;
    img.dataset.caption = attachment.fileName || '';
    img.title = 'Открыть изображение';
    img.addEventListener('error', () => {
      img.classList.add('is-broken');
      if (onError) onError('Не удалось загрузить изображение.');
    });
    wrap.appendChild(img);
    const meta = el(
      'span',
      'attachment-meta',
      `${attachment.fileName || 'image'}${attachment.width ? ` · ${attachment.width}×${attachment.height}` : ''}`
    );
    wrap.appendChild(meta);
  } else {
    const button = el('button', 'file-chip');
    button.type = 'button';
    button.dataset.textViewer = attachment.url || attachmentUrl(attachment.id);
    button.dataset.fileName = attachment.fileName || 'file.txt';
    button.appendChild(el('span', 'file-chip-icon', '📄'));
    button.appendChild(el('span', 'file-chip-name', attachment.fileName || 'Текстовый файл'));
    button.appendChild(el('span', 'file-chip-size', formatBytes(attachment.sizeBytes || 0)));
    wrap.appendChild(button);
  }
  return wrap;
}

/**
 * Создаёт представление списка комментариев.
 * @param {{table:HTMLTableElement, tbody:HTMLElement, emptyEl?:HTMLElement,
 *          loadingEl?:HTMLElement, paginationEl?:HTMLElement, statusEl?:HTMLElement,
 *          onReply?:Function, onError?:Function, pageSize?:number}} opts
 */
export function createCommentsView(opts) {
  const state = {
    page: 1,
    pageSize: opts.pageSize || DEFAULT_PAGE_SIZE,
    sortBy: DEFAULT_SORT_BY,
    sortDir: DEFAULT_SORT_DIR,
    totalItems: 0,
    totalPages: 0,
    items: [],
  };
  const expanded = new Set();
  let loading = false;
  let requestSeq = 0;

  function notifyError(error) {
    const message =
      (error && (error.detail || error.message || error.title)) || 'Не удалось загрузить комментарии';
    if (opts.statusEl) {
      opts.statusEl.textContent = message;
      opts.statusEl.className = 'comments-status is-error';
    }
    if (opts.onError) opts.onError(message, error);
  }

  function setLoading(value) {
    loading = value;
    if (opts.loadingEl) opts.loadingEl.hidden = !value;
    if (opts.table) opts.table.setAttribute('aria-busy', value ? 'true' : 'false');
  }

  /* ------------------------------- cards ------------------------------- */

  function renderCard(comment, depth, visited) {
    const card = el('article', 'comment-card');
    card.dataset.id = comment.id;
    card.style.setProperty('--depth', String(depth));

    const head = el('header', 'comment-head');
    const avatar = el('span', 'avatar', (comment.userName || '?').slice(0, 2).toUpperCase());
    avatar.style.setProperty('--avatar-hue', `${hashString(comment.userName)}deg`);
    avatar.setAttribute('aria-hidden', 'true');
    head.appendChild(avatar);

    const author = el('div', 'comment-author');
    const name = el('span', 'comment-username', comment.userName || 'Неизвестный');
    author.appendChild(name);

    const time = el('time', 'comment-date', formatDate(comment.createdAt));
    if (comment.createdAt) time.dateTime = comment.createdAt;
    author.appendChild(time);

    if (comment.email) {
      const email = el('span', 'comment-email', comment.email);
      email.title = 'E-mail';
      author.appendChild(email);
    }
    const home = safeHomePage(comment.homePage);
    if (home) {
      const link = el('a', 'comment-home', 'домашняя страница');
      link.href = home;
      link.target = '_blank';
      link.rel = 'noopener noreferrer nofollow';
      author.appendChild(link);
    } else if (comment.homePage) {
      // Значение есть, но не является абсолютным http(s) URL — показываем текстом.
      author.appendChild(el('span', 'comment-home', String(comment.homePage)));
    }
    head.appendChild(author);

    const actions = el('div', 'comment-actions');
    const reply = el('button', 'btn btn-ghost btn-reply', 'Ответить');
    reply.type = 'button';
    reply.dataset.action = 'reply';
    reply.dataset.id = String(comment.id);
    reply.dataset.userName = comment.userName || '';
    actions.appendChild(reply);
    head.appendChild(actions);
    card.appendChild(head);

    const body = el('div', 'comment-body');
    body.innerHTML = sanitizeHtml(comment.text || '');
    card.appendChild(body);

    const attachment = buildAttachment(comment, opts.onError);
    if (attachment) card.appendChild(attachment);

    const replies = Array.isArray(comment.replies) ? comment.replies : [];
    if (replies.length) {
      if (depth >= MAX_DEPTH) {
        card.appendChild(el('div', 'comment-depth-note', 'Достигнут предел вложенности отображения.'));
      } else {
        const children = el('div', 'comment-children');
        for (const child of replies) {
          if (child && child.id != null) {
            if (visited.has(child.id)) continue;
            visited.add(child.id);
          }
          children.appendChild(renderCard(child, depth + 1, visited));
        }
        card.appendChild(children);
      }
    }
    return card;
  }

  function renderCascade(comment) {
    const wrap = el('div', 'cascade');
    const visited = new Set([comment.id]);
    wrap.appendChild(renderCard(comment, 0, visited));
    return wrap;
  }

  /* ------------------------------- table ------------------------------- */

  function columnCount() {
    const headRow = opts.table && opts.table.tHead && opts.table.tHead.rows[0];
    return headRow ? headRow.cells.length : 6;
  }

  function renderRows() {
    const tbody = opts.tbody;
    if (!tbody) return;
    tbody.textContent = '';
    const cols = columnCount();

    for (const comment of state.items) {
      const row = el('tr', 'root-row');
      row.dataset.id = String(comment.id);
      if (expanded.has(comment.id)) row.classList.add('is-expanded');

      const nameCell = el('td', 'cell-user');
      nameCell.appendChild(el('span', 'cell-username', comment.userName || ''));
      row.appendChild(nameCell);

      row.appendChild(el('td', 'cell-email', comment.email || ''));
      const dateCell = el('td', 'cell-date', formatDate(comment.createdAt));
      if (comment.createdAt) dateCell.title = comment.createdAt;
      row.appendChild(dateCell);

      const textCell = el('td', 'cell-text', truncate(comment.textPlain || '', 140));
      row.appendChild(textCell);

      const fileCell = el('td', 'cell-file');
      if (comment.attachment) {
        fileCell.appendChild(
          el('span', 'file-badge', comment.attachment.kind === 'image' ? '🖼' : '📄')
        );
        fileCell.appendChild(
          el('span', 'file-badge-name', truncate(comment.attachment.fileName || '', 24))
        );
      } else {
        fileCell.appendChild(el('span', 'muted', '—'));
      }
      row.appendChild(fileCell);

      const actionsCell = el('td', 'cell-actions');
      const actionsWrap = el('div', 'row-actions');
      const replyCount = Number(comment.replyCount) || (Array.isArray(comment.replies) ? comment.replies.length : 0);
      if (replyCount > 0) {
        const toggle = el('button', 'btn btn-ghost btn-toggle');
        toggle.type = 'button';
        toggle.dataset.action = 'toggle';
        toggle.dataset.id = String(comment.id);
        toggle.setAttribute('aria-expanded', expanded.has(comment.id) ? 'true' : 'false');
        toggle.textContent = expanded.has(comment.id)
          ? 'Свернуть'
          : `${replyCount} ${replyWord(replyCount)}`;
        actionsWrap.appendChild(toggle);
      } else {
        actionsWrap.appendChild(el('span', 'muted', '—'));
      }
      const reply = el('button', 'btn btn-ghost btn-reply', 'Ответить');
      reply.type = 'button';
      reply.dataset.action = 'reply';
      reply.dataset.id = String(comment.id);
      reply.dataset.userName = comment.userName || '';
      actionsWrap.appendChild(reply);
      actionsCell.appendChild(actionsWrap);
      row.appendChild(actionsCell);
      tbody.appendChild(row);

      if (expanded.has(comment.id)) {
        const detail = el('tr', 'detail-row');
        detail.dataset.forId = String(comment.id);
        const cell = el('td', 'detail-cell');
        cell.colSpan = cols;
        cell.appendChild(renderCascade(comment));
        detail.appendChild(cell);
        tbody.appendChild(detail);
      }
    }

    if (opts.emptyEl) opts.emptyEl.hidden = state.items.length > 0;
    if (opts.table) opts.table.hidden = state.items.length === 0;
  }

  function replyWord(count) {
    const mod10 = count % 10;
    const mod100 = count % 100;
    if (mod10 === 1 && mod100 !== 11) return 'ответ';
    if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return 'ответа';
    return 'ответов';
  }

  function updateSortIndicators() {
    if (!opts.table) return;
    const headers = opts.table.querySelectorAll('th[data-sort]');
    headers.forEach((th) => {
      const field = th.dataset.sort;
      const active = field === state.sortBy;
      th.classList.toggle('is-sorted', active);
      th.classList.toggle('is-asc', active && state.sortDir === 'asc');
      th.classList.toggle('is-desc', active && state.sortDir === 'desc');
      th.setAttribute(
        'aria-sort',
        active ? (state.sortDir === 'asc' ? 'ascending' : 'descending') : 'none'
      );
      const arrow = th.querySelector('.sort-arrow');
      if (arrow) arrow.textContent = active ? (state.sortDir === 'asc' ? '▲' : '▼') : '';
    });
  }

  function updateStatus() {
    if (!opts.statusEl) return;
    if (!state.totalItems) {
      opts.statusEl.textContent = 'Комментариев пока нет.';
      opts.statusEl.className = 'comments-status';
      return;
    }
    const from = (state.page - 1) * state.pageSize + 1;
    const to = Math.min(state.page * state.pageSize, state.totalItems);
    opts.statusEl.textContent = `Показано ${from}–${to} из ${state.totalItems}`;
    opts.statusEl.className = 'comments-status';
  }

  function pageWindow(current, total) {
    const pages = new Set([1, total, current, current - 1, current + 1]);
    if (current <= 3) [2, 3, 4].forEach((p) => pages.add(p));
    if (current >= total - 2) [total - 1, total - 2, total - 3].forEach((p) => pages.add(p));
    return Array.from(pages)
      .filter((p) => p >= 1 && p <= total)
      .sort((a, b) => a - b);
  }

  function renderPagination() {
    const host = opts.paginationEl;
    if (!host) return;
    host.textContent = '';
    const total = state.totalPages;
    if (total <= 1) {
      host.hidden = true;
      return;
    }
    host.hidden = false;

    const makeButton = (label, page, className, disabled) => {
      const button = el('button', `page-btn${className ? ` ${className}` : ''}`, label);
      button.type = 'button';
      button.dataset.page = String(page);
      button.disabled = Boolean(disabled);
      if (!disabled && page === state.page) button.classList.add('is-current');
      return button;
    };

    host.appendChild(makeButton('‹', state.page - 1, 'page-prev', state.page <= 1));
    const pages = pageWindow(state.page, total);
    let previous = 0;
    for (const page of pages) {
      if (previous && page - previous > 1) host.appendChild(el('span', 'page-gap', '…'));
      host.appendChild(makeButton(String(page), page, 'page-num', false));
      previous = page;
    }
    host.appendChild(makeButton('›', state.page + 1, 'page-next', state.page >= total));
  }

  /* ------------------------------ actions ------------------------------ */

  function setSort(field) {
    if (SORT_FIELDS.indexOf(field) === -1) return;
    if (state.sortBy === field) {
      state.sortDir = state.sortDir === 'asc' ? 'desc' : 'asc';
    } else {
      state.sortBy = field;
      state.sortDir = field === 'createdAt' ? 'desc' : 'asc';
    }
    state.page = 1;
    expanded.clear();
    return load();
  }

  function goToPage(page) {
    const target = Math.max(1, Number(page) || 1);
    if (target === state.page && state.items.length) return Promise.resolve(state);
    state.page = target;
    expanded.clear();
    return load();
  }

  function containsComment(id) {
    const needle = String(id);
    const walk = (nodes) => {
      for (const node of nodes || []) {
        if (String(node.id) === needle) return true;
        if (node.replies && walk(node.replies)) return true;
      }
      return false;
    };
    return walk(state.items);
  }

  async function load() {
    if (loading) return state;
    const seq = ++requestSeq;
    setLoading(true);
    try {
      const data = await listComments({
        page: state.page,
        pageSize: state.pageSize,
        sortBy: state.sortBy,
        sortDir: state.sortDir,
      });
      if (seq !== requestSeq) return state; // устаревший ответ
      state.page = Number(data.page) || state.page;
      state.pageSize = Number(data.pageSize) || state.pageSize;
      state.sortBy = data.sortBy || state.sortBy;
      state.sortDir = data.sortDir || state.sortDir;
      state.totalItems = Number(data.totalItems) || 0;
      state.totalPages = Number(data.totalPages) || 0;
      state.items = Array.isArray(data.items) ? data.items : [];
      renderRows();
      updateSortIndicators();
      updateStatus();
      renderPagination();
      return state;
    } catch (error) {
      notifyError(error);
      return state;
    } finally {
      if (seq === requestSeq) setLoading(false);
    }
  }

  function refresh() {
    return load();
  }

  /* ------------------------------ bindings ------------------------------ */

  if (opts.table) {
    const head = opts.table.tHead;
    if (head) {
      const activate = (event) => {
        const th = event.target.closest('th[data-sort]');
        if (!th) return;
        event.preventDefault();
        setSort(th.dataset.sort);
      };
      head.addEventListener('click', activate);
      head.addEventListener('keydown', (event) => {
        if (event.key === 'Enter' || event.key === ' ') activate(event);
      });
    }
  }

  if (opts.tbody) {
    opts.tbody.addEventListener('click', (event) => {
      const button = event.target.closest('button[data-action]');
      if (!button) return;
      const id = Number(button.dataset.id);
      if (button.dataset.action === 'toggle') {
        if (expanded.has(id)) expanded.delete(id);
        else expanded.add(id);
        renderRows();
      } else if (button.dataset.action === 'reply') {
        const comment = findComment(id);
        if (opts.onReply) opts.onReply(comment || { id });
      }
    });
  }

  if (opts.paginationEl) {
    opts.paginationEl.addEventListener('click', (event) => {
      const button = event.target.closest('button[data-page]');
      if (!button || button.disabled) return;
      goToPage(button.dataset.page);
    });
  }

  function findComment(id) {
    let found = null;
    const walk = (nodes) => {
      for (const node of nodes || []) {
        if (found) return;
        if (String(node.id) === String(id)) {
          found = node;
          return;
        }
        walk(node.replies);
      }
    };
    walk(state.items);
    return found;
  }

  return {
    load,
    refresh,
    setSort,
    goToPage,
    getState: () => Object.assign({}, state),
    getItems: () => state.items.slice(),
    containsComment,
    findComment,
    expand: (id) => {
      expanded.add(Number(id));
      renderRows();
    },
    isDefaultSort: () => state.sortBy === DEFAULT_SORT_BY && state.sortDir === DEFAULT_SORT_DIR,
  };
}

export default { createCommentsView, formatDate };
