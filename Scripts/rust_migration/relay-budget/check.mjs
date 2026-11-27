import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { RelayBudget, relayClock, wasmBudgetPolicy } from '../../../MobileRelay/src/budget.mjs';
import { nodeSQL } from '../../../MobileRelay/src/node-sql.mjs';
import { Store } from '../../../MobileRelay/src/store.mjs';
import { runCases } from './scenario.mjs';

const root = fileURLToPath(new URL('../../../', import.meta.url));
const pinned = execFileSync('git', ['show', 'v2.1.41:MobileRelay/src/budget.mjs'], {cwd: root, encoding:'utf8'})
  .replace("'./model.mjs'", JSON.stringify(new URL('../../../MobileRelay/src/model.mjs', import.meta.url).href));
const release = await import('data:text/javascript;base64,' + Buffer.from(pinned).toString('base64'));
const binary = await readFile(new URL('../../../MobileRelay/migration/quick_picker.wasm', import.meta.url));
const native = new WebAssembly.Instance(new WebAssembly.Module(binary)).exports;
const calls = { budget_count: 0, budget_record: 0, budget_level: 0, budget_reserve: 0 };
const instrumented = { ...native };
for (const name of Object.keys(calls)) instrumented[name] = (...args) => { calls[name]++; return native[name](...args); };
const policy = wasmBudgetPolicy(() => instrumented);

function replay(Budget, policy, clock) {
  const db = nodeSQL();
  try { return runCases(Budget, new Store(db), policy, clock); } finally { db.close(); }
}
const oracle = replay(release.RelayBudget);
assert.deepEqual(replay(RelayBudget), oracle);
assert.deepEqual(replay(RelayBudget, policy), oracle);
assert.deepEqual(replay(RelayBudget, policy, relayClock), oracle);
assert.ok(Object.values(calls).every(n => n > 0));
console.log(JSON.stringify({ environments: ['v2.1.41', 'JavaScript', 'Rust'], restores: oracle.length,
  frames: oracle.flat().length, calls, digest: createHash('sha256').update(JSON.stringify(oracle)).digest('hex') }));
