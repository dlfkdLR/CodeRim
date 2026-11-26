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
