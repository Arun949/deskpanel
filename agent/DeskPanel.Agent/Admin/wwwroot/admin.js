// DeskPanel settings page. Builds DOM with text nodes only (no innerHTML), talks only to /admin/api on this origin.
'use strict';

const $ = (id) => document.getElementById(id);
function h(tag, attrs, ...kids) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (v === true) el.setAttribute(k, '');
    else if (v !== false && v != null) el.setAttribute(k, String(v));
  }
  for (const kid of kids.flat()) if (kid != null && kid !== false) el.append(kid.nodeType ? kid : String(kid));
  return el;
}

async function api(path, options = {}) {
  const res = await fetch('/admin/api' + path, {
    ...options,
    headers: { 'X-DeskPanel-Admin': '1', ...(options.body ? { 'Content-Type': 'application/json' } : {}) },
    credentials: 'same-origin',
  });
  if (res.status === 401) { document.body.replaceChildren(h('main', {}, h('p', {}, 'Signed out. Open the settings again: deskpanel-agent settings'))); throw new Error('signed out'); }
  if (!res.ok && res.status !== 204) throw new Error(`${path}: ${res.status}`);
  return res.status === 204 ? null : res.json();
}

// ---------------------------------------------------------------- phones and permissions
let permissionInfo = [];

async function loadDevices() {
  permissionInfo = await api('/permissions');
  const devices = await api('/devices');
  const wifi = await api('/wifi');
  $('wifi-status').textContent = wifi.open
    ? `Wi-Fi: listening on port ${wifi.port}` + (wifi.addresses.length ? ` (this computer: ${wifi.addresses.join(', ')})` : '') + '. Only phones allowed below can connect.'
    : 'Wi-Fi: off. Nothing listens on the network until you allow a phone below.';
  const list = $('device-list');
  if (devices.length === 0) { list.replaceChildren(h('p', { class: 'hint' }, 'No phone enrolled yet. Plug one in with the app installed.')); return; }
  list.replaceChildren(...devices.map((d) => h('div', { class: 'card' },
    h('h3', {}, d.model, d.revoked ? ' (revoked)' : ''),
    h('div', { class: 'meta' }, `device ${d.id} · serial ${d.serial} · enrolled ${new Date(d.createdAt).toLocaleString()}`),
    d.revoked ? null : permissionInfo.map((p) => h('label', { class: 'perm' },
      h('input', { type: 'checkbox', checked: d.permissions.includes(p.id), onchange: () => savePermissions(d.id) , 'data-device': d.id, 'data-perm': p.id }),
      h('span', {}, p.description), h('code', {}, p.id))),
    d.revoked ? null : h('label', { class: 'perm wifi' },
      h('input', { type: 'checkbox', checked: d.wifi, onchange: (e) => setWifi(d, e.target) }),
      h('span', {}, 'Allow over Wi-Fi'),
      h('span', { class: 'hint', id: `wifi-msg-${d.id}` }, d.wifi ? 'On: the phone can be unplugged.' : 'Plug the phone in to turn this on.')),
    d.revoked ? null : h('div', { class: 'row' }, h('button', { class: 'danger', onclick: () => revoke(d) }, 'Revoke this phone')),
  )));
}

async function savePermissions(deviceId) {
  const granted = [...document.querySelectorAll(`input[data-device="${deviceId}"]:checked`)].map((i) => i.dataset.perm);
  await api(`/devices/${deviceId}/permissions`, { method: 'PUT', body: JSON.stringify({ permissions: granted }) });
}

// what each outcome of turning Wi-Fi on means for the user (WifiSetupResult on the agent)
const wifiMessages = {
  Ok: 'Done.',
  NoAdb: "adb isn't installed on this computer (needed once, to set Wi-Fi up over USB).",
  NotPluggedIn: 'Plug the phone in over USB to turn Wi-Fi on. Turning it off works any time.',
  AppNotReady: "The app on the phone isn't ready. Open it once, wait a few seconds, and try again.",
  PhoneKeyRefused: "The phone's app didn't give a valid Wi-Fi key. Update the app and try again.",
  PhoneRefused: "The phone's app didn't accept the Wi-Fi details. Update the app and try again.",
  NoAddress: "This computer isn't on a private network right now (home or office Wi-Fi or Ethernet).",
};

