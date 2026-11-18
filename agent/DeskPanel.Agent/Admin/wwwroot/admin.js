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
  if (res.status === 401) { document.body.replaceChildren(h('main', {}, h('p', {}, 'Signed out. Open a fresh admin link: tools/admin.sh'))); throw new Error('signed out'); }
  if (!res.ok && res.status !== 204) throw new Error(`${path}: ${res.status}`);
  return res.status === 204 ? null : res.json();
}

// ---------------------------------------------------------------- phones and permissions
let permissionInfo = [];

async function loadDevices() {
  permissionInfo = await api('/permissions');
  const devices = await api('/devices');
  const list = $('device-list');
  if (devices.length === 0) { list.replaceChildren(h('p', { class: 'hint' }, 'No phone enrolled yet. Plug one in with the app installed.')); return; }
  list.replaceChildren(...devices.map((d) => h('div', { class: 'card' },
    h('h3', {}, d.model, d.revoked ? ' (revoked)' : ''),
    h('div', { class: 'meta' }, `device ${d.id} · serial ${d.serial} · enrolled ${new Date(d.createdAt).toLocaleString()}`),
    d.revoked ? null : permissionInfo.map((p) => h('label', { class: 'perm' },
      h('input', { type: 'checkbox', checked: d.permissions.includes(p.id), onchange: () => savePermissions(d.id) , 'data-device': d.id, 'data-perm': p.id }),
      h('span', {}, p.description), h('code', {}, p.id))),
    d.revoked ? null : h('div', { class: 'row' }, h('button', { class: 'danger', onclick: () => revoke(d) }, 'Revoke this phone')),
  )));
}

async function savePermissions(deviceId) {
  const granted = [...document.querySelectorAll(`input[data-device="${deviceId}"]:checked`)].map((i) => i.dataset.perm);
  await api(`/devices/${deviceId}/permissions`, { method: 'PUT', body: JSON.stringify({ permissions: granted }) });
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

// ---------------------------------------------------------------- activity
async function loadAudit() {
  const rows = await api('/audit?limit=200');
  $('audit-rows').replaceChildren(...rows.map((r) => h('tr', {},
    h('td', {}, new Date(r.ts).toLocaleString()), h('td', {}, r.deviceId), h('td', {}, r.action),
    h('td', { class: r.result }, r.result, r.detail ? ` (${r.detail})` : ''), h('td', {}, r.ms))));
}

// ---------------------------------------------------------------- tabs
const loaders = { devices: loadDevices, actions: loadActions, audit: loadAudit };
for (const b of document.querySelectorAll('nav button')) {
  b.addEventListener('click', () => {
    for (const o of document.querySelectorAll('nav button')) o.classList.toggle('active', o === b);
    for (const s of document.querySelectorAll('main section')) s.hidden = s.id !== b.dataset.tab;
    loaders[b.dataset.tab]().catch((e) => console.error(e));
  });
}
$('logout').addEventListener('click', async () => { await api('/logout', { method: 'POST' }).catch(() => {}); location.reload(); });

loadDevices().catch((e) => console.error(e));
