import './widgets/clock';
import './widgets/nowPlaying';
import './widgets/pomodoro';
import './widgets/stat';
import { h } from './dom';
import { age } from './format';
import { Page } from './layout';
import type { LiveView, WidgetConfig } from './registry';
import { apply, emptyState, isStale, type StateMessage } from './store';
import { getJson, subscribe, type LayoutDoc } from './transport';

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
  get receivedAt() { return state.mediaAt; },
};

const pager = document.getElementById('pager')!;
const badge = h('div', 'status');
document.body.append(badge);
const page = new Page(pager, live);
page.mount(FALLBACK_LAYOUT);

let streaming = false;

function onMessage(msg: StateMessage): void {
  for (const kind of apply(state, msg, Date.now())) page.emit(kind);
}

subscribe((e) => {
  if (e.type === 'open') streaming = true;
  else if (e.type === 'close') streaming = false;
  else onMessage(e.msg);
});

/** Polling fallback: only while the stream is down or silent. */
async function poll(): Promise<void> {
  if (!streaming || isStale(state, Date.now())) {
    try { onMessage(await getJson<StateMessage>('/state')); } catch { /* offline: the badge says so */ }
  }
  setTimeout(poll, 2000);
}

async function loadLayout(): Promise<void> {
  try {
    const doc = await getJson<LayoutDoc>('/layout');
    if (doc.widgets.length > 0) page.mount(doc.widgets);
  } catch {
    setTimeout(loadLayout, 5000); // keep the fallback page and try again
  }
}

/** Offline / stale badge with the age of the data (PhoneBox showed no data age, Module 10). */
function renderBadge(now: number): void {
  if (state.receivedAt === 0) { badge.textContent = 'Connecting to your computer…'; badge.dataset['kind'] = 'offline'; return; }
  if (isStale(state, now)) { badge.textContent = `Offline · data from ${age(now - state.receivedAt)}`; badge.dataset['kind'] = 'stale'; return; }
  badge.textContent = '';
  badge.dataset['kind'] = 'ok';
}

setInterval(() => { page.emit('tick'); renderBadge(Date.now()); }, 1000);
document.addEventListener('visibilitychange', () => { page.visible = !document.hidden; page.catchUp(); });
void loadLayout();
void poll();
renderBadge(Date.now());
