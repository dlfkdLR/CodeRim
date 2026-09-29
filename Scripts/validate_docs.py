#!/usr/bin/env python3
"""Validate maintained English/Korean guides against the repository catalogue.

Run: python3 Scripts/validate_docs.py [--report /absolute/result.json]
An optional --overlay validates staged documentation without changing --root.
Historical records and verbatim licenses retain their original language.
"""
from __future__ import annotations

import argparse
import ast
import html
import json
import os
from pathlib import Path
import re
from urllib.parse import unquote, urlsplit


def without_fences(text: str) -> str:
    return re.sub(r'^(`{3,}|~{3,})[^\n]*\n.*?^\1\s*$', '', text, flags=re.M | re.S)


def anchors(text: str) -> set[str]:
    text = without_fences(text)
    result = set(re.findall(r'<a\s+(?:id|name)=["\']([^"\']+)["\']', text))
    seen: dict[str, int] = {}
    for heading in re.findall(r'^#{1,6}\s+(.+?)\s*#*\s*$', text, flags=re.M):
        heading = re.sub(r'<[^>]+>', '', heading)
        heading = re.sub(r'\[([^\]]+)\]\([^)]*\)', r'\1', heading)
        slug = re.sub(r'[^\w\- ]', '', html.unescape(heading).lower()).replace(' ', '-')
        number = seen.get(slug, 0)
        seen[slug] = number + 1
        result.add(slug if number == 0 else f'{slug}-{number}')
    return result


def fenced_blocks(text: str) -> list[tuple[str, str]]:
    return re.findall(r'^```([^\n]*)\n(.*?)^```\s*$', text, flags=re.M | re.S)


