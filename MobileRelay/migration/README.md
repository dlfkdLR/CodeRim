# Optional Rust quick picker

The alternate Worker entry point `migration/worker.mjs` enables the Rust quick-provider policy for the version 2 picker. It honors pinned, recent and current providers followed by waiting, working and inactive providers in catalogue order. Swift and the relay continue to exchange the same JSON. The existing relay configuration loads `src/worker.mjs`. The Wasm instance is created on the first eligible request. Each synchronous call receives only provider indices and activity ranks.

From the repository root, build the generated module before loading the alternate entry point.

```sh
rustup target add wasm32-unknown-unknown
bash Scripts/rust_migration/relay-picker/build.sh
node Scripts/rust_migration/relay-picker/check.mjs
```

The Node 24 replay compares complete desktop states, exercises the SQLite relay over its Fetch interface, and measures prepared desktop states against the shipping and linear JavaScript implementations. The migration workflow additionally runs both Worker entry points and the SQLite Durable Object locally. Its pinned Miniflare test dependency installs into a temporary directory independently of the deployable relay.
