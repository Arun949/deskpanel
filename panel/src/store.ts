// Live state from the agent, merged from stream messages (full or only-changed sections).

export interface Metrics { cpu: number | null; ramUsedGb: number | null; ramTotalGb: number | null; netDownBps: number | null; netUpBps: number | null }
export interface Media { app: string; state: string; title: string | null; artist: string | null; album: string | null; positionMs: number | null; durationMs: number | null; playing?: boolean }
export interface Volume { level: number | null; muted: boolean | null }

/** One message from GET /api/v1/state or the stream. Sections are null when unchanged (partial) or unknown (full). */
export interface StateMessage {
  runId: string;
  version: number;
  ts: number;
  full: boolean;
  metrics: Metrics | null;
  media: Media | null;
  volume: Volume | null;
  /** Changes when the computer's layout changes (an editor save): reload it at once. */
  layoutVersion?: string | null;
}

export interface LiveState {
  runId: string | null;
  version: number;
  metrics: Metrics | null;
  media: Media | null;
  volume: Volume | null;
  /** When the panel last received anything (local clock), for the stale badge. */
  receivedAt: number;
  /** Agent time of the newest message, so data age can be shown. */
  agentTs: number;
  /** Local time the media section last changed, to advance a playing track's position between messages. */
  mediaAt: number;
}

export type Section = 'metrics' | 'media' | 'volume';

export const emptyState = (): LiveState => ({ runId: null, version: -1, metrics: null, media: null, volume: null, receivedAt: 0, agentTs: 0, mediaAt: 0 });

/**
 * Applies a message and returns the sections that changed. A different runId means the agent restarted: its versions
 * start again, so the message replaces everything. Older versions from the same run are ignored (a late poll answer).
 */
export function apply(state: LiveState, msg: StateMessage, now: number): Section[] {
  const restarted = state.runId !== null && msg.runId !== state.runId;
  if (!restarted && state.runId !== null && msg.version < state.version) return [];
  const changed: Section[] = [];
  const take = <K extends Section>(key: K, value: LiveState[K]) => {
    if (JSON.stringify(state[key]) !== JSON.stringify(value)) { state[key] = value; changed.push(key); }
  };
  if (msg.full || restarted) {
    take('metrics', msg.metrics);
    take('media', msg.media);
    take('volume', msg.volume);
  } else {
    if (msg.metrics) take('metrics', msg.metrics);
    if (msg.media) take('media', msg.media);
    if (msg.volume) take('volume', msg.volume);
  }
  state.runId = msg.runId;
  state.version = msg.version;
  state.agentTs = msg.ts;
  state.receivedAt = now;
  if (changed.includes('media')) state.mediaAt = now;
  return changed;
}

/** Live data counts as stale after 15 s without a message (the agent sends a full state at least every 10 s). */
export const isStale = (state: LiveState, now: number, afterMs = 15_000): boolean =>
  state.receivedAt === 0 || now - state.receivedAt > afterMs;
