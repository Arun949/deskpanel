import { h } from '../dom';
import { mmss } from '../format';
import { registerWidget } from '../registry';
import type { Media } from '../store';

/** Position now, advancing a playing track between agent messages (they arrive every 2 s). */
export function livePosition(media: Media, receivedAt: number, now: number): number | null {
  if (media.positionMs == null) return null;
  const pos = media.state === 'playing' ? media.positionMs + Math.max(0, now - receivedAt) : media.positionMs;
  return media.durationMs != null ? Math.min(pos, media.durationMs) : pos;
}

registerWidget('now-playing', {
  needs: ['media', 'tick'],
  create(box, { live }) {
    const title = h('div', 'np-title');
    const artist = h('div', 'np-artist');
    const bar = h('div', 'np-bar', h('i'));
    const fill = bar.firstElementChild as HTMLElement;
    const times = h('div', 'np-times');
    box.append(h('div', 'w-title', 'Now playing'), title, artist, bar, times);
    return {
      update() {
        const m = live.media;
        if (!m) {
          title.textContent = 'Nothing playing';
          artist.textContent = '';
          fill.style.width = '0';
          times.textContent = '';
          box.classList.add('idle');
          return;
        }
        box.classList.remove('idle');
        title.textContent = m.title ?? 'Unknown track';
        artist.textContent = [m.artist, m.app].filter(Boolean).join(' · ') + (m.state === 'paused' ? ' · paused' : '');
        const pos = livePosition(m, live.receivedAt, Date.now());
        fill.style.width = pos != null && m.durationMs ? `${(100 * pos) / m.durationMs}%` : '0';
        times.textContent = `${mmss(pos)} / ${mmss(m.durationMs)}`;
      },
    };
  },
});
