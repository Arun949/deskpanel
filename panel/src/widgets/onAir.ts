import { h } from '../dom';
import { registerWidget } from '../registry';
import type { Presence } from '../store';

type Watch = 'any' | 'camera' | 'mic';

/** What the light shows. Pure, so it is unit-tested. Unknown (no adapter on that computer) is never shown as "off". */
export function onAirView(p: Presence | null, watch: Watch): { on: boolean | null; title: string; sub: string } {
  const camera = p?.camera ?? null, mic = p?.mic ?? null;
  const relevant = watch === 'camera' ? [camera] : watch === 'mic' ? [mic] : [camera, mic];
  if (relevant.every((v) => v === null)) return { on: null, title: 'On Air', sub: 'not available on this computer' };
  const on = relevant.some((v) => v === true);
  if (!on) return { on: false, title: 'Off air', sub: 'camera and mic are free' };
  const what = [camera && watch !== 'mic' ? 'camera' : null, mic && watch !== 'camera' ? 'mic' : null].filter(Boolean).join(' + ');
  return { on: true, title: 'ON AIR', sub: `${what} in use` };
}

registerWidget('on-air', {
  needs: ['presence'],
  create(box, { options, live }) {
    const watch: Watch = options['watch'] === 'camera' || options['watch'] === 'mic' ? options['watch'] : 'any';
    const glow = options['glowScreen'] !== false;
    const title = h('div', 'onair-title');
    const sub = h('div', 'onair-sub');
    box.classList.add('onair');
    box.append(title, sub);
    const set = (on: boolean) => {
      box.dataset['on'] = String(on);
      // the whole screen glows, so the phone works as a door sign even when the widget is small
      if (glow) document.documentElement.classList.toggle('on-air', on);
    };
    return {
      update() {
        const v = onAirView(live.presence, watch);
        title.textContent = v.title;
        sub.textContent = v.sub;
        set(v.on === true);
      },
      destroy() { if (glow) document.documentElement.classList.remove('on-air'); },
    };
  },
});
