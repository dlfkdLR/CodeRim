import module from './quick_picker.wasm';
import { RelayObject as ShippingRelayObject } from '../src/worker.mjs';
import { lazyExports, wasmQuickPicker } from './picker.mjs';
import { wasmActivitySessions, wasmSnapshotRead } from './snapshot-read.mjs';
import { wasmStarterPlan } from './starter.mjs';

export { default } from '../src/worker.mjs';
const getExports = lazyExports(module);
const quickPicker = wasmQuickPicker(module, getExports);
const snapshotRead = wasmSnapshotRead(getExports);
export class RelayObject extends ShippingRelayObject {
  constructor(ctx, env) {
    super(ctx, env);
    this.relay.quickPicker = quickPicker;
    this.relay.activitySessions = wasmActivitySessions(getExports);
    if (env.CODERIM_RUST_PUSH_STARTER === '1') this.relay.starterPlan = wasmStarterPlan(getExports);
    if (env.CODERIM_RUST_SNAPSHOT_READ === '1') this.relay.snapshotRead = snapshotRead;
  }
}
