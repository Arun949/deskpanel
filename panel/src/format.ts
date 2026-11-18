// Formatting shared by widgets. Unknown values render as an em dash, never as 0.

export const DASH = '—';

export const pct = (v: number | null | undefined): string => (v == null ? DASH : `${Math.round(v)}%`);

export function gb(used: number | null | undefined, total: number | null | undefined): string {
  if (used == null) return DASH;
  return total == null ? `${used.toFixed(1)} GB` : `${used.toFixed(1)} / ${Math.round(total)} GB`;
}

export function rate(bps: number | null | undefined): string {
  if (bps == null) return DASH;
  const units = ['B/s', 'KB/s', 'MB/s', 'GB/s'];
  let v = bps, i = 0;
  while (v >= 1000 && i < units.length - 1) { v /= 1000; i++; }
  return `${v >= 100 || i === 0 ? Math.round(v) : v.toFixed(1)} ${units[i]}`;
}

export function mmss(ms: number | null | undefined): string {
  if (ms == null || ms < 0) return DASH;
  const s = Math.floor(ms / 1000);
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
  const two = (n: number) => String(n).padStart(2, '0');
  return h > 0 ? `${h}:${two(m)}:${two(sec)}` : `${m}:${two(sec)}`;
}

/** "3 s ago" style age of data, for the stale badge. */
export function age(ms: number): string {
  const s = Math.max(0, Math.round(ms / 1000));
  if (s < 60) return `${s} s ago`;
  const m = Math.round(s / 60);
  return m < 60 ? `${m} min ago` : `${Math.round(m / 60)} h ago`;
}
