// 0 is unknown, 1 offline, 2 connected and idle, 3 connected and working/waiting.
const skip = Object.freeze({ due: Infinity });
const clear = Object.freeze({ clear: true, due: Infinity });
export function wasmStarterPlan(getExports) {
  let plan;
  return (suppressed, registered, live, startedAt, now, cooldown, readState) => {
    if (!Number.isFinite(startedAt) || !Number.isFinite(now)) return undefined;
    plan ??= getExports().starter_plan;
    const earliest = startedAt + cooldown;
    if (plan(suppressed, registered, live, 0, now, earliest) === 0) return skip;
    const state = readState();
    const status = state.connection !== 'connected' ? 1 : state.workingCount + state.waitingCount === 0 ? 2 : 3;
    switch (plan(suppressed, registered, live, status, now, earliest)) {
      case 2: return clear;
      case 3: return { due: earliest };
      case 4: return { state };
      default: return skip;
    }
  };
}
