export class HTTPError extends Error {
  constructor(status, code) { super(code); this.status = status; }
}
export function requireValue(condition, code = 'invalid_request') {
  if (!condition) throw new HTTPError(400, code);
}
const states = new Set(['ready', 'partial', 'stale', 'loading', 'disabled', 'unavailable', 'needsAuth', 'accessDenied', 'unsupported']);
const phases = new Set(['working', 'waiting', 'idle', 'unavailable']);
const text = (v, max) => typeof v === 'string' ? v.replace(/[\p{Cc}\p{Cf}]/gu, '').trim().slice(0, max).toWellFormed() : '';
const timestamp = v => Number.isFinite(v) && v >= 0 && v <= 4102444800 ? v : null;
const percent = v => Number.isFinite(v) && v >= 0 && v <= 100 ? v : null;
const tokens = v => Number.isSafeInteger(v) && v >= 0 ? v : null;
export const defaultPreferences = () => ({ providerIDs: [] });
export function preferences(input) {
  requireValue(Array.isArray(input?.providerIDs) && input.providerIDs.length <= 100);
  requireValue(input.providerIDs.every(id => typeof id === 'string' && /^[a-z0-9-]{1,48}$/.test(id)));
  requireValue(new Set(input.providerIDs).size === input.providerIDs.length);
  return { providerIDs: input.providerIDs };
}
// Reconstruct the allowlisted wire format; never forward prompts, paths or credentials.
export function sanitizeSnapshot(input, now) {
  requireValue(input?.schemaVersion === 1 && Array.isArray(input.providers) && input.providers.length <= 100);
  requireValue(timestamp(input.generatedAt) !== null && Math.abs(now - input.generatedAt) <= 120, 'invalid_timestamp');
  const ids = new Set();
  const providers = input.providers.map(p => {
    requireValue(typeof p.id === 'string' && /^[a-z0-9-]{1,48}$/.test(p.id) && !ids.has(p.id));
    ids.add(p.id);
    requireValue(states.has(p.state) && Array.isArray(p.windows) && p.windows.length <= 2);
    return {
      id: p.id, name: text(p.name, 32), state: p.state,
      windows: p.windows.map(w => ({ name: text(w.name, 32), remainingPercent: percent(w.remainingPercent), resetsAt: timestamp(w.resetsAt) })),
      todayTokens: tokens(p.todayTokens), localState: states.has(p.localState) ? p.localState : 'unavailable',
      updatedAt: timestamp(p.updatedAt),
    };
  });
  requireValue(Array.isArray(input.sessions) && input.sessions.length <= 64);
  const sessions = input.sessions.map(s => {
    requireValue(ids.has(s.providerID) && phases.has(s.phase));
    return { providerID: s.providerID, phase: s.phase, title: text(s.title, 60), since: timestamp(s.since) };
  });
  return { schemaVersion: 1, generatedAt: input.generatedAt, providers, sessions };
}
export function contentState(snapshot, prefs, receivedAt, now) {
  const staleAt = receivedAt ? receivedAt + 90 : now;
  const connection = !snapshot || now >= staleAt ? 'offline' : 'connected';
  const providers = prefs.providerIDs.map(id => snapshot?.providers.find(p => p.id === id)).filter(Boolean).map(p => ({
    ...p,
    state: (p.state === 'ready' || p.state === 'partial') && (!p.updatedAt || now - p.updatedAt > 300 || p.windows.some(w => w.resetsAt && w.resetsAt <= now)) ? 'stale' : p.state,
  }));
  const rank = { waiting: 0, working: 1, idle: 2, unavailable: 3 };
  const all = (snapshot?.sessions ?? []).filter(s => prefs.providerIDs.includes(s.providerID)).sort((a, b) => rank[a.phase] - rank[b.phase] || (b.since ?? 0) - (a.since ?? 0));
  return {
    connection, updatedAt: snapshot?.generatedAt ?? now, staleAt,
    providers, sessions: all.slice(0, 2), additionalSessionCount: Math.max(0, all.length - 2),
    workingCount: all.filter(s => s.phase === 'working').length,
    waitingCount: all.filter(s => s.phase === 'waiting').length,
    unavailableCount: all.filter(s => s.phase === 'unavailable').length,
  };
}
export function activityPayload(state, now, event = 'update') {
  const payload = { aps: { timestamp: Math.floor(now), event, 'content-state': state, 'stale-date': Math.floor(state.staleAt) } };
  if (event === 'end') payload.aps['dismissal-date'] = Math.floor(now);
  // Reserve space for the ActivityAttributes and APNs envelope under the 4 KB ceiling.
  requireValue(Buffer.byteLength(JSON.stringify(payload)) <= 3800, 'activity_too_large');
  return payload;
}

