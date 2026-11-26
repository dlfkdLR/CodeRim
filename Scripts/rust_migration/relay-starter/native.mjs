// Same early exit and single activity list, implemented without the Wasm call.
const skip = Object.freeze({due: Infinity});
const clear = Object.freeze({clear: true, due: Infinity});

export function nativePlan(suppressed, registered, live, startedAt, now, cooldown, readState) {
  if (!Number.isFinite(startedAt) || !Number.isFinite(now)) return undefined;
  if (!suppressed && (!registered || live)) return skip;
  const state=readState();
  if (state.connection!=='connected') return skip;
  const idle=state.workingCount+state.waitingCount===0;
  if (suppressed) return idle ? clear : skip;
  if (idle) return skip;
  return now < startedAt+cooldown ? {due:startedAt+cooldown} : {state};
}