async function setWifi(d, box) {
  const on = box.checked;
  box.disabled = true;
  const msg = $(`wifi-msg-${d.id}`);
  msg.textContent = on ? 'Setting up over USB…' : 'Turning off…';
  try {
    const { result } = await api(`/devices/${d.id}/wifi`, { method: 'POST', body: JSON.stringify({ on }) });
    if (result !== 'Ok') { msg.textContent = wifiMessages[result] || result; box.checked = !on; box.disabled = false; return; }
    await loadDevices();
  } catch (e) {
    msg.textContent = String(e.message || e);
    box.checked = !on;
    box.disabled = false;
  }
}

async function revoke(d) {
  if (!confirm(`Revoke ${d.model}? It will stop working until it is enrolled again over USB.`)) return;
  await api(`/devices/${d.id}/revoke`, { method: 'POST' });
  await loadDevices();
}

// ---------------------------------------------------------------- actions
let actions = [];

async function loadActions() {
  actions = await api('/actions');
  renderActions();
}

function renderActions() {
  const list = $('action-list');
  if (actions.length === 0) { list.replaceChildren(h('p', { class: 'hint' }, 'No actions yet.')); return; }
  list.replaceChildren(...actions.map((a, i) => h('div', { class: 'card' },
    h('h3', {}, a.label, ' ', h('code', {}, a.id)),
    h('div', { class: 'meta' }, a.kind === 'launch' ? `opens ${a.target}` : `presses ${a.target}`),
    h('button', { class: 'danger', onclick: () => { actions.splice(i, 1); saveActions(); } }, 'Remove'))));
}

async function saveActions() {
  const { errors } = await api('/actions', { method: 'PUT', body: JSON.stringify(actions) });
  $('action-errors').textContent = errors.join('\n');
  await loadActions();
}

const slug = (s) => s.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 40) || 'app';

$('add-app').addEventListener('click', async () => {
  const picked = await api('/pick-app', { method: 'POST' }); // a dialog opens on this Mac
  if (!picked) return;
  let id = slug(picked.name);
  while (actions.some((a) => a.id === id)) id += '-2';
  actions.push({ id, label: picked.name.slice(0, 40), kind: 'launch', target: picked.path });
  await saveActions();
});

$('add-keys').addEventListener('click', async () => {
  const id = $('keys-id').value.trim(), label = $('keys-label').value.trim(), target = $('keys-combo').value.trim();
  actions.push({ id, label: label || id, kind: 'keys', target });
  await saveActions();
  if (!$('action-errors').textContent) for (const f of ['keys-id', 'keys-label', 'keys-combo']) $(f).value = '';
});

// ---------------------------------------------------------------- layout editor
const COLS = 12, ROWS = 6;
let catalog = [], layout = null, layoutVersion = null, savedJson = '', selected = null;

const defOf = (type) => catalog.find((d) => d.type === type);
const overlaps = (w, x, y, ww, hh) => layout.widgets.some((o) => o !== w && x < o.x + o.w && o.x < x + ww && y < o.y + o.h && o.y < y + hh);

/** First free spot for a widget, shrinking towards its minimum size if the page is full. */
function freeSpot(def) {
  for (let hh = def.h; hh >= def.minH; hh--)
    for (let ww = def.w; ww >= def.minW; ww--)
      for (let y = 0; y + hh <= ROWS; y++)
        for (let x = 0; x + ww <= COLS; x++)
          if (!overlaps(null, x, y, ww, hh)) return { x, y, w: ww, h: hh };
  return null;
}

