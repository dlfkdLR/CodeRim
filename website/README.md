# CodeRim website

**English** · [한국어](README.ko.md)

A static Korean/English product, download, provider-setup and first-use site built with HTML, CSS, JavaScript and local images. It needs no package installation, build step, or API key and does not access app accounts or settings. This handbook defaults to English; the website currently defaults to Korean with an English language selector.

The project/activity feature shows the complete native panel, including its usage header and provider rings, with synthetic task and project names. The hero combines usage and project tasks inside each provider panel and automatically repeats Codex and Claude, without a pause button. Native render provenance is documented in [ASSETS](ASSETS.md).

## Local preview

Run from the repository root:

```sh
python3 -m http.server 4173 --bind 127.0.0.1 --directory website
```

Open [http://127.0.0.1:4173](http://127.0.0.1:4173); `/?lang=en` opens English. Refresh after edits. Basic content also works with `file://`; verify clipboard behavior over local HTTP.

## Deployment configuration

The recorded 2026-09-28 deployment uses [coderim.vercel.app](https://coderim.vercel.app), project `willbrains-projects/coderim`, and public domain [codrim.dlfkd.dev](https://codrim.dlfkd.dev). Hosting is Vercel and DNS is Cloudflare with the `codrim` CNAME DNS-only. These are recorded configuration facts; this documentation audit performs no deployment or fresh production verification.

Upload only `website`. CLI Root Directory is `.`, Framework is Other, Output Directory is `.`, Build/Install commands are empty, and no environment variables are required. [Vercel static build documentation](https://vercel.com/docs/builds/configure-a-build#skip-build-step). `vercel.json` serves static output; `.vercelignore` excludes maintenance docs, scripts and environment files. Keep `.vercel/` out of Git.

When an authorized deployment is requested, run from `website`:

```sh
vercel deploy --prod --scope willbrains-projects
```

Git automatic deployment is not connected in the recorded configuration. If connected later, Git Root Directory must be `website`. The original deployment record confirmed READY, unauthenticated HTTPS 200 and all 82 public-file SHA-256 values matching the local final build; maintenance docs/scripts returned 404. IDs and evidence are in `outputs/website-2026-09-28/deployment-verification.json`. [VALIDATION](VALIDATION.md) is the dated predeployment local record; these results are not rerun here.

## Maintaining content

- `index.html`: Korean default copy, `data-en` English copy, downloads and docs links.
- `styles.css`: Quiet Instrument neutral colors, information hierarchy and responsive layout.
- `app.js`: language/theme/mobile menus, native-UI composition and looping motion, provider filters, OS tabs and command copy. Language switches keep the current section position. Only site language/theme persist in localStorage.
- `terminal-examples.js`: real CLI output using a synthetic snapshot, animated limits → tokens → JSON with pause; offscreen/background stops and Reduce Motion uses static output.
- `providers.js`: 70 entries generated from provider guides, Korean README descriptions, and the shared name switch. Regenerate with the commands below. The generator accepts both `docs/providers/` and `docs/ko/providers/` Korean links.

```sh
python3 website/scripts/sync-providers.py
node --check website/app.js
node --check website/providers.js
```

A changed catalogue count stops generation and requires a review of site copy. Provider artwork comes from bundled SVG and native `GlyphOutline` paths; retain [ASSETS](ASSETS.md) and `assets/NOTICE.txt`. Documentation links should follow the selected language where a counterpart exists.

Download assets verified on 2026-09-28 are macOS `v2.1.13` DMG and Windows `v2.1.13` x64/ARM64 MSI. Check the actual platform assets rather than sharing `releases/latest`. For new releases update file URLs, versions, checksums, and release notes together.

## Demonstration and privacy boundary

Images are native CodeRim SwiftUI renders or recorded layout captures with example data. The editor background is authored HTML/CSS. Quotas, charts and CLI values are synthetic; account/widget renders are not fresh installed-app or live Notification Center proof. CLI SVG contains actual fixture command output. Images have no enlargement modal.

The seven feature slides cover limits, analytics, notch settings, accounts, CLI, macOS widgets and project/task activity in both languages. A full-width manual gallery shows two/three desktop cards or one larger mobile/tablet card, with arrows, direction keys, Home/End and touch scrolling. It uses subtle blurred/faded edges, no surrounding card border, disabled end arrows and instant Reduce Motion transitions.

The main scene loops resting pill → unfolded notch → Codex detail → Claude detail → project/task activity → folded notch using actual `NotchMotion` springs/delays and `SideNotchShape` geometry; each content view lasts about 2.3 seconds. The task list uses an actual SessionList render with matching 1.4-second rotating rings, paused offscreen/in the background/under Reduce Motion. The hero has no playback or provider-selection buttons; the separate terminal retains pause/resume. This is a web rendering of app motion, not a recording or remote control of the installed app. The count means catalogue coverage, not successful connections to every account. Windows remains a preview; local tokens, account limits and Apple notarization are described separately. No external analytics, remote fonts, or app-account requests are used.
