import module from './quick_picker.wasm';
import { RelayObject as ShippingRelayObject } from '../src/worker.mjs';
import { wasmQuickPicker } from './picker.mjs';

export { default } from '../src/worker.mjs';
const quickPicker = wasmQuickPicker(module);
export class RelayObject extends ShippingRelayObject {
  constructor(ctx, env) {
    super(ctx, env);
    this.relay.quickPicker = quickPicker;
  }
}
