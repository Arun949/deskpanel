import { h } from '../dom';
import { DASH } from '../format';
import { registerWidget } from '../registry';
import { control, type PublicAction } from '../transport';

/** Labels of the actions the agent listed with the layout (ids only ever go back to the agent). */
const known = new Map<string, PublicAction>();
export function setActions(actions: readonly PublicAction[]): void {
  known.clear();
  for (const a of actions) known.set(a.id, a);
}

/** A short message under a widget after a refused tap; clears itself. */
function feedback(box: HTMLElement): (message: string | null) => void {
  const line = h('div', 'ctl-msg');
  box.append(line);
  let timer: ReturnType<typeof setTimeout> | undefined;
  return (message) => {
    clearTimeout(timer);
    line.textContent = message ?? '';
    if (message) timer = setTimeout(() => { line.textContent = ''; }, 5000);
  };
}

function button(label: string, title: string, onTap: () => Promise<void>, cls = 'ctl-btn'): HTMLButtonElement {
  const b = h('button', cls, label);
  b.type = 'button';
  b.title = title;
  b.setAttribute('aria-label', title);
  b.addEventListener('click', async () => {
    if (b.disabled) return;
    b.disabled = true; // one request per tap; the agent dedupes retries by Idempotency-Key anyway
    try { await onTap(); } finally { b.disabled = false; }
  });
  return b;
}

registerWidget('media-controls', {
  needs: ['media'],
  create(box, { live }) {
    const say = feedback(box);
    const send = (op: string) => async () => say((await control(`/media/${op}`)).message);
    const play = button('⏯', 'Play or pause', send('play-pause'), 'ctl-btn big');
    box.prepend(h('div', 'ctl-row',
      button('⏮', 'Previous track', send('prev')),
      play,
      button('⏭', 'Next track', send('next'))));
    return {
      update() { play.textContent = live.media?.state === 'playing' ? '⏸' : '▶'; },
    };
  },
});

registerWidget('volume', {
  needs: ['volume'],
  create(box, { live }) {
    const say = feedback(box);
    const level = h('div', 'vol-level');
    const fill = h('i');
    const bar = h('div', 'vol-bar', fill);
    const step = (delta: number) => async () => {
      const now = live.volume?.level;
      if (now == null) { say('Volume unknown on this computer'); return; }
      say((await control(`/volume?level=${Math.max(0, Math.min(100, now + delta))}`)).message);
    };
    const mute = button('🔇', 'Mute or unmute', async () => {
      say((await control(`/volume?mute=${live.volume?.muted ? 'false' : 'true'}`)).message);
    });
    box.prepend(h('div', 'ctl-row', button('−', 'Volume down', step(-10)), h('div', 'vol-mid', level, bar), button('+', 'Volume up', step(10)), mute));
    return {
      update() {
        const v = live.volume;
        level.textContent = v?.level == null ? DASH : v.muted ? 'Muted' : `${v.level}%`;
        fill.style.width = v?.level == null || v.muted ? '0' : `${v.level}%`;
        mute.classList.toggle('on', v?.muted === true);
      },
    };
  },
});

registerWidget('shortcuts', {
  needs: ['layout'],
  create(box, { options }) {
    const ids = typeof options['actions'] === 'string'
      ? options['actions'].split(',').map((s) => s.trim()).filter(Boolean)
      : [];
    const grid = h('div', 'sc-grid');
    const say = feedback(box);
    box.prepend(h('div', 'w-title', 'Shortcuts'), grid);
    const render = () => {
      const actions = ids.map((id) => known.get(id)).filter((a): a is PublicAction => !!a);
      if (actions.length === 0) {
        grid.replaceChildren(h('div', 'empty', 'No shortcuts yet. Add actions in DeskPanel settings, then list their ids in this widget.'));
        return;
      }
      grid.replaceChildren(...actions.map((a) =>
        button(`${a.icon ?? (a.kind === 'launch' ? '🚀' : '⌨️')} ${a.label}`, a.label,
          async () => say((await control(`/actions/${encodeURIComponent(a.id)}`)).message), 'ctl-btn sc-btn')));
    };
    render();
    return { update: render };
  },
});