// The full catalog stays in the relay. Each drilldown level has at most three choices.
const nameOrder = new Intl.Collator('en', { numeric: true, sensitivity: 'base' });
function eligible(device, prefs) {
  return (device?.snapshot?.providers ?? []).filter(p => !prefs.providerIDs.length || prefs.providerIDs.includes(p.id));
}
function splitCatalog(list) {
  const size = Math.ceil(list.length / 3);
  return Array.from({ length: Math.ceil(list.length / size) }, (_, i) => list.slice(i * size, (i + 1) * size));
}
function historyFor(view, deviceID) {
  return view?.providerHistory?.[deviceID] ?? { pinned: [], recent: [] };
}
function advancedView(devices, view, deviceID, providerID, remember = false) {
  const history = Object.fromEntries(devices.filter(d => view?.providerHistory?.[d.id]).map(d => [d.id, view.providerHistory[d.id]]));
  if (remember && providerID) {
    const previous = historyFor(view, deviceID);
    history[deviceID] = { pinned: previous.pinned, recent: [providerID, ...previous.recent.filter(id => id !== providerID)].slice(0, 3) };
  }
  return { deviceID, providerID, providerHistory: history, pickerVersion: view?.pickerVersion,
    revision: (view?.revision ?? 0) + 1 };
}
function catalogPicker(device, list, view, current, now) {
  const history = historyFor(view, device.id);
  const pinned = history.pinned.filter(id => list.some(p => p.id === id));
  const phase = id => {
    if (now >= device.receivedAt + 90) return undefined;
    const sessions = (device.snapshot?.sessions ?? []).filter(s => s.providerID === id);
    return sessions.some(s => s.phase === 'waiting') ? 'waiting' : sessions.some(s => s.phase === 'working') ? 'working' : undefined;
  };
  const option = p => ({ id: p.id, name: p.name, isPinned: pinned.includes(p.id), phase: phase(p.id) });
  const result = { mode: view?.pickerMode === 'all' ? 'all' : 'quick', groups: [], canGoBack: false,
    isCurrentPinned: pinned.includes(current?.id), canPinCurrent: Boolean(current && (pinned.includes(current.id) || pinned.length < 3)) };
  if (result.mode === 'quick') {
    const active = [...list].sort((a, b) => (phase(a.id) === 'waiting' ? 0 : phase(a.id) === 'working' ? 1 : 2) - (phase(b.id) === 'waiting' ? 0 : phase(b.id) === 'working' ? 1 : 2));
    const ids = [...new Set([...pinned, ...history.recent, current?.id, ...active.map(p => p.id)])];
    result.options = ids.map(id => list.find(p => p.id === id)).filter(Boolean).slice(0, 3).map(option);
    return result;
  }
  result.canGoBack = true;
  let catalog = [...list].sort((a, b) => nameOrder.compare(a.name, b.name) || a.id.localeCompare(b.id));
  const path = view?.pickerPath ?? [];
  for (const index of path) {
    if (catalog.length <= 3) break;
    catalog = splitCatalog(catalog)[index] ?? [];
  }
  if (catalog.length > 3) {
    result.options = [];
    result.groups = splitCatalog(catalog).map((items, i) => ({ id: [...path, i].join('.'),
      firstName: items[0].name, lastName: items.at(-1).name, count: items.length }));
  } else result.options = catalog.map(option);
  return result;
}
// Each display has one source. Matching provider names never imply matching accounts.
export function desktopState(devices, prefs, view, now) {
  const device = devices.find(d => d.id === view?.deviceID) ?? devices.find(d => d.receivedAt + 90 > now) ?? devices[0];
  const list = eligible(device, prefs);
  const provider = list.find(p => p.id === view?.providerID) ?? list[0];
  const state = contentState(device?.snapshot, { providerIDs: provider ? [provider.id] : [] }, device?.receivedAt ?? 0, now);
  state.focus = { deviceID: device?.id ?? '', deviceName: device?.name ?? 'No device', platform: device?.platform ?? 'macOS',
    deviceIndex: device ? devices.indexOf(device) + 1 : 0, deviceCount: devices.length,
    providerIndex: provider ? list.indexOf(provider) + 1 : 0, providerCount: list.length };
  const pageCount = Math.ceil(list.length / 3);
  const focusedPage = Math.floor(Math.max(0, state.focus.providerIndex - 1) / 3);
  const page = Math.min(Math.max(0, pageCount - 1), Math.max(0, view?.deviceID === device?.id && Number.isSafeInteger(view?.pickerPage) ? view.pickerPage : focusedPage));
  state.providerPicker = { isOpen: Boolean(view?.pickerOpen && view?.deviceID === device?.id && list.length > 1), page, pageCount,
    options: list.slice(page * 3, page * 3 + 3).map(p => ({ id: p.id, name: p.name })) };
  // An explicit capability prevents an older app from labeling shortcuts as catalog pages.
  if (view?.pickerVersion === 2 && view?.pickerMode !== 'legacy' && device) {
    Object.assign(state.providerPicker, catalogPicker(device, list, view?.deviceID === device.id ? view : {}, provider, now));
  }
  state.viewRevision = view?.revision ?? 0;
  return state;
}
export function navigate(devices, prefs, view, axis, direction, now, { groupID, pickerVersion } = {}) {
  requireValue(['provider', 'device', 'provider-picker', 'provider-page', 'provider-all', 'provider-back', 'provider-group', 'provider-pin'].includes(axis) && [-1, 1].includes(direction));
  const current = desktopState(devices, prefs, view, now);
  let deviceID = current.focus.deviceID, providerID = current.providers[0]?.id;
  const next = advancedView(devices, view, deviceID, providerID);
  if (axis === 'provider-picker') {
    return { ...next, pickerVersion, pickerOpen: !current.providerPicker.isOpen,
      pickerMode: 'quick', pickerPath: [], pickerPage: Math.floor(Math.max(0, current.focus.providerIndex - 1) / 3) };
  }
  if (['provider-all', 'provider-back', 'provider-group', 'provider-pin'].includes(axis)) {
    requireValue(current.providerPicker.isOpen && view?.pickerVersion === 2, 'picker_closed');
    const opened = { ...next, pickerOpen: true, pickerMode: view.pickerMode, pickerPath: view.pickerPath ?? [] };
    if (axis === 'provider-all') return { ...opened, pickerMode: 'all', pickerPath: [] };
    if (axis === 'provider-back') return { ...opened, pickerMode: opened.pickerPath.length ? 'all' : 'quick', pickerPath: opened.pickerPath.slice(0, -1) };
    if (axis === 'provider-group') {
      requireValue(current.providerPicker.groups?.some(group => group.id === groupID), 'group_unavailable');
      return { ...opened, pickerMode: 'all', pickerPath: groupID.split('.').map(Number) };
    }
    requireValue(providerID, 'provider_unavailable');
    const history = historyFor(view, deviceID), allowed = eligible(devices.find(d => d.id === deviceID), prefs);
    const pinned = history.pinned.filter(id => allowed.some(p => p.id === id));
    requireValue(pinned.includes(providerID) || pinned.length < 3, 'pin_limit');
    opened.providerHistory[deviceID] = { recent: history.recent,
      pinned: pinned.includes(providerID) ? pinned.filter(id => id !== providerID) : [...pinned, providerID] };
    return opened;
  }
  if (axis === 'provider-page') {
    requireValue(current.providerPicker.isOpen, 'picker_closed');
    return { ...next, pickerOpen: true, pickerMode: 'legacy',
      pickerPage: Math.min(current.providerPicker.pageCount - 1, Math.max(0, current.providerPicker.page + direction)) };
  }
  if (axis === 'device' && devices.length) {
    const index = (current.focus.deviceIndex - 1 + direction + devices.length) % devices.length;
    deviceID = devices[index].id;
    // Remember the last chosen service on that computer independently.
    const list = eligible(devices[index], prefs), recent = historyFor(view, deviceID).recent;
    providerID = recent.find(id => list.some(p => p.id === id)) ?? (list.some(p => p.id === providerID) ? providerID : list[0]?.id);
  } else if (axis === 'provider') {
    const providers = eligible(devices.find(d => d.id === deviceID), prefs);
    if (providers.length) providerID = providers[(current.focus.providerIndex - 1 + direction + providers.length) % providers.length].id;
  }
  return advancedView(devices, view, deviceID, providerID, axis === 'provider');
}
// Direct selection is tied to the computer and revision the phone actually showed.
export function selectProvider(devices, prefs, view, providerID, deviceID, now) {
  const current = desktopState(devices, prefs, view, now);
  requireValue(typeof providerID === 'string' && deviceID === current.focus.deviceID, 'display_changed');
  const device = devices.find(d => d.id === deviceID);
  requireValue(eligible(device, prefs).some(p => p.id === providerID), 'provider_unavailable');
  return advancedView(devices, view, deviceID, providerID, true);
}
export function deviceLabel(body) {
  requireValue(body.platform === undefined || ['macOS', 'windows'].includes(body.platform));
  const platform = body.platform ?? 'macOS';
  return { platform, name: text(body.name, 24) || (platform === 'windows' ? 'Windows PC' : 'Mac') };
}
