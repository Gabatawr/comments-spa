// ---------------------------------------------------------------------------
// SPA «Комментарии» — k6 load scenarios (Middle+)
//
// Realistic mix:
//   browse    GET /api/comments?sortBy&sortDir&page     (cache + DB, full reply trees)
//   read_one  GET /api/comments/{id}                    (DB tree assembly)
//   search    GET /api/search?q=...                     (Elasticsearch)
//   create    GET /api/captcha -> GET /api/dev/captcha/{id}
//             -> POST /api/comments/{parentId}/child    (CAPTCHA + write + broker)
//   websocket WS /ws, expect {"type":"hello"}           (connect latency)
//
// Run (compose load profile):
//   docker compose --profile load run --rm k6 run /scripts/scenarios.js
//
// Useful env overrides (all optional):
//   BASE_URL=http://api:8080   WS_URL=ws://api:8080/ws
//   SCALE=1                    multiplies every VU target
//   RAMP_S=20 STEADY_S=60      stage durations
//   SEARCH_ENABLED=false       skip ES scenario
//   CREATE_ENABLED=false       skip write scenario
//   WS_ENABLED=false           skip websocket scenario
//   RUN_LABEL=100k-full        file label for /results/summary-<label>.json
//   SUMMARY_DIR=/results       where handleSummary writes the JSON
// ---------------------------------------------------------------------------

import http from 'k6/http';
import { check, sleep, group } from 'k6';
import { Trend, Counter, Rate } from 'k6/metrics';
import { WebSocket } from 'k6/websockets';

const BASE = __ENV.BASE_URL || 'http://api:8080';
const WS_URL = __ENV.WS_URL || 'ws://api:8080/ws';
const SCALE = Number(__ENV.SCALE || 1);
const RAMP_S = Number(__ENV.RAMP_S || 20);
const STEADY_S = Number(__ENV.STEADY_S || 60);
const SEARCH_ENABLED = (__ENV.SEARCH_ENABLED || 'true') !== 'false';
const CREATE_ENABLED = (__ENV.CREATE_ENABLED || 'true') !== 'false';
const WS_ENABLED = (__ENV.WS_ENABLED || 'true') !== 'false';

const vus = (n) => Math.max(1, Math.round(n * SCALE));
const rampStages = (target) => [
  { duration: `${RAMP_S}s`, target: vus(target) },        // ramp-up
  { duration: `${STEADY_S}s`, target: vus(target) },      // steady state
  { duration: `${RAMP_S}s`, target: 0 },                  // ramp-down
];

// --- custom metrics -------------------------------------------------------
const wsConnectMs = new Trend('ws_connect_ms', true);
const wsHello = new Counter('ws_hello_received');
const wsErrors = new Counter('ws_errors');
const createOk = new Counter('create_ok');
const createFail = new Counter('create_fail');
const searchHits = new Counter('search_hits');
const createCaptchaMs = new Trend('create_captcha_ms', true);
const createPeekMs = new Trend('create_peek_ms', true);
const createPostMs = new Trend('create_post_ms', true);

const scenarios = {
  browse: {
    executor: 'ramping-vus',
    exec: 'browse',
    startTime: '0s',
    stages: rampStages(15),
    gracefulStop: '10s',
  },
  read_one: {
    executor: 'ramping-vus',
    exec: 'readOne',
    startTime: '0s',
    stages: rampStages(8),
    gracefulStop: '10s',
  },
  create: {
    executor: 'ramping-vus',
    exec: 'create',
    startTime: '0s',
    stages: rampStages(2),
    gracefulStop: '15s',
  },
  websocket: {
    executor: 'ramping-vus',
    exec: 'websocket',
    startTime: '0s',
    stages: rampStages(5),
    gracefulStop: '10s',
  },
};
if (SEARCH_ENABLED) {
  scenarios.search = {
    executor: 'ramping-vus',
    exec: 'search',
    startTime: '0s',
    stages: rampStages(5),
    gracefulStop: '10s',
  };
}
if (!CREATE_ENABLED) delete scenarios.create;
if (!WS_ENABLED) delete scenarios.websocket;

