// A compiled WebAssembly.Module is provided by the Worker module loader, or by
// the local replay. Instantiate lazily on the first quick-picker request.
export function wasmQuickPicker(module) {
  let engine;
  return (list, pinned, recent, current, sessions) => {
    if (list.length > 100 || sessions.length > 64 || pinned.length > 3 || recent.length > 3) return null;
    if (!engine) {
      const { memory, picker_buffer, quick_pick } = new WebAssembly.Instance(module).exports;
      const start = picker_buffer();
      // repr(C) byte arrays from quick_picker.rs; the standalone module never grows memory.
      const view = (n, len) => new Uint8Array(memory.buffer, start + n, len);
      engine = { preferred: view(0, 7), provider: view(7, 64), phase: view(71, 64),
        selected: view(135, 3), activity: view(138, 3), quick_pick };
    }
    const index = new Map(list.map((p, i) => [p.id, i]));
    if (index.size !== list.length) return null; // Older/corrupt snapshots retain the JS first-match rule.
    let preferred = 0;
    for (const id of [...pinned, ...recent, current?.id]) {
      if (index.has(id)) engine.preferred[preferred++] = index.get(id);
    }
    for (let i = 0; i < sessions.length; i++) {
      const s = sessions[i];
      engine.provider[i] = index.get(s.providerID) ?? 255;
      engine.phase[i] = s.phase === 'waiting' ? 2 : s.phase === 'working' ? 1 : 0;
    }
    const count = engine.quick_pick(list.length, preferred, sessions.length);
    if (count < 0 || count > 3) throw new RangeError('Invalid Rust quick-picker result');
    const options = [];
    for (let i = 0; i < count; i++) {
      const p = list[engine.selected[i]], phase = engine.activity[i];
      options.push({ id: p.id, name: p.name, isPinned: pinned.includes(p.id),
        phase: phase === 2 ? 'waiting' : phase === 1 ? 'working' : undefined });
    }
    return options;
  };
}
