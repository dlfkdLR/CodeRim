#!/usr/bin/env python3
"""Refresh website provider metadata and identification artwork from this checkout."""
from pathlib import Path
import argparse
import json
import re
import shutil

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--source', type=Path, default=Path(__file__).resolve().parents[2])
args = parser.parse_args()
source = args.source.resolve()
site = Path(__file__).resolve().parents[1]
assets = site / 'assets/providers'
assets.mkdir(parents=True, exist_ok=True)
shutil.copy2(source / 'Assets/AppIcon.svg', site / 'assets/coderim.svg')
shutil.copy2(source / 'NOTICE', site / 'assets/NOTICE.txt')
shutil.copy2(source / 'LICENSE', site / 'assets/LICENSE.txt')
logo_source = source / 'Sources/CodeRim/Resources/ProviderLogos'
for svg in logo_source.glob('*.svg'):
    shutil.copy2(svg, assets / svg.name)
for name, filename in {'codex': 'OpenAI.svg'}.items():
    shutil.copy2(logo_source / filename, assets / f'{name}.svg')

# These unit-box paths are the existing native app's identification artwork.
# Keep every contour and its even-odd holes; do not redraw third-party marks.
outlines = (source / 'Sources/CodeRim/Notch/GlyphOutline.swift').read_text()
for web_id, native_id in {
    'cursor': 'cursor', 'copilot': 'copilot', 'ollama': 'ollama',
    'ollama-local': 'ollama', 'gemini-cli': 'gemini', 'gemini': 'antigravity',
    'glm': 'glm', 'grok': 'grok', 'commandcode': 'commandcode',
}.items():
    match = re.search(rf'static let {native_id}: \[\[CGPoint\]\] = (\[.*?\n    \])', outlines, re.S)
    if not match:
        raise ValueError(f'Missing native outline: {native_id}')
    contours = re.findall(r'\[\s*CGPoint.*?\]', match.group(1), re.S)
    paths = []
    for contour in contours:
        points = re.findall(r'CGPoint\(x:\s*([\d.-]+), y:\s*([\d.-]+)\)', contour)
        if not points:
            raise ValueError(f'Empty contour: {native_id}')
        coordinates = [f'{float(x)*100:.4f} {float(y)*100:.4f}' for x, y in points]
        paths.append('M' + ' L'.join(coordinates) + ' Z')
    svg = '<!-- Exported from CodeRim GlyphOutline.swift; see assets/NOTICE.txt. -->\n'
    svg += '<svg xmlns="http://www.w3.org/2000/svg" viewBox="-3 -3 106 106">\n'
    svg += '<path fill="black" fill-rule="evenodd" d="' + ' '.join(paths) + '"/>\n</svg>\n'
    (assets / f'{web_id}.svg').write_text(svg)

swift = (source / 'Sources/CodeRimShared/CompanionProviderID.swift').read_text()
name_block = re.search(r'public var name: String \{(.*?)\n    \}', swift, re.S)
if not name_block:
    raise ValueError('Missing provider name switch')
names = dict(re.findall(r'case \.([\w]+):\s*"([^"]+)"', name_block.group(1)))
names.update({'ollama-local': 'Ollama Local', 'opencode-zen': 'OpenCode Zen', 'gemini-cli': 'Gemini CLI'})
korean = dict(re.findall(r'^- \[[^\]]+\]\(docs/(?:ko/)?providers/([^)]+)\.md\) — (.+)$', (source / 'README.ko.md').read_text(), re.M))
rows = re.findall(r'^\| \[([^\]]+)\]\(providers/([^)]+)\.md\) \| `([^`]+)` \| (.*?) \|$', (source / 'docs/providers.md').read_text(), re.M)
api_ids = set('openai azureopenai clinepass fireworks vertexai moonshot synthetic openrouter elevenlabs perplexity deepseek deepinfra venice bedrock groq llmproxy litellm deepgram poe chutes neuralwatt clawrouter sub2api zenmux xai aiand'.split())
first = ['codex', 'claude', 'cursor', 'copilot', 'gemini-cli', 'gemini', 'opencode', 'glm', 'ollama', 'ollama-local', 'openai', 'openrouter']
providers = []
asset_names = {path.name for path in assets.iterdir() if path.is_file()}
for fallback, provider_id, cli_id, description in rows:
    if provider_id != cli_id or not (source / f'docs/providers/{provider_id}.md').is_file():
        raise ValueError(f'Provider guide mismatch: {provider_id}')
    image = next((name for name in [f'{provider_id}.svg', f'ProviderIcon-{provider_id}.svg'] if name in asset_names), None)
    if provider_id in ('openai', 'claude'):
        image = {'openai': 'OpenAI.svg', 'claude': 'Claude.svg'}[provider_id]
    if image and image not in asset_names:
        raise ValueError(f'Provider asset filename mismatch: {provider_id}: {image}')
    providers.append({
        'id': provider_id, 'name': names.get(provider_id, fallback),
        'ko': korean.get(provider_id, description), 'en': description,
        'category': 'local' if provider_id in ('codex', 'claude') else 'api' if provider_id in api_ids else 'tool',
        'icon': image,
    })
providers.sort(key=lambda item: (first.index(item['id']) if item['id'] in first else 999, item['name'].casefold()))
if len(providers) != 70 or len({item['id'] for item in providers}) != 70:
    raise ValueError('Provider catalogue count changed; review the website count and copy before regenerating')
(site / 'providers.js').write_text('// Generated from the CodeRim provider catalogue and user guides. See ASSETS.md.\nwindow.CODERIM_PROVIDERS = ' + json.dumps(providers, ensure_ascii=False, indent=2) + ';\n')
print(json.dumps({'providers': len(providers), 'logos': sum(bool(item['icon']) for item in providers)}, ensure_ascii=False))
