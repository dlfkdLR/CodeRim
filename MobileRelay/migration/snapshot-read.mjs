import { ONLINE_WINDOW } from '../src/model.mjs';

export function wasmSnapshotRead(getExports) {
  let rows, project;
  return (devices, view, floor, now) => {
    if (devices.length > 32 || !Number.isFinite(floor) || !Number.isFinite(now)) return undefined;
    if (!rows) {
      const { memory, snapshot_buffer, snapshot_read } = getExports();
      rows = new Float64Array(memory.buffer, snapshot_buffer(), 2 + 32 * 3);
      project = snapshot_read;
    }
    let selected = -1;
    for (let i = 0; i < devices.length; i++) {
      const d = devices[i], seq = d.seq ?? 0, window = d.onlineWindow ?? ONLINE_WINDOW;
      if (!Number.isFinite(seq) || !Number.isFinite(d.receivedAt) || !Number.isFinite(window)) return undefined;
      const offset = 2 + 3 * i;
      rows[offset] = seq;
      rows[offset + 1] = d.receivedAt;
      rows[offset + 2] = window;
      if (selected < 0 && d.id === view?.deviceID) selected = i;
    }
    const online = project(devices.length, selected, floor, now) >>> 0;
    return { dataVersion: rows[0], focused: rows[1], online };
  };
}

// Only phase ranks and numeric times for this display enter Wasm. The original
// session objects stay in the host and preserve every field in the public response.
export function wasmActivitySessions(getExports) {
  let times, phases, output, summarize;
  return (sessions, provider, state) => {
    if (!Array.isArray(sessions) || sessions.length > 64) return false;
    if (!times) {
      const { memory, activity_session_buffer, activity_session_summary } = getExports();
      const start = activity_session_buffer();
      times = new Float64Array(memory.buffer, start, 64);
      phases = new Uint8Array(memory.buffer, start + 512, 64);
      output = new Uint8Array(memory.buffer, start + 576, 6);
      summarize = activity_session_summary;
    }
    for (let i = 0; i < sessions.length; i++) {
      const row = sessions[i];
      if (row === undefined) return false;
      if (row.providerID !== provider) { phases[i] = 255; continue; }
      switch (row.phase) {
        case 'waiting': phases[i] = 0; break;
        case 'working': phases[i] = 1; break;
        case 'idle': phases[i] = 2; break;
        case 'unavailable': phases[i] = 3; break;
        default: return false;
      }
      const since = row.since ?? 0;
      if (!Number.isFinite(since)) return false;
      times[i] = since;
    }
    const count = summarize(sessions.length);
    if (count < 0) return false;
    if (count > 0) state.sessions.push(sessions[output[0]]);
    if (count > 1) state.sessions.push(sessions[output[1]]);
    state.additionalSessionCount = Math.max(0, count - 2);
    state.waitingCount = output[2];
    state.workingCount = output[3];
    state.unavailableCount = output[5];
    return true;
  };
}
