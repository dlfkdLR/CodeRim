#!/usr/bin/env python3
"""Refresh website provider metadata and identification artwork from this checkout."""
from pathlib import Path
import argparse
import json
import re

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--source', type=Path, default=Path(__file__).resolve().parents[2])
parser.add_argument('--check', action='store_true', help='Fail on generated drift without writing files')
args = parser.parse_args()
source = args.source.resolve()
site = Path(__file__).resolve().parents[1]
assets = site / 'assets/providers'
if not args.check:
    assets.mkdir(parents=True, exist_ok=True)


def emit(path, data):
    if args.check:
        if not path.is_file() or path.read_bytes() != data:
            raise ValueError(f'Generated file is stale: {path.relative_to(site)}')
    else:
        path.write_bytes(data)


emit(site / 'assets/coderim.svg', (source / 'Assets/AppIcon.svg').read_bytes())
emit(site / 'assets/NOTICE.txt', (source / 'NOTICE').read_bytes())
emit(site / 'assets/LICENSE.txt', (source / 'LICENSE').read_bytes())
logo_source = source / 'Sources/CodeRim/Resources/ProviderLogos'
for svg in logo_source.glob('*.svg'):
    emit(assets / svg.name, svg.read_bytes())
for name, filename in {'codex': 'OpenAI.svg'}.items():
    emit(assets / f'{name}.svg', (logo_source / filename).read_bytes())

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
    emit(assets / f'{web_id}.svg', svg.encode())

swift = (source / 'Sources/CodeRimShared/CompanionProviderID.swift').read_text()
name_block = re.search(r'public var name: String \{(.*?)\n    \}', swift, re.S)
if not name_block:
    raise ValueError('Missing provider name switch')
names = dict(re.findall(r'case \.([\w]+):\s*"([^"]+)"', name_block.group(1)))
names.update({'ollama-local': 'Ollama Local', 'opencode-zen': 'OpenCode Zen', 'gemini-cli': 'Gemini CLI'})
def provider_rows(path):
    return re.findall(r'^\| \[([^\]]+)\]\(providers/([^)]+)\.md\) \| `([^`]+)` \| (.*?) \|$',
                      (source / path).read_text(), re.M)


rows = provider_rows('docs/providers.md')
korean_rows = provider_rows('docs/ko/providers.md')
korean = {provider_id: description for _, provider_id, _, description in korean_rows}
if len(korean_rows) != len(korean) or set(korean) != {row[1] for row in rows} or any(row[1] != row[2] for row in korean_rows):
    raise ValueError('Korean provider catalogue does not match the English IDs')
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
        'ko': korean[provider_id], 'en': description,
        'category': 'local' if provider_id in ('codex', 'claude') else 'api' if provider_id in api_ids else 'tool',
        'icon': image,
    })
providers.sort(key=lambda item: (first.index(item['id']) if item['id'] in first else 999, item['name'].casefold()))
if len(providers) != 70 or len({item['id'] for item in providers}) != 70:
    raise ValueError('Provider catalogue count changed; review the website count and copy before regenerating')
emit(site / 'providers.js', ('// Generated from the CodeRim provider catalogue and user guides. See ASSETS.md.\nwindow.CODERIM_PROVIDERS = ' + json.dumps(providers, ensure_ascii=False, indent=2) + ';\n').encode())
print(json.dumps({'providers': len(providers), 'logos': sum(bool(item['icon']) for item in providers)}, ensure_ascii=False))
