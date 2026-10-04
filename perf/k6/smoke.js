// perf/k6/smoke.js — fast endpoint validation (1 VU, one iteration per flow).
// Use before a full run: docker compose --profile load run --rm k6 run /scripts/smoke.js
import http from 'k6/http';
import { check } from 'k6';

const BASE = __ENV.BASE_URL || 'http://api:8080';

export const options = {
  vus: 1,
  iterations: 1,
  thresholds: { checks: ['rate>0.99'] },
};

export default function () {
  const health = http.get(`${BASE}/api/health`);
  check(health, { 'health 200': (r) => r.status === 200 });
  check(health, { 'database ok': (r) => r.status === 200 && r.json('database') === 'ok' });

  const list = http.get(`${BASE}/api/comments?sortBy=createdAt&sortDir=desc&page=1&pageSize=25`);
  check(list, { 'list 200': (r) => r.status === 200 });
  const items = list.status === 200 ? list.json('items') || [] : [];
  check(list, { 'list has items': () => items.length > 0 });

  if (items.length > 0) {
    const one = http.get(`${BASE}/api/comments/${items[0].id}`);
    check(one, { 'detail 200': (r) => r.status === 200 });
  }

  const s = http.get(`${BASE}/api/search?q=lorem&pageSize=5`);
  check(s, { 'search 200 or 503': (r) => r.status === 200 || r.status === 503 });

  const cap = http.get(`${BASE}/api/captcha`);
  check(cap, { 'captcha 200': (r) => r.status === 200 });
  if (cap.status === 200) {
    const cid = cap.json('captchaId');
    const peek = http.get(`${BASE}/api/dev/captcha/${cid}`);
    check(peek, { 'dev peek 200': (r) => r.status === 200 });
    if (peek.status === 200) {
      const payload = JSON.stringify({
        userName: 'smoke1',
        email: 'smoke1@example.com',
        homePage: null,
        text: 'smoke <strong>test</strong>',
        parentId: null,
        captchaId: cid,
        captchaAnswer: peek.json('code'),
      });
      const created = http.post(`${BASE}/api/comments/0/child`, payload, {
        headers: { 'Content-Type': 'application/json' },
      });
      check(created, { 'create 201': (r) => r.status === 201 });
    }
  }

  const stats = http.get(`${BASE}/api/stats`);
  check(stats, { 'stats 200': (r) => r.status === 200 });
}
