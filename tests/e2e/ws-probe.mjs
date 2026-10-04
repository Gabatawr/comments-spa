#!/usr/bin/env node
/**
 * WebSocket probe for the «Комментарии» API (v2 contract §2.9).
 *
 * Connects to the /ws endpoint, waits for the `hello` frame, then waits for a
 * `comment.created` frame carrying the expected userName (created by the
 * caller via HTTP while this probe is running).
 *
 * Usage:
 *   node ws-probe.mjs <ws-url> <timeoutMs> <expectedUserName> [readyFile]
 *
 * Emits a single JSON object on stdout:
 *   {"url":..., "connected":true, "hello":{...},"created":{...},"messages":[...]}
 * Exit code 0 when hello AND comment.created were observed, 1 otherwise.
 *
 * Node >= 22 provides a global WebSocket implementation; no npm deps required.
 */

const url = process.argv[2] || 'ws://localhost:8080/ws';
const timeoutMs = Number(process.argv[3] || 20000);
const expectedUser = process.argv[4] || null;
const readyFile = process.argv[5] || null;

const out = {
  url,
  connected: false,
  hello: null,
  created: null,
  createdUserName: null,
  messages: [],
  error: null,
};

const fs = await import('node:fs');

function emit(code) {
  process.stdout.write(JSON.stringify(out) + '\n');
  process.exit(code);
}

if (typeof WebSocket === 'undefined') {
  out.error = 'global WebSocket is not available in this Node runtime';
  emit(1);
}

let ws;
try {
  ws = new WebSocket(url);
} catch (e) {
  out.error = `connect threw: ${e.message}`;
  emit(1);
}

const deadline = setTimeout(() => {
  cleanup();
  emit(out.hello && out.created ? 0 : 1);
}, timeoutMs);

function cleanup() {
  clearTimeout(deadline);
  try { ws.close(); } catch { /* ignore */ }
}

ws.addEventListener('open', () => {
  out.connected = true;
});

ws.addEventListener('message', (ev) => {
  const raw = typeof ev.data === 'string' ? ev.data : String(ev.data);
  out.messages.push(raw.slice(0, 4000));
  let msg;
  try { msg = JSON.parse(raw); } catch { return; }
  if (msg && msg.type === 'hello' && !out.hello) {
    out.hello = msg;
    if (readyFile) {
      try { fs.writeFileSync(readyFile, 'HELLO\n'); } catch { /* ignore */ }
    }
  }
  if (msg && msg.type === 'comment.created') {
    const name = msg?.comment?.userName ?? null;
    out.createdUserName = name;
    if (!expectedUser || name === expectedUser) {
      out.created = msg;
      cleanup();
      emit(0);
    } else if (!out.firstCreated) {
      // keep the first non-matching frame for diagnostics only
      out.firstCreated = msg;
    }
  }
});

ws.addEventListener('error', (e) => {
  out.error = `ws error: ${e?.message || 'unknown'}`;
});

ws.addEventListener('close', () => {
  out.connected = out.connected || false;
});

// Fallback: if the runtime never fires `open` but messages arrive, the flags above still work.