export const options = {
  scenarios,
  summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(90)', 'p(95)', 'p(99)'],
  thresholds: {
    // Headline SLO for the whole run.
    'http_req_duration': ['p(95)<500', 'p(99)<1500'],
    'http_req_failed': ['rate<0.01'],
    'checks': ['rate>0.99'],
    // Per-scenario SLO.
    'http_req_duration{scenario:browse}': ['p(95)<500', 'p(99)<1500'],
    'http_req_duration{scenario:read_one}': ['p(95)<500', 'p(99)<1500'],
    'http_req_duration{scenario:search}': ['p(95)<500', 'p(99)<1500'],
    'http_req_duration{scenario:create}': ['p(95)<800', 'p(99)<2000'],
    'ws_connect_ms': ['p(95)<500'],
  },
  tags: { project: 'comments-spa' },
};

const SORTS = ['createdAt', 'userName', 'email'];
const DIRS = ['asc', 'desc'];
const SEARCH_TERMS = ['lorem', 'comment', 'alpha', 'beta', 'hello', 'user1', 'user42', 'world'];
const FALLBACK_TERMS = ['lorem', 'comment', 'hello'];

function pick(arr) {
  return arr[Math.floor(Math.random() * arr.length)];
}
function randInt(n) {
  return Math.floor(Math.random() * n);
}

// ---------------------------------------------------------------------------
// setup(): health gate + sample ids/terms once. Runs in one VU before load.
// ---------------------------------------------------------------------------
export function setup() {
  const health = http.get(`${BASE}/api/health`);
  if (health.status !== 200) {
    throw new Error(`health gate: GET /api/health -> ${health.status}`);
  }
  const healthBody = health.json();
  if (healthBody.database !== 'ok') {
    throw new Error(`health gate: database=${healthBody.database} status=${healthBody.status}`);
  }

  // Sample root ids + user names/emails from the first pages.
  const ids = [];
  const terms = [];
  for (let page = 1; page <= 3; page++) {
    const res = http.get(
      `${BASE}/api/comments?sortBy=createdAt&sortDir=desc&page=${page}&pageSize=25`
    );
    if (res.status !== 200) break;
    let body;
    try {
      body = res.json();
    } catch (e) {
      break;
    }
    for (const c of body.items || []) {
      if (typeof c.id === 'number') ids.push(c.id);
      if (c.userName) terms.push(c.userName);
      if (c.email) terms.push(c.email);
    }
  }

  // Search probe: fail fast with a clear message if ES is required but off.
  let searchAvailable = false;
  let searchStatus = 0;
  if (SEARCH_ENABLED) {
    const s = http.get(`${BASE}/api/search?q=lorem&pageSize=5`);
    searchStatus = s.status;
    searchAvailable = s.status === 200;
    if (!searchAvailable) {
      throw new Error(
        `search probe: GET /api/search -> ${s.status}. ` +
          `Start Elasticsearch or re-run with SEARCH_ENABLED=false.`
      );
    }
  }

  // Create probe: CAPTCHA peek must be usable in Development/Load.
  let createAvailable = false;
  if (CREATE_ENABLED) {
    const cap = http.get(`${BASE}/api/captcha`);
    const cid = cap.status === 200 ? cap.json('captchaId') : null;
    const peek = cid ? http.get(`${BASE}/api/dev/captcha/${cid}`) : { status: 0, json: () => ({}) };
    createAvailable = cap.status === 200 && peek.status === 200;
    if (!createAvailable) {
      throw new Error(
        `create probe: captcha=${cap.status} peek=${peek.status}. ` +
          `Enable ASPNETCORE_ENVIRONMENT=Development and Features:DevCaptchaPeek=true, ` +
          `or re-run with CREATE_ENABLED=false.`
      );
    }
  }

  const stats = http.get(`${BASE}/api/stats`);
  let total = null;
  if (stats.status === 200) {
    try {
      total = stats.json().totalComments;
    } catch (e) {
      total = null;
    }
  }

  const searchTerms = terms.length > 0 ? terms.slice(0, 200).concat(SEARCH_TERMS) : FALLBACK_TERMS;

  return {
    ids,
    searchTerms,
    totalComments: total,
    health: healthBody,
    searchStatus,
    createAvailable,
    startedAt: new Date().toISOString(),
  };
}

