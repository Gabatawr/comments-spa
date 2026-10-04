/**
 * realtime.service.ts — WebSocket-клиент `/ws` (docs/API-v2.md §2.9).
 * Принимает `hello` / `comment.created` / `pong`, держит reconnect с backoff,
 * ping каждые 25 с и watchdog по устареванию соединения.
 */

import { Injectable, signal } from '@angular/core';
import { Subject } from 'rxjs';
import { CommentDto } from './models';

export type RealtimeState =
  | 'connecting'
  | 'online'
  | 'offline'
  | 'reconnecting'
  | 'error'
  | 'stopped'
  | 'unsupported';

const PING_INTERVAL_MS = 25000;
const WATCHDOG_INTERVAL_MS = 15000;
const STALE_AFTER_MS = 70000;
const MAX_BACKOFF_MS = 30000;

interface WsEnvelope {
  type?: string;
  comment?: CommentDto;
  clients?: number;
}

@Injectable({ providedIn: 'root' })
export class RealtimeService {
  readonly status = signal<RealtimeState>('stopped');
  readonly clients = signal(0);
  readonly commentCreated$ = new Subject<CommentDto>();

  private socket: WebSocket | null = null;
  private attempts = 0;
  private stopped = true;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private pingTimer: ReturnType<typeof setInterval> | null = null;
  private watchdogTimer: ReturnType<typeof setInterval> | null = null;
  private lastMessageAt = 0;
  private readonly onVisibility = () => {
    if (typeof document !== 'undefined' && document.visibilityState === 'visible' && !this.stopped) {
      if (!this.socket || this.socket.readyState === WebSocket.CLOSED) {
        this.attempts = 0;
        this.connect();
      }
    }
  };

  start(): void {
    if (!this.stopped) {
      return;
    }
    this.stopped = false;
    this.connect();
    if (typeof document !== 'undefined') {
      document.addEventListener('visibilitychange', this.onVisibility);
    }
  }

  stop(): void {
    this.stopped = true;
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    this.stopTimers();
    if (typeof document !== 'undefined') {
      document.removeEventListener('visibilitychange', this.onVisibility);
    }
    if (this.socket) {
      try {
        this.socket.close(1000, 'client shutdown');
      } catch {
        /* ignore */
      }
      this.socket = null;
    }
    this.status.set('stopped');
  }

  private endpoint(): string | null {
    if (typeof location === 'undefined') {
      return null;
    }
    const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    return `${protocol}//${location.host}/ws`;
  }

  private connect(): void {
    if (this.stopped) {
      return;
    }
    const target = this.endpoint();
    if (!target || typeof WebSocket === 'undefined') {
      this.status.set('unsupported');
      return;
    }
    if (this.socket && (this.socket.readyState === WebSocket.CONNECTING || this.socket.readyState === WebSocket.OPEN)) {
      return;
    }
    this.status.set('connecting');
    let socket: WebSocket;
    try {
      socket = new WebSocket(target);
    } catch {
      this.status.set('offline');
      this.scheduleReconnect();
      return;
    }
    this.socket = socket;

    socket.onopen = () => {
      this.attempts = 0;
      this.status.set('online');
      this.startTimers();
    };
    socket.onmessage = (event: MessageEvent) => {
      this.lastMessageAt = Date.now();
      if (typeof event.data !== 'string') {
        return;
      }
      let message: WsEnvelope;
      try {
        message = JSON.parse(event.data) as WsEnvelope;
      } catch {
        return; // невалидный JSON игнорируем, соединение не рвём
      }
      if (!message || typeof message.type !== 'string') {
        return;
      }
      if (message.type === 'hello') {
        if (typeof message.clients === 'number') {
          this.clients.set(message.clients);
        }
      } else if (message.type === 'comment.created' && message.comment) {
        this.commentCreated$.next(message.comment);
      }
    };
    socket.onerror = () => {
      this.status.set('error');
    };
    socket.onclose = () => {
      this.stopTimers();
      if (!this.stopped) {
        this.status.set('offline');
        this.scheduleReconnect();
      }
    };
  }

  private scheduleReconnect(): void {
    if (this.stopped) {
      return;
    }
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
    }
    const base = Math.min(MAX_BACKOFF_MS, 1000 * 2 ** Math.min(this.attempts, 5));
    const delay = base + Math.floor(Math.random() * 500);
    this.attempts += 1;
    this.status.set('reconnecting');
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.connect();
    }, delay);
  }

  private stopTimers(): void {
    if (this.pingTimer) {
      clearInterval(this.pingTimer);
      this.pingTimer = null;
    }
    if (this.watchdogTimer) {
      clearInterval(this.watchdogTimer);
      this.watchdogTimer = null;
    }
  }

  private startTimers(): void {
    this.stopTimers();
    this.lastMessageAt = Date.now();
    this.pingTimer = setInterval(() => {
      if (this.socket && this.socket.readyState === WebSocket.OPEN) {
        try {
          this.socket.send(JSON.stringify({ type: 'ping' }));
        } catch {
          /* соединение закроется само */
        }
      }
    }, PING_INTERVAL_MS);
    this.watchdogTimer = setInterval(() => {
      if (
        this.socket &&
        this.socket.readyState === WebSocket.OPEN &&
        Date.now() - this.lastMessageAt > STALE_AFTER_MS
      ) {
        try {
          this.socket.close();
        } catch {
          /* ignore */
        }
      }
    }, WATCHDOG_INTERVAL_MS);
  }
}
