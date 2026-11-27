import module from './quick_picker.wasm';
import { RelayObject as ShippingRelayObject } from '../src/worker.mjs';
import { lazyExports, wasmQuickPicker } from './picker.mjs';
import { wasmActivitySessions, wasmSnapshotRead } from './snapshot-read.mjs';
import { wasmStarterPlan } from './starter.mjs';
import { wasmBudgetPolicy } from '../src/budget.mjs';

export { default } from '../src/worker.mjs';
const getExports = lazyExports(module);
const quickPicker = wasmQuickPicker(module, getExports);
const snapshotRead = wasmSnapshotRead(getExports);
const budgetPolicy = wasmBudgetPolicy(getExports);
export class RelayObject extends ShippingRelayObject {
  constructor(ctx, env) {
    super(ctx, env, budgetPolicy);
    this.relay.quickPicker = quickPicker;
    this.relay.deviceReport = (changed, received, seen, previousWindow, nextWindow, now) => {
      // Unusual legacy values keep the original JavaScript coercion.
      if (typeof received !== 'number' || typeof seen !== 'number' || typeof previousWindow !== 'number') return undefined;
      return getExports().device_report(changed, received, seen, previousWindow, nextWindow, now);
    };
    this.relay.activitySessions = wasmActivitySessions(getExports);
    if (env.CODERIM_RUST_PUSH_STARTER === '1') this.relay.starterPlan = wasmStarterPlan(getExports);
    if (env.CODERIM_RUST_SNAPSHOT_READ === '1') this.relay.snapshotRead = snapshotRead;
  }
}