async function loadLayout() {
  catalog = await api('/catalog');
  const env = await api('/layout');
  layout = env.layout; layoutVersion = env.version; savedJson = JSON.stringify(layout); selected = null;
  renderPalette(); renderEditor(); setStatus('');
}

function dirty() { return JSON.stringify(layout) !== savedJson; }
function setStatus(text) {
  $('layout-status').textContent = text;
  $('layout-save').disabled = !dirty();
  $('layout-revert').disabled = !dirty();
}

function renderPalette() {
  $('palette').replaceChildren(...catalog.map((d) => h('button', { title: d.desc, onclick: () => addWidget(d) }, `${d.icon} ${d.name}`)));
}

function addWidget(def) {
  const spot = freeSpot(def);
  if (!spot) { setStatus('No free space for that widget.'); return; }
  let id = def.type, n = 2;
  while (layout.widgets.some((w) => w.id === id)) id = `${def.type}-${n++}`;
  const options = Object.fromEntries(def.options.map((o) => [o.key, o.default]));
  const w = { id, type: def.type, ...spot, options };
  layout.widgets.push(w); selected = w;
  renderEditor(); setStatus('');
}

function renderEditor() {
  const canvas = $('canvas');
  canvas.replaceChildren(...layout.widgets.map((w) => {
    const def = defOf(w.type) || { name: w.type, icon: '?' };
    const box = h('div', { class: 'wbox' + (w === selected ? ' sel' : '') },
      h('div', { class: 'wname' }, `${def.icon} ${def.name}`), h('div', { class: 'wid' }, w.id), h('div', { class: 'rz' }));
    box.style.gridArea = `${w.y + 1} / ${w.x + 1} / span ${w.h} / span ${w.w}`;
    box.addEventListener('pointerdown', (e) => startDrag(e, w, e.target.classList.contains('rz') ? 'resize' : 'move'));
    return box;
  }));
  renderInspector();
}

/** Pointer drag in whole cells; a change applies only if it stays on the grid and overlaps nothing. */
function startDrag(e, w, mode) {
  e.preventDefault();
  if (selected !== w) { selected = w; renderEditor(); }
  const rect = $('canvas').getBoundingClientRect();
  const cw = rect.width / COLS, ch = rect.height / ROWS, sx = e.clientX, sy = e.clientY, start = { ...w };
  const def = defOf(w.type);
  const move = (ev) => {
    const dx = Math.round((ev.clientX - sx) / cw), dy = Math.round((ev.clientY - sy) / ch);
    let { x, y, w: ww, h: hh } = start;
    if (mode === 'move') { x = Math.max(0, Math.min(COLS - ww, x + dx)); y = Math.max(0, Math.min(ROWS - hh, y + dy)); }
    else { ww = Math.max(def.minW, Math.min(COLS - x, ww + dx)); hh = Math.max(def.minH, Math.min(ROWS - y, hh + dy)); }
    if ((x !== w.x || y !== w.y || ww !== w.w || hh !== w.h) && !overlaps(w, x, y, ww, hh)) {
      Object.assign(w, { x, y, w: ww, h: hh });
      renderEditor(); setStatus('');
    }
  };
  const up = () => { window.removeEventListener('pointermove', move); window.removeEventListener('pointerup', up); };
  window.addEventListener('pointermove', move);
  window.addEventListener('pointerup', up);
}

