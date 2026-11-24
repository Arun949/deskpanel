import './widgets/clock';
import './widgets/nowPlaying';
import './widgets/onAir';
import './widgets/pomodoro';
import './widgets/stat';
import { setActions } from './widgets/controls';
import { h } from './dom';
import { age } from './format';
import { Page } from './layout';
import type { LiveView, WidgetConfig } from './registry';
import { apply, emptyState, isStale, type StateMessage } from './store';
import { getJson, subscribe, type LayoutDoc } from './transport';
import { applyScreenCare } from './screen';

/** Shown until the agent's layout arrives (first start, or the PC is not reachable yet). */
const FALLBACK_LAYOUT: WidgetConfig[] = [
  { id: 'clock', type: 'clock', x: 0, y: 0, w: 8, h: 6 },
  { id: 'pomodoro', type: 'pomodoro', x: 8, y: 0, w: 4, h: 6 },
];

const state = emptyState();
const live: LiveView = {
  get metrics() { return state.metrics; },
  get media() { return state.media; },
  get volume() { return state.volume; },
  get presence() { return state.presence; },
  get receivedAt() { return state.mediaAt; },
};

const pager = document.getElementById('pager')!;
const badge = h('div', 'status');
const via = h('div', 'via'); // a small "USB" / "Wi-Fi" mark: which route the phone uses right now
document.body.append(badge, via);
const page = new Page(pager, live);
page.mount(FALLBACK_LAYOUT);

let streaming = false;
let knownLayoutVersion: string | null = null;

function onMessage(msg: StateMessage): void {
  for (const kind of apply(state, msg, Date.now())) page.emit(kind);
  if (msg.layoutVersion && msg.layoutVersion !== knownLayoutVersion) {
    const first = knownLayoutVersion === null;
    knownLayoutVersion = msg.layoutVersion;
    if (!first) void loadLayout(false); // saved in the editor: show it now, not in 30 s
  }
}

subscribe((e) => {
  if (e.type === 'open') { streaming = true; via.textContent = e.via === 'wifi' ? 'Wi-Fi' : e.via === 'usb' ? 'USB' : ''; }
  else if (e.type === 'close') { streaming = false; via.textContent = ''; }
  else onMessage(e.msg);
});

/** Polling fallback: only while the stream is down or silent. */
async function poll(): Promise<void> {
  if (!streaming || isStale(state, Date.now())) {
    try { onMessage(await getJson<StateMessage>('/state')); } catch { /* offline: the badge says so */ }
  }
  setTimeout(poll, 2000);
}

let layoutJson = '';

/**
 * Loads the layout, and checks it again every 30 s: a change made on the computer (layout.json, actions) shows up
 * without restarting the app. Widgets are re-created only when something actually changed.
 */
let layoutTimer: ReturnType<typeof setTimeout> | undefined;

async function loadLayout(schedule = true): Promise<void> {
  let next = 30_000;
  try {
    const doc = await getJson<LayoutDoc>('/layout');
    const json = JSON.stringify(doc);
    if (json !== layoutJson && doc.widgets.length > 0) {
      layoutJson = json;
      setActions(doc.actions ?? []);
      page.mount(doc.widgets);
    }
  } catch {
    if (!layoutJson) next = 5000; // still on the fallback page: try again soon
  }
  if (schedule) { clearTimeout(layoutTimer); layoutTimer = setTimeout(loadLayout, next); }
}

/** Offline / stale badge with the age of the data (PhoneBox showed no data age, Module 10). */
function renderBadge(now: number): void {
  if (state.receivedAt === 0) { badge.textContent = 'Connecting to your computer…'; badge.dataset['kind'] = 'offline'; return; }
  if (isStale(state, now)) { badge.textContent = `Offline · data from ${age(now - state.receivedAt)}`; badge.dataset['kind'] = 'stale'; return; }
  badge.textContent = '';
  badge.dataset['kind'] = 'ok';
}

setInterval(() => { page.emit('tick'); renderBadge(Date.now()); applyScreenCare(document.documentElement, new Date()); }, 1000);
document.addEventListener('visibilitychange', () => { page.visible = !document.hidden; page.catchUp(); });
void loadLayout();
void poll();
renderBadge(Date.now());
