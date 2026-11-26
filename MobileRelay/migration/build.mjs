// The Worker imports the verified module directly, including in clean Wrangler checkouts.
import { execFileSync } from 'node:child_process';
import { chmod, mkdtemp, readFile, rename, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const directory = fileURLToPath(new URL('.', import.meta.url));
const output = join(directory, 'quick_picker.wasm');
const check = process.argv[2] === "--check";
if (process.argv.length !== (check ? 3 : 2)) throw new Error("Usage: node build.mjs [--check]");
const staging = await mkdtemp(join(directory, '.wasm-build-'));
try {
  const compiled = join(staging, 'quick_picker.wasm');
  execFileSync('rustc', [
    '--edition=2024', '--crate-type=cdylib', '--target', 'wasm32-unknown-unknown',
    '-C', 'opt-level=z', '-C', 'panic=abort', '-C', 'strip=symbols', '-C', 'link-arg=-zstack-size=65536',
    'quick_picker.rs', '-o', compiled,
  ], { cwd: directory, stdio: 'inherit' });
  const previous = await readFile(output).catch(error => {
    if (error.code === 'ENOENT') return undefined;
    throw error;
  });
  if (!previous || !previous.equals(await readFile(compiled))) {
    if (check) throw new Error('The packaged relay module is stale. Run npm run build in MobileRelay.');
    await chmod(compiled, 0o644);
    await rename(compiled, output);
  }
} finally {
  await rm(staging, { recursive: true, force: true });
}