function renderInspector() {
  const box = $('inspector');
  if (!selected) { box.replaceChildren(h('p', { class: 'hint' }, 'Select a widget.')); return; }
  const w = selected, def = defOf(w.type);
  w.options = w.options || {};
  const field = (o) => {
    const value = w.options[o.key] ?? o.default;
    const set = (v) => { w.options[o.key] = v; setStatus(''); };
    let input;
    if (o.type === 'bool') input = h('input', { type: 'checkbox', checked: !!value, onchange: (e) => set(e.target.checked) });
    else if (o.type === 'number') input = h('input', { type: 'number', min: o.min, max: o.max, value, onchange: (e) => set(Number(e.target.value)) });
    else if (o.type === 'select') {
      input = h('select', { onchange: (e) => set(e.target.value) }, ...o.choices.map((c) => h('option', { value: c, selected: c === value }, c)));
    } else input = h('input', { value, maxlength: o.maxLength || 200, placeholder: o.type === 'idlist' ? 'e.g. google-chrome, spotlight' : '', onchange: (e) => set(e.target.value) });
    return [h('label', {}, o.label), input];
  };
  box.replaceChildren(
    h('h3', {}, `${def.icon} ${def.name}`), h('p', { class: 'hint' }, def.desc),
    h('label', {}, 'Id'), h('input', { value: w.id, maxlength: 40, onchange: (e) => { w.id = e.target.value.trim(); renderEditor(); setStatus(''); } }),
    ...def.options.flatMap(field),
    h('div', { class: 'row' }, h('button', { class: 'danger', onclick: () => {
      layout.widgets = layout.widgets.filter((x) => x !== w); selected = null; renderEditor(); setStatus('');
    } }, 'Remove')));
}

$('layout-save').addEventListener('click', async () => {
  $('layout-errors').replaceChildren();
  const res = await fetch('/admin/api/layout', {
    method: 'PUT', credentials: 'same-origin',
    headers: { 'X-DeskPanel-Admin': '1', 'Content-Type': 'application/json' },
    body: JSON.stringify({ version: layoutVersion, layout }),
  });
  const body = await res.json().catch(() => ({}));
  if (res.ok) { layoutVersion = body.version; savedJson = JSON.stringify(layout); setStatus('Saved. The phone updates within seconds.'); return; }
  if (res.status === 409) {
    setStatus('The layout was changed elsewhere since you opened it. Revert to load the latest, then redo your change.');
    $('layout-revert').disabled = false;
    return;
  }
  // 400: show every problem with where it is (e.g. widgets[2].options.focusMin)
  $('layout-errors').replaceChildren(...(body.errors || [{ path: '', message: `HTTP ${res.status}` }])
    .map((e) => h('li', {}, `${describe(e.path)}: ${e.message}`)));
  setStatus('Not saved.');
});

/** "widgets[2].options.focusMin" → "Pomodoro 'timer' › focusMin" */
function describe(path) {
  const m = /^widgets\[(\d+)\]\.?(.*)$/.exec(path);
  if (!m) return path;
  const w = layout.widgets[Number(m[1])];
  const name = w ? `${defOf(w.type)?.name ?? w.type} '${w.id}'` : `widget ${m[1]}`;
  return m[2] ? `${name} › ${m[2].replace(/^options\./, '')}` : name;
}

$('layout-revert').addEventListener('click', () => { $('layout-errors').replaceChildren(); loadLayout().catch(console.error); });

// ---------------------------------------------------------------- activity
async function loadAudit() {
  const rows = await api('/audit?limit=200');
  $('audit-rows').replaceChildren(...rows.map((r) => h('tr', {},
    h('td', {}, new Date(r.ts).toLocaleString()), h('td', {}, r.deviceId), h('td', {}, r.action),
    h('td', { class: r.result }, r.result, r.detail ? ` (${r.detail})` : ''), h('td', {}, r.ms))));
}

// ---------------------------------------------------------------- tabs
const loaders = { devices: loadDevices, layout: loadLayout, actions: loadActions, audit: loadAudit };
for (const b of document.querySelectorAll('nav button')) {
  b.addEventListener('click', () => {
    for (const o of document.querySelectorAll('nav button')) o.classList.toggle('active', o === b);
    for (const s of document.querySelectorAll('main section')) s.hidden = s.id !== b.dataset.tab;
    loaders[b.dataset.tab]().catch((e) => console.error(e));
  });
}
$('logout').addEventListener('click', async () => { await api('/logout', { method: 'POST' }).catch(() => {}); location.reload(); });

loadDevices().catch((e) => console.error(e));