// ---------------------------------------------------------------------------
// browse: exercise cache + DB with mixed sort keys and page depths.
// ---------------------------------------------------------------------------
export function browse() {
  const sortBy = pick(SORTS);
  const sortDir = pick(DIRS);
  const page = 1 + randInt(20);
  const url = `${BASE}/api/comments?sortBy=${sortBy}&sortDir=${sortDir}&page=${page}&pageSize=25`;
  const res = http.get(url, { tags: { endpoint: 'comments_list', sortBy, sortDir } });
  check(res, {
    'browse status 200': (r) => r.status === 200,
    'browse has body': (r) => !!r.body && r.body.length > 0,
  });
  sleep(Math.random() * 0.2);
}

// ---------------------------------------------------------------------------
// read_one: fetch a sampled comment with its full reply tree.
// ---------------------------------------------------------------------------
export function readOne(DATA) {
  const ids = DATA.ids || [];
  let url;
  if (ids.length > 0) {
    url = `${BASE}/api/comments/${pick(ids)}`;
  } else {
    // Fallback: derive from the first page if setup sampling failed.
    url = `${BASE}/api/comments/1`;
  }
  const res = http.get(url, { tags: { endpoint: 'comment_detail' } });
  check(res, {
    'read_one status 200 or 404': (r) => r.status === 200 || r.status === 404,
    'read_one status 200': (r) => r.status === 200,
  });
  sleep(Math.random() * 0.2);
}

// ---------------------------------------------------------------------------
// search: Elasticsearch full-text query.
// ---------------------------------------------------------------------------
export function search(DATA) {
  const terms = (DATA && DATA.searchTerms) || FALLBACK_TERMS;
  const q = encodeURIComponent(pick(terms));
  const page = 1 + randInt(5);
  const res = http.get(`${BASE}/api/search?q=${q}&page=${page}&pageSize=25`, {
    tags: { endpoint: 'search' },
  });
  check(res, { 'search status 200': (r) => r.status === 200 });
  if (res.status === 200) {
    try {
      searchHits.add((res.json().items || []).length);
    } catch (e) {
      /* ignore */
    }
  }
  sleep(Math.random() * 0.3);
}

// ---------------------------------------------------------------------------
// create: captcha -> dev peek -> JSON child create (write path + broker).
// ---------------------------------------------------------------------------
export function create(DATA) {
  return group('create_flow', () => {
    let t0 = Date.now();
    const cap = http.get(`${BASE}/api/captcha`, { tags: { endpoint: 'captcha' } });
    createCaptchaMs.add(Date.now() - t0);
    if (cap.status !== 200) {
      createFail.add(1);
      check(cap, { 'captcha 200': (r) => r.status === 200 });
      return;
    }
    const captchaId = cap.json('captchaId');

    t0 = Date.now();
    const peek = http.get(`${BASE}/api/dev/captcha/${captchaId}`, {
      tags: { endpoint: 'captcha_peek' },
    });
    createPeekMs.add(Date.now() - t0);
    if (peek.status !== 200) {
      createFail.add(1);
      check(peek, { 'captcha peek 200': (r) => r.status === 200 });
      return;
    }
    const answer = peek.json('code');

    const ids = (DATA && DATA.ids) || [];
    // ~20% roots (POST /0/child, parentId null), ~80% replies to a sampled root.
    const asRoot = ids.length === 0 || Math.random() < 0.2;
    const parentId = asRoot ? null : pick(ids);
    const n = randInt(1000000);
    const payload = {
      userName: `k6u${n}`,
      email: `k6u${n}@example.com`,
      homePage: null,
      text: `k6 load <strong>comment</strong> <i>${n}</i> alpha beta`,
      parentId: parentId,
      captchaId: captchaId,
      captchaAnswer: answer,
    };

    const url = parentId
      ? `${BASE}/api/comments/${parentId}/child`
      : `${BASE}/api/comments/0/child`;
    const tPost = Date.now();
    const res = http.post(url, JSON.stringify(payload), {
      headers: { 'Content-Type': 'application/json' },
      tags: { endpoint: 'comment_create' },
    });
    createPostMs.add(Date.now() - tPost);
    const ok = res.status === 201;
    check(res, { 'create status 201': () => ok });
    if (ok) createOk.add(1);
    else createFail.add(1);
    sleep(Math.random() * 0.5);
  });
}

