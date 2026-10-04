/** Форматирование дат/размеров и мелкие презентационные утилиты. */

export function formatDate(iso: string | null | undefined): string {
  if (!iso) {
    return '';
  }
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) {
    return String(iso);
  }
  const p = (n: number) => String(n).padStart(2, '0');
  // Формат образца: 22.05.22 в 22:30 (локальное время браузера).
  return `${p(date.getDate())}.${p(date.getMonth() + 1)}.${String(date.getFullYear()).slice(-2)} в ${p(
    date.getHours(),
  )}:${p(date.getMinutes())}`;
}

export function formatBytes(bytes: number | null | undefined): string {
  const n = Number(bytes) || 0;
  if (n < 1024) {
    return `${n} Б`;
  }
  if (n < 1024 * 1024) {
    return `${(n / 1024).toFixed(1)} КБ`;
  }
  return `${(n / (1024 * 1024)).toFixed(2)} МБ`;
}

export function truncate(value: string | null | undefined, max: number): string {
  const s = String(value == null ? '' : value)
    .replace(/\s+/g, ' ')
    .trim();
  return s.length > max ? `${s.slice(0, max - 1)}…` : s;
}

export function replyWord(count: number): string {
  const mod10 = count % 10;
  const mod100 = count % 100;
  if (mod10 === 1 && mod100 !== 11) {
    return 'ответ';
  }
  if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) {
    return 'ответа';
  }
  return 'ответов';
}

/** URL домашней страницы только со схемой http(s) (defense-in-depth). */
export function safeHomePage(value: string | null | undefined): string | null {
  if (typeof value !== 'string' || !value.trim()) {
    return null;
  }
  try {
    const url = new URL(value);
    if (url.protocol === 'http:' || url.protocol === 'https:') {
      return url.href;
    }
  } catch {
    return null;
  }
  return null;
}

export function avatarHue(userName: string | null | undefined): string {
  const s = String(userName || '');
  let hash = 0;
  for (let i = 0; i < s.length; i += 1) {
    hash = (hash * 31 + s.charCodeAt(i)) % 360;
  }
  return `${hash}deg`;
}

export function initials(userName: string | null | undefined): string {
  const s = String(userName || '?').trim();
  return (s.slice(0, 2) || '?').toUpperCase();
}
