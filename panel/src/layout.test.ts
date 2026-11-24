import { h } from './dom';
import { Page, placement } from './layout';
import { registerWidget, widgetTypes } from './registry';
import './widgets/clock';
import type { LiveView } from './registry';

const LIVE: LiveView = { metrics: null, media: null, volume: null, presence: null, receivedAt: 0 };

const updates: string[] = [];
registerWidget('probe', {
  needs: ['metrics'],
  create() { return { update: (kind, bg) => { updates.push(`${kind}:${bg}`); } }; },
});
registerWidget('broken', {
  needs: ['tick'],
  create() { throw new Error('boom'); },
});

beforeEach(() => { updates.length = 0; });

describe('Page', () => {
  it('delivers data only to widgets that need it', () => {
    const page = new Page(document.createElement('div'), LIVE);
    page.mount([{ id: 'p', type: 'probe', x: 0, y: 0, w: 3, h: 2 }]);
    updates.length = 0;
    page.emit('tick');
    page.emit('metrics');
    expect(updates).toEqual(['metrics:false']);
    expect(page.needs('metrics')).toBe(true);
    expect(page.needs('media')).toBe(false);
  });

  it('marks hidden widgets dirty and catches them up when shown', () => {
    const page = new Page(document.createElement('div'), LIVE);
    page.mount([{ id: 'p', type: 'probe', x: 0, y: 0, w: 3, h: 2 }]);
    updates.length = 0;
    page.visible = false;
    page.emit('metrics');
    expect(updates).toEqual([]);
    page.visible = true;
    page.catchUp();
    expect(updates).toEqual(['metrics:false']);
  });

  it('keeps the page working when one widget throws', () => {
    const root = document.createElement('div');
    const page = new Page(root, LIVE);
    page.mount([
      { id: 'b', type: 'broken', x: 0, y: 0, w: 6, h: 6 },
      { id: 'c', type: 'clock', x: 6, y: 0, w: 6, h: 6 },
    ]);
    expect(root.querySelector('.w-broken')?.textContent).toBe('broken: boom');
    expect(root.querySelector('.clock-time')?.textContent).not.toBe('');
  });

  it('clamps widgets onto the 12 × 6 grid', () => {
    expect(placement({ x: 10, y: 5, w: 8, h: 4 })).toEqual({ x: 10, y: 5, w: 2, h: 1 });
    expect(placement({ x: -3, y: 99, w: 0, h: Number.NaN })).toEqual({ x: 0, y: 5, w: 1, h: 1 });
  });

  it('drops unknown widget types', () => {
    const root = document.createElement('div');
    new Page(root, LIVE).mount([{ id: 'x', type: 'not-a-widget', x: 0, y: 0, w: 1, h: 1 }]);
    expect(root.querySelectorAll('.card')).toHaveLength(0);
  });
});

describe('registry and DOM helper', () => {
  it('rejects duplicate and malformed widget types', () => {
    expect(() => registerWidget('clock', { needs: [], create: () => ({}) })).toThrow(/twice/);
    expect(() => registerWidget('Bad Type', { needs: [], create: () => ({}) })).toThrow(/invalid/);
    expect(widgetTypes()).toContain('clock');
  });

  it('h() never turns text into markup', () => {
    const el = h('div', null, '<img src=x onerror=alert(1)>');
    expect(el.children).toHaveLength(0);
    expect(el.textContent).toBe('<img src=x onerror=alert(1)>');
  });
});
