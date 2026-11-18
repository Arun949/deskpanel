import type { StateMessage } from './store';
import type { WidgetConfig } from './registry';

/**
 * The panel never holds the device key. On the phone, the Android shell intercepts /agent/api/* and adds the key
 * (and has already checked the agent's proof). In a desktop browser during development, Vite's dev proxy does it.
 */
export const API = '/agent/api/v1';

/** Marks requests from the panel itself; the native proxy refuses /agent/* without it (no sandboxed frame can add it). */
export const PANEL_HEADERS = { 'X-DeskPanel-Panel': '1' } as const;

/** What the phone may know about an action: never its target. */
export interface PublicAction { id: string; label: string; kind: 'launch' | 'keys'; icon: string | null }

export interface LayoutDoc { schemaVersion: number; widgets: WidgetConfig[]; actions?: PublicAction[] }

/** Outcome of a control request, phrased for the person holding the phone. */
export interface ControlOutcome { ok: boolean; message: string | null }

const MESSAGES: Record<string, string> = {
  'not-allowed': 'Not allowed yet: turn it on in DeskPanel settings on your computer',
  'not-on-layout': 'This control isn’t on this phone’s layout',
  'slow-down': 'Too many taps; wait a moment',
  'no-player-running': 'Open Spotify or Music on your computer first',
  'needs-accessibility-permission': 'Allow DeskPanel under Accessibility in macOS settings',
  'app-missing': 'That app is no longer on your computer',
  'unsupported-on-this-computer': 'Not supported on this computer',
  offline: 'Your computer isn’t connected',
};

function newKey(): string {
  if (typeof crypto.randomUUID === 'function') return crypto.randomUUID();
  const b = crypto.getRandomValues(new Uint8Array(16));
  return Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('');
}

/**
 * Sends a control. Each tap gets its own Idempotency-Key, and a failed request is never retried automatically:
 * the agent may already have acted on it (PhoneBox's rule, Module 3).
 */
export async function control(path: string): Promise<ControlOutcome> {
  try {
    const res = await fetch(API + path, { method: 'POST', headers: { ...PANEL_HEADERS, 'Idempotency-Key': newKey() }, cache: 'no-store' });
    if (res.ok) return { ok: true, message: null };
    let code = `http-${res.status}`;
    try { code = ((await res.json()) as { error?: string }).error ?? code; } catch { /* no body */ }
    return { ok: false, message: MESSAGES[code] ?? `Didn’t work (${code})` };
  } catch {
    return { ok: false, message: MESSAGES['offline']! };
  }
}

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
