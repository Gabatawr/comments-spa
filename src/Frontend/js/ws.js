/**
 * ws.js — WebSocket-клиент /ws: приём `comment.created`, reconnect с backoff,
 * ping/pong, статус соединения.
 */

const PING_INTERVAL_MS = 25000;
const WATCHDOG_INTERVAL_MS = 15000;
const STALE_AFTER_MS = 70000;
const MAX_BACKOFF_MS = 30000;

export function createRealtime(options = {}) {
  const {
    onCommentCreated,
    onStatus,
    onHello,
    url,
    silent = false,
  } = options;

  let socket = null;
  let attempts = 0;
  let stopped = true;
  let reconnectTimer = null;
  let pingTimer = null;
  let watchdogTimer = null;
  let lastMessageAt = 0;

  function status(state, detail) {
    if (!silent && typeof onStatus === 'function') onStatus(state, detail);
  }

  function endpoint() {
    if (url) return url;
    if (typeof location === 'undefined') return null;
    const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    return `${protocol}//${location.host}/ws`;
  }

  function stopTimers() {
    if (pingTimer) {
      clearInterval(pingTimer);
      pingTimer = null;
    }
    if (watchdogTimer) {
      clearInterval(watchdogTimer);
      watchdogTimer = null;
    }
  }

  function startTimers() {
    stopTimers();
    lastMessageAt = Date.now();
    pingTimer = setInterval(() => {
      if (socket && socket.readyState === 1) {
        try {
          socket.send(JSON.stringify({ type: 'ping' }));
        } catch {
          /* соединение закроется само */
        }
      }
    }, PING_INTERVAL_MS);
    watchdogTimer = setInterval(() => {
      if (socket && socket.readyState === 1 && Date.now() - lastMessageAt > STALE_AFTER_MS) {
        try {
          socket.close();
        } catch {
          /* ignore */
        }
      }
    }, WATCHDOG_INTERVAL_MS);
  }

  function scheduleReconnect() {
    if (stopped) return;
    if (reconnectTimer) clearTimeout(reconnectTimer);
    const base = Math.min(MAX_BACKOFF_MS, 1000 * 2 ** Math.min(attempts, 5));
    const delay = base + Math.floor(Math.random() * 500);
    attempts += 1;
    status('reconnecting', delay);
    reconnectTimer = setTimeout(() => {
      reconnectTimer = null;
      connect();
    }, delay);
  }

  function connect() {
    if (stopped) return;
    const target = endpoint();
    if (!target || typeof WebSocket === 'undefined') {
      status('unsupported');
      return;
    }
    if (socket && (socket.readyState === 0 || socket.readyState === 1)) return;

    status('connecting');
    try {
      socket = new WebSocket(target);
    } catch (error) {
      socket = null;
      status('offline', error);
      scheduleReconnect();
      return;
    }

    socket.onopen = () => {
      attempts = 0;
      status('online');
      startTimers();
    };

    socket.onmessage = (event) => {
      lastMessageAt = Date.now();
      if (!event || typeof event.data !== 'string') return;
      let message = null;
      try {
        message = JSON.parse(event.data);
      } catch {
        return; // невалидный JSON игнорируем (как в контракте)
      }
      if (!message || typeof message.type !== 'string') return;
      if (message.type === 'hello') {
        if (typeof onHello === 'function') onHello(message);
      } else if (message.type === 'comment.created') {
        if (message.comment && typeof onCommentCreated === 'function') onCommentCreated(message.comment);
      }
      // pong и прочие типы — просто обновляют lastMessageAt
    };

    socket.onerror = () => {
      status('error');
    };

    socket.onclose = () => {
      stopTimers();
      status('offline');
      if (!stopped) scheduleReconnect();
    };
  }

  function handleVisibility() {
    if (typeof document === 'undefined') return;
    if (document.visibilityState === 'visible' && !stopped) {
      if (!socket || socket.readyState === 3) {
        attempts = 0;
        connect();
      }
    }
  }

  return {
    start() {
      stopped = false;
      connect();
      if (typeof document !== 'undefined') {
        document.addEventListener('visibilitychange', handleVisibility);
      }
      return this;
    },
    stop() {
      stopped = true;
      if (reconnectTimer) {
        clearTimeout(reconnectTimer);
        reconnectTimer = null;
      }
      stopTimers();
      if (typeof document !== 'undefined') {
        document.removeEventListener('visibilitychange', handleVisibility);
      }
      if (socket) {
        try {
          socket.close(1000, 'client shutdown');
        } catch {
          /* ignore */
        }
        socket = null;
      }
      status('stopped');
    },
    isConnected: () => Boolean(socket && socket.readyState === 1),
    getState: () => ({
      attempts,
      readyState: socket ? socket.readyState : 3,
      endpoint: endpoint(),
    }),
  };
}

export default { createRealtime };
