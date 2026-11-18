import { h } from '../dom';
import { mmss } from '../format';
import { registerWidget } from '../registry';

export type Phase = 'focus' | 'break';

/** Timer state. `endsAt` is set while running; `remainingMs` while paused. Pure transitions, unit-tested. */
export interface Pomo { phase: Phase; running: boolean; endsAt: number | null; remainingMs: number }

export const durations = (focusMin: number, breakMin: number): Record<Phase, number> =>
  ({ focus: focusMin * 60_000, break: breakMin * 60_000 });

export const initial = (d: Record<Phase, number>): Pomo => ({ phase: 'focus', running: false, endsAt: null, remainingMs: d.focus });

export const remaining = (p: Pomo, now: number): number => (p.running && p.endsAt != null ? Math.max(0, p.endsAt - now) : p.remainingMs);

export const start = (p: Pomo, now: number): Pomo => (p.running ? p : { ...p, running: true, endsAt: now + p.remainingMs });

export const pause = (p: Pomo, now: number): Pomo => (p.running ? { ...p, running: false, endsAt: null, remainingMs: remaining(p, now) } : p);

/** Advances the timer; when a phase ends it switches to the other phase, stopped, and reports it. */
export function tick(p: Pomo, now: number, d: Record<Phase, number>): { next: Pomo; finished: Phase | null } {
  if (!p.running || remaining(p, now) > 0) return { next: p, finished: null };
  const phase: Phase = p.phase === 'focus' ? 'break' : 'focus';
  return { next: { phase, running: false, endsAt: null, remainingMs: d[phase] }, finished: p.phase };
}

const minutes = (v: unknown, fallback: number, max: number) =>
  typeof v === 'number' && Number.isFinite(v) && v >= 1 ? Math.min(max, Math.round(v)) : fallback;

registerWidget('pomodoro', {
  needs: ['tick'],
  always: true, // must ring even when another page is showing
  create(box, { options }) {
    const d = durations(minutes(options['focusMin'], 25, 120), minutes(options['breakMin'], 5, 60));
    let p = initial(d);
    const phase = h('div', 'pomo-phase');
    const time = h('div', 'pomo-time');
    const toggle = h('button', 'pomo-btn');
    const reset = h('button', 'pomo-btn secondary', 'Reset');
    toggle.type = reset.type = 'button';
    box.append(phase, time, h('div', 'pomo-actions', toggle, reset));
    const render = () => {
      phase.textContent = p.phase === 'focus' ? 'Focus' : 'Break';
      time.textContent = mmss(remaining(p, Date.now()));
      toggle.textContent = p.running ? 'Pause' : 'Start';
      box.dataset['phase'] = p.phase;
    };
    toggle.addEventListener('click', () => { p = p.running ? pause(p, Date.now()) : start(p, Date.now()); render(); });
    reset.addEventListener('click', () => { p = initial(d); render(); });
    render();
    return {
      update() {
        const r = tick(p, Date.now(), d);
        p = r.next;
        if (r.finished) {
          box.classList.add('ring');
          setTimeout(() => box.classList.remove('ring'), 4000);
          navigator.vibrate?.([300, 150, 300]);
        }
        render();
      },
    };
  },
});
