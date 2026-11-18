// The widget catalogue (agent/…/widgets.json) and the panel's registry must match, and every widget must render with
// its default options (PhoneBox's "every catalogue widget builds" test, Module 11).
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { Page } from './layout';
import { widgetTypes, type LiveView } from './registry';
import { isNight, pixelShift } from './screen';
import './widgets/clock';
import './widgets/controls';
import './widgets/nowPlaying';
import './widgets/pomodoro';
import './widgets/stat';

interface Def { type: string; w: number; h: number; options: Array<{ key: string; default: unknown }> }
// tests run from panel/ (npm test); the catalogue is the agent's embedded resource
const catalog = JSON.parse(readFileSync(resolve(process.cwd(), '../agent/DeskPanel.Agent/Layout/widgets.json'), 'utf8')) as Def[];

const LIVE: LiveView = {
  metrics: { cpu: 12, ramUsedGb: 8, ramTotalGb: 16, netDownBps: 1000, netUpBps: 10 },
  media: { app: 'Spotify', state: 'playing', title: 'T', artist: 'A', album: null, positionMs: 1000, durationMs: 9000 },
  volume: { level: 40, muted: false },
  receivedAt: 0,
};

describe('widget catalogue', () => {
  it('lists exactly the widgets the panel registers', () => {
    expect(catalog.map((d) => d.type).sort()).toEqual(widgetTypes());
  });

  it.each(catalog.map((d) => [d.type, d] as const))('%s renders with its default options', (_type, def) => {
    const root = document.createElement('div');
    const page = new Page(root, LIVE);
    const options = Object.fromEntries(def.options.map((o) => [o.key, o.default]));
    page.mount([{ id: 'x', type: def.type, x: 0, y: 0, w: def.w, h: def.h, options }]);
    for (const kind of ['tick', 'metrics', 'media', 'volume', 'layout'] as const) page.emit(kind);
    const box = root.querySelector('.card')!;
    expect(box).not.toBeNull();
    expect(box.textContent).not.toMatch(new RegExp(`^${def.type}: `)); // the error box a throwing widget shows
  });
});

describe('screen care', () => {
  it('dims only at night', () => {
    expect(isNight(new Date(2026, 10, 18, 23, 30))).toBe(true);
    expect(isNight(new Date(2026, 10, 18, 6, 59))).toBe(true);
    expect(isNight(new Date(2026, 10, 18, 7, 0))).toBe(false);
    expect(isNight(new Date(2026, 10, 18, 15, 0))).toBe(false);
  });

  it('shifts a few pixels, one step every 2 minutes', () => {
    const a = pixelShift(new Date(0)), b = pixelShift(new Date(120_000));
    expect(a).not.toEqual(b);
    expect(pixelShift(new Date(119_000))).toEqual(a);
    for (let t = 0; t < 30; t++) {
      const { x, y } = pixelShift(new Date(t * 120_000));
      expect(Math.abs(x)).toBeLessThanOrEqual(2);
      expect(Math.abs(y)).toBeLessThanOrEqual(2);
    }
  });
});