// ---------------------------------------------------------------------------
// websocket: connect, expect {"type":"hello"}, measure connect latency.
// ---------------------------------------------------------------------------
export function websocket() {
  const started = Date.now();
  let helloSeen = false;
  let closed = false;

  try {
    const ws = new WebSocket(WS_URL);
    const timeout = setTimeout(() => {
      if (!closed) {
        try {
          ws.close();
        } catch (e) {
          /* ignore */
        }
      }
    }, 5000);

    ws.addEventListener('open', () => {
      wsConnectMs.add(Date.now() - started);
    });
    ws.addEventListener('message', (ev) => {
      try {
        const msg = JSON.parse(ev.data);
        if (msg && msg.type === 'hello') {
          helloSeen = true;
          wsHello.add(1);
          clearTimeout(timeout);
          ws.close();
        }
      } catch (e) {
        wsErrors.add(1);
      }
    });
    ws.addEventListener('error', () => {
      wsErrors.add(1);
    });
    ws.addEventListener('close', () => {
      closed = true;
      clearTimeout(timeout);
      check({ helloSeen }, { 'websocket hello received': (o) => o.helloSeen === true });
    });
  } catch (e) {
    wsErrors.add(1);
  }
  sleep(Math.random() * 0.2);
}

// ---------------------------------------------------------------------------
// handleSummary: full JSON machine-readable export + compact console table.
// ---------------------------------------------------------------------------
function ms(v) {
  return v === undefined || v === null ? 'n/a' : `${Number(v).toFixed(2)}ms`;
}
function rate(v) {
  return v === undefined || v === null ? 'n/a' : `${(Number(v) * 100).toFixed(2)}%`;
}

function consoleSummary(data) {
  const m = data.metrics || {};
  const dur = m['http_req_duration'] ? m['http_req_duration'].values : {};
  const failed = m['http_req_failed'] ? m['http_req_failed'].values : {};
  const checks = m['checks'] ? m['checks'].values : {};
  const reqs = m['http_reqs'] ? m['http_reqs'].values : {};
  const ws = m['ws_connect_ms'] ? m['ws_connect_ms'].values : {};

  const lines = [];
  lines.push('');
  lines.push('================ k6 SUMMARY — comments-spa ================');
  lines.push(`dataset totalComments (setup) : see RUN bookkeeping / perf/report.md`);
  lines.push(`http_reqs                     : ${reqs.count !== undefined ? reqs.count : 'n/a'}`);
  lines.push(`http_req_duration avg         : ${ms(dur.avg)}`);
  lines.push(`http_req_duration p(95)       : ${ms(dur['p(95)'])}`);
  lines.push(`http_req_duration p(99)       : ${ms(dur['p(99)'])}`);
  lines.push(`http_req_duration max         : ${ms(dur.max)}`);
  lines.push(`http_req_failed               : ${rate(failed.rate)}`);
  lines.push(`checks                        : ${rate(checks.rate)}`);
  lines.push(`ws_connect_ms p(95)           : ${ms(ws['p(95)'])}`);
  lines.push('');
  lines.push('--- per-scenario http_req_duration (p95 / p99) ---');
  const byScenario = {};
  for (const [key, val] of Object.entries(m)) {
    const mm = key.match(/^http_req_duration\{scenario:([^}]+)\}$/);
    if (mm && val && val.values) {
      byScenario[mm[1]] = val.values;
    }
  }
  for (const s of Object.keys(byScenario).sort()) {
    lines.push(`  ${s.padEnd(12)} p95=${ms(byScenario[s]['p(95)'])}  p99=${ms(byScenario[s]['p(99)'])}`);
  }
  lines.push('');
  lines.push('--- thresholds ---');
  for (const [key, val] of Object.entries(m)) {
    if (val && val.thresholds) {
      for (const [t, res] of Object.entries(val.thresholds)) {
        // k6 >= v0.5x: value is {ok:boolean}; older versions: boolean.
        const ok = typeof res === 'boolean' ? res : !!(res && res.ok);
        lines.push(`  [${ok ? 'PASS' : 'FAIL'}] ${key}: ${t}`);
      }
    }
  }
  lines.push('==========================================================');
  lines.push('');
  return lines.join('\n');
}

export function handleSummary(data) {
  const label = (__ENV.RUN_LABEL || 'default').replace(/[^A-Za-z0-9._-]/g, '_');
  const dir = __ENV.SUMMARY_DIR || '/results';
  const out = {};
  out[`${dir}/summary-${label}.json`] = JSON.stringify(data, null, 2);
  out['stdout'] = consoleSummary(data);
  return out;
}
