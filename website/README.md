# CodeRim website

**English** · [한국어](README.ko.md)

Static HTML/CSS/JavaScript product, download and provider guides. No package installation, build or API key is needed. Korean is the default; `/?lang=en` selects English. Only site language/theme persist locally. The page uses Vercel Web Analytics and does not read app accounts, settings or remote fonts.

## Preview and checks

From the repository root:

```sh
python3 -m http.server 4173 --bind 127.0.0.1 --directory website
```

Open [http://127.0.0.1:4173](http://127.0.0.1:4173). Use HTTP for clipboard checks.

```sh
python3 website/scripts/sync-providers.py --check
node --check website/app.js
node --check website/providers.js
python3 website/scripts/test-language.py
```

`index.html` owns Korean/`data-en` copy, downloads and localized guide links; `styles.css` owns layout; `app.js` owns interactions and motion. `providers.js` is generated from the English/Korean provider tables and shared name switch; omit `--check` to regenerate. A changed catalogue count stops generation and requires reviewing site copy. `terminal-examples.js` contains actual CLI output from synthetic snapshots.

The installed Chrome/Chromium regression uses a temporary profile to check Korean → English → Korean guide destinations and retention of search, filter, expansion, theme, platform, pause, gallery position and styles. It does not deploy or query accounts.

## Deployment

Upload only `website`: Root/Output Directory `.`, Framework Other, empty Build/Install commands, no environment variables. `vercel.json` and `.vercelignore` define static output and exclude maintenance docs/scripts/environment files. Keep `.vercel/` out of Git. [Vercel static configuration](https://vercel.com/docs/builds/configure-a-build#skip-build-step).

The recorded deployment uses project `willbrains-projects/coderim`, [coderim.vercel.app](https://coderim.vercel.app) and [coderim.dlfkd.dev](https://coderim.dlfkd.dev), with a DNS-only Cloudflare CNAME. Git deployment was not connected in that record; a future Git connection needs Root Directory `website`. These are recorded settings, not a fresh production check. When deployment is authorized, run from `website`:

```sh
vercel deploy --prod --scope willbrains-projects
```

The page pins macOS `v2.1.37` and Windows `v2.1.34`. Before deployment, verify the platform-specific published assets and update versions, download/checksum URLs and release-note links together; do not assume `releases/latest` identifies both platforms.

## Images and motion

[ASSETS](ASSETS.md) owns source, licence and historical capture details; retain `assets/NOTICE.txt`. Native images and CLI output use example data, not live-account or installed-app verification. [VALIDATION](VALIDATION.md) records the dated local check; deployment evidence is in `outputs/website-2026-09-28/deployment-verification.json`.

The hero composes native panels in a repeating sequence; the seven feature cards use a manual keyboard/touch gallery. Working rings and terminal animation pause offscreen/in the background, and Reduce Motion uses static/instant transitions. The hero has no playback control; the terminal has pause/resume. This is a web composition of recorded app components, not remote control or a frame-by-frame native parity claim.
