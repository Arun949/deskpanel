import { age, gb, mmss, pct, rate } from './format';
import { apply, emptyState, isStale, type StateMessage } from './store';
import { livePosition } from './widgets/nowPlaying';
import { durations, initial, pause, remaining, start, tick } from './widgets/pomodoro';
import { statView } from './widgets/stat';

const metrics = { cpu: 10, ramUsedGb: 8, ramTotalGb: 16, netDownBps: 1000, netUpBps: 50 };
const media = { app: 'Spotify', state: 'playing', title: 'T', artist: 'A', album: null, positionMs: 1000, durationMs: 10_000 };
const msg = (o: Partial<StateMessage>): StateMessage =>
  ({ runId: 'run1', version: 1, ts: 0, full: true, metrics: null, media: null, volume: null, ...o });

describe('store.apply', () => {
  it('a full message sets every section and reports what changed', () => {
    const s = emptyState();
    expect(apply(s, msg({ metrics, media }), 100)).toEqual(['metrics', 'media']);
    expect(s.metrics?.cpu).toBe(10);
    expect(s.mediaAt).toBe(100);
  });

  it('a partial message keeps sections it does not carry', () => {
    const s = emptyState();
    apply(s, msg({ metrics, media }), 100);
    expect(apply(s, msg({ version: 2, full: false, metrics: { ...metrics, cpu: 50 } }), 200)).toEqual(['metrics']);
    expect(s.media?.title).toBe('T');
    expect(s.mediaAt).toBe(100);
  });

  it('a full message with media null clears it (playback stopped)', () => {
    const s = emptyState();
    apply(s, msg({ media }), 100);
    expect(apply(s, msg({ version: 2, full: true, media: null }), 200)).toEqual(['media']);
    expect(s.media).toBeNull();
  });

  it('ignores an older version from the same run (a late poll answer)', () => {
    const s = emptyState();
    apply(s, msg({ version: 5, metrics }), 100);
    expect(apply(s, msg({ version: 4, metrics: { ...metrics, cpu: 99 } }), 200)).toEqual([]);
    expect(s.metrics?.cpu).toBe(10);
  });

  it('a new runId (agent restarted) replaces everything even with a lower version', () => {
    const s = emptyState();
    apply(s, msg({ version: 50, metrics, media }), 100);
    expect(apply(s, msg({ runId: 'run2', version: 1, full: false, metrics: { ...metrics, cpu: 3 } }), 200)).toEqual(['metrics', 'media']);
    expect(s.media).toBeNull();
    expect(s.runId).toBe('run2');
  });

  it('is stale before the first message and after 15 s of silence', () => {
    const s = emptyState();
    expect(isStale(s, 0)).toBe(true);
    apply(s, msg({}), 1000);
    expect(isStale(s, 15_000)).toBe(false);
    expect(isStale(s, 16_001)).toBe(true);
  });
});

describe('widgets (pure parts)', () => {
  it('stat shows unknown as a dash, never 0', () => {
    expect(statView('cpu', null)).toMatchObject({ value: '—', level: null });
    expect(statView('ram', metrics)).toMatchObject({ value: '50%', sub: '8.0 / 16 GB', level: 50 });
    expect(statView('net', metrics).value).toBe('↓ 1.0 KB/s');
  });

  it('now playing advances position only while playing, capped at the duration', () => {
    expect(livePosition(media, 100, 1100)).toBe(2000);
    expect(livePosition({ ...media, state: 'paused' }, 100, 1100)).toBe(1000);
    expect(livePosition(media, 0, 60_000)).toBe(10_000);
    expect(livePosition({ ...media, positionMs: null }, 0, 0)).toBeNull();
  });

  it('pomodoro runs, pauses and switches phase when time is up', () => {
    const d = durations(25, 5);
    let p = start(initial(d), 0);
    expect(remaining(p, 60_000)).toBe(24 * 60_000);
    p = pause(p, 60_000);
    expect(remaining(p, 999_999)).toBe(24 * 60_000);
    p = start(p, 100_000);
    const done = tick(p, 100_000 + 24 * 60_000, d);
    expect(done.finished).toBe('focus');
    expect(done.next).toEqual({ phase: 'break', running: false, endsAt: null, remainingMs: 5 * 60_000 });
    expect(tick(done.next, 0, d).finished).toBeNull();
  });

  it('formats', () => {
    expect([pct(null), pct(12.6)]).toEqual(['—', '13%']);
    expect(gb(1.25, null)).toBe('1.3 GB');
    expect([rate(0), rate(999), rate(1500), rate(250_000_000)]).toEqual(['0 B/s', '999 B/s', '1.5 KB/s', '250 MB/s']);
    expect([mmss(61_000), mmss(3_725_000), mmss(null)]).toEqual(['1:01', '1:02:05', '—']);
    expect([age(3000), age(120_000)]).toEqual(['3 s ago', '2 min ago']);
  });
});
