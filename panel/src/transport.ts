import type { StateMessage } from './store';
import type { WidgetConfig } from './registry';

/**
 * The panel never holds the device key. On the phone, the Android shell intercepts /agent/api/* and adds the key
 * (and has already checked the agent's proof). In a desktop browser during development, Vite's dev proxy does it.
 */
export const API = '/agent/api/v1';

/** Marks requests from the panel itself; the native proxy refuses /agent/* without it (no sandboxed frame can add it). */
export const PANEL_HEADERS = { 'X-DeskPanel-Panel': '1' } as const;

export interface LayoutDoc { schemaVersion: number; widgets: WidgetConfig[] }

export async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const res = await fetch(API + path, { headers: PANEL_HEADERS, cache: 'no-store', signal });
  if (!res.ok) throw new Error(`${path}: HTTP ${res.status}`);
  return (await res.json()) as T;
}

export type StreamEvent = { type: 'open' } | { type: 'close' } | { type: 'state'; msg: StateMessage };

/** Hooks the Android shell calls (see android/ AgentStream): it owns the stream, because only it holds the key. */
declare global {
  interface Window {
    onAgentStream?: (event: string, data: string) => void;
  }
}

/**
 * Subscribes to live state. On the phone the native shell pushes events through window.onAgentStream; elsewhere an
 * EventSource reads the same stream through the dev proxy. Returns an unsubscribe function.
 */
export function subscribe(listener: (e: StreamEvent) => void): () => void {
  const deliver = (event: string, data: string) => {
    if (event === 'open') return listener({ type: 'open' });
    if (event === 'close') return listener({ type: 'close' });
    if (event !== 'state') return;
    try { listener({ type: 'state', msg: JSON.parse(data) as StateMessage }); } catch { /* ignore a malformed message */ }
  };

  // Inside the Android app the panel is served from the WebView's asset origin; the native shell owns the stream.
  if (location.hostname === 'appassets.androidplatform.net') {
    window.onAgentStream = deliver;
    return () => { window.onAgentStream = undefined; };
  }

  let es: EventSource | null = null;
  let closed = false;
  const connect = () => {
    es = new EventSource(API + '/stream');
    es.onopen = () => deliver('open', '');
    es.addEventListener('state', (e) => deliver('state', (e as MessageEvent<string>).data));
    es.onerror = () => { deliver('close', ''); es?.close(); if (!closed) setTimeout(connect, 2000); };
  };
  connect();
  return () => { closed = true; es?.close(); };
}