def provider_ids(swift: str) -> list[str]:
    declaration = swift.split('public var name:', 1)[0]
    result = []
    for line in re.findall(r'^\s*case\s+(.+)$', declaration, flags=re.M):
        for case in line.split(','):
            fields = case.strip().split('=', 1)
            result.append(fields[1].strip().strip('"') if len(fields) == 2 else fields[0].strip())
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--overlay', type=Path)
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    overlay = args.overlay.resolve() if args.overlay else None
    errors: list[str] = []
    counts = {'bilingualPairs': 0, 'markdownFiles': 0, 'localLinks': 0, 'providers': 0,
              'matchingCodeBlocks': 0, 'providerEnvironmentPairs': 0}

    def selected(path: str) -> Path:
        candidate = overlay / path if overlay else None
        return candidate if candidate and candidate.exists() else root / path

    def read(path: str) -> str:
        return selected(path).read_text(encoding='utf-8')

    manifest = json.loads(read('docs/translations.json'))
    pairs: dict[str, str] = manifest['pairs']
    english_for_korean = {ko: en for en, ko in pairs.items()}
    if manifest.get('defaultLanguage') != 'en' or manifest.get('languages') != ['en', 'ko']:
        errors.append('Documentation must default to English and provide en/ko.')
    if len(pairs.values()) != len(set(pairs.values())):
        errors.append('Korean counterpart paths are not unique.')
    owned = sorted(set(pairs) | set(pairs.values()))
    texts: dict[str, str] = {}
    for path in owned:
        if not selected(path).is_file():
            errors.append(f'{path}: missing counterpart')
            continue
        texts[path] = read(path)
        counts['markdownFiles'] += 1
        if '\ufffd' in texts[path]:
            errors.append(f'{path}: replacement character in UTF-8 text')
    for en, ko in pairs.items():
        if en not in texts or ko not in texts:
            continue
        counts['bilingualPairs'] += 1
        en_link = os.path.relpath(ko, Path(en).parent)
        ko_link = os.path.relpath(en, Path(ko).parent)
        if f'**English** · [한국어]({en_link})' not in texts[en]:
            errors.append(f'{en}: missing English-default language navigation')
        if f'[English]({ko_link}) · **한국어**' not in texts[ko]:
            errors.append(f'{ko}: missing reciprocal language navigation')
        en_blocks, ko_blocks = fenced_blocks(texts[en]), fenced_blocks(texts[ko])
        if en_blocks != ko_blocks:
            errors.append(f'{en}: fenced commands/data differ from {ko}')
        else:
            counts['matchingCodeBlocks'] += len(en_blocks)
        if en.startswith('docs/providers/'):
            env = lambda data: set(re.findall(r'`([A-Z][A-Z0-9]*_[A-Z0-9_]+)`', data))
            if env(texts[en]) != env(texts[ko]):
                errors.append(f'{en}: environment keys differ: {sorted(env(texts[en]) ^ env(texts[ko]))}')
            else:
                counts['providerEnvironmentPairs'] += 1
    for path, text in texts.items():
        clean = without_fences(text)
        links = re.findall(r'\]\((<[^>]+>|[^\s)]+)(?:\s+["\'][^"\']*["\'])?\)', clean)
        links += re.findall(r'<(?:img|a)\b[^>]*\b(?:src|href)=["\']([^"\']+)["\']', clean)
        for link in links:
            link = html.unescape(link.strip('<>'))
            parsed = urlsplit(link)
            if parsed.scheme or parsed.netloc:
                continue
            destination = (root / Path(path).parent / unquote(parsed.path)).resolve() if parsed.path else root / path
            try:
                relative = str(destination.relative_to(root))
            except ValueError:
                errors.append(f'{path}: link escapes repository: {link}')
                continue
            counts['localLinks'] += 1
            if (path in english_for_korean and relative in pairs
                    and relative != english_for_korean[path]):
                errors.append(f'{path}: use the Korean counterpart for {relative}')
            target = selected(relative)
            if not target.exists():
                errors.append(f'{path}: missing local target: {link}')
                continue
            if parsed.fragment and target.suffix.lower() in ('.md', '.mdx'):
                fragment = unquote(parsed.fragment)
                if fragment not in anchors(target.read_text(encoding='utf-8')):
                    errors.append(f'{path}: missing fragment in {relative}: #{fragment}')
    ids = provider_ids(read('Sources/CodeRimShared/CompanionProviderID.swift'))
    counts['providers'] = len(ids)
    if len(ids) != len(set(ids)):
        errors.append('Duplicate shared provider IDs.')
    for path in ('docs/providers.md', 'docs/ko/providers.md'):
        rows = re.findall(r'^\| \[([^\]]+)\]\(providers/([^)]+)\.md\) \| `([^`]+)` \|', texts.get(path, ''), flags=re.M)
        row_ids = [row[1] for row in rows]
        if len(row_ids) != len(ids) or set(row_ids) != set(ids) or any(row[1] != row[2] for row in rows):
            errors.append(f'{path}: catalogue does not match shared source IDs')
    for path in ('README.md', 'README.ko.md'):
        guide_ids = re.findall(r'^- \[[^\]]+\]\(docs/(?:ko/)?providers/([^)]+)\.md\) — ', texts.get(path, ''), flags=re.M)
        if len(guide_ids) != len(ids) or set(guide_ids) != set(ids):
            errors.append(f'{path}: flat provider list does not match shared catalogue')
    for identifier in ids:
        for prefix in ('docs/providers/', 'docs/ko/providers/'):
            path = f'{prefix}{identifier}.md'
            if path not in texts or f'`{identifier}`' not in texts[path]:
                errors.append(f'{path}: missing provider ID or translated guide')
    generator = selected('website/scripts/sync-providers.py')
    if generator.is_file():
        data = generator.read_text(encoding='utf-8')
        ast.parse(data, filename=str(generator))
        # Read the extraction regex without executing the website asset writer.
        match = re.search(r"korean = dict\(re\.findall\(r(['\"])(.*?)\1", data, re.S)
        pattern = match.group(2) if match else None
        extracted = dict(re.findall(pattern, texts.get('README.ko.md', ''), flags=re.M)) if pattern else {}
        if set(extracted) != set(ids):
            errors.append('Website generator cannot read all Korean provider descriptions.')
    result = {'status': 'PASS' if not errors else 'FAIL', 'counts': counts, 'errors': errors,
              'root': str(root), 'overlay': str(overlay) if overlay else None,
              'scope': 'Maintained bilingual documentation; external URLs, live accounts, CI and historical test claims are not executed.'}
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 1 if errors else 0


if __name__ == '__main__':
    raise SystemExit(main())
