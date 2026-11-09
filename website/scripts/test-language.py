#!/usr/bin/env python3
"""Run the actual site in installed headless Chrome; no package installation."""
import functools
from html import unescape
import http.server
import json
import re
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading


SITE = Path(__file__).resolve().parents[1]
FIXTURE = r'''<!doctype html><meta charset="utf-8"><pre id="result">RUNNING</pre>
<iframe src="/index.html?lang=ko" width="1400" height="1000"></iframe>
<script>
document.querySelector('iframe').onload = () => {
  try {
    const w = document.querySelector('iframe').contentWindow, d = w.document;
    const $ = selector => d.querySelector(selector);
    const all = selector => [...d.querySelectorAll(selector)];
    const assert = (condition, message) => { if (!condition) throw new Error(message); };
    const files = new Set();
    const prefix = 'https://github.com/dlfkdLR/CodeRim/blob/main/';
    function links(language, count) {
      assert(d.documentElement.lang === language, 'document language');
      assert(all('.provider-card').length === count, 'provider count');
      assert(all('.featured-brand').length === 5, 'featured count');
      const guides = all('a[href^="' + prefix + 'docs/"], [data-en-href]');
      assert(all('[data-doc]').length === 16 && all('[data-en-href]').length === 2, 'static guide coverage');
      for (const link of guides) {
        const path = link.href.slice(prefix.length);
        const korean = path.startsWith('docs/ko/') || path.endsWith('.ko.md');
        assert(korean === (language === 'ko'), language + ' destination: ' + path);
        files.add(path);
      }
      assert($('.provider-card .icon use').getAttribute('href') === '#i-arrow', 'card icon');
      assert($('.featured-brand .brand-glyph').style.getPropertyValue('--brand-mark'), 'brand artwork');
    }
    links('ko', 12);
    $('#show-all-providers').click();
    links('ko', w.CODERIM_PROVIDERS.length);
    $('#theme-toggle').click();
    $('[data-platform="windows"]').click();
    $('#terminal-pause').click();
    const track = $('#feature-track');
    track.scrollTo({left: 500, behavior: 'instant'});
    const trackPosition = track.scrollLeft;
    const styles = () => ['.provider-card', '.featured-brand', '[data-doc]'].map(selector => {
      const node = $(selector), style = w.getComputedStyle(node);
      return [node.className, style.display, style.fontFamily, style.fontWeight, style.color];
    });
    const before = JSON.stringify(styles());
    function switchTo(language, count) {
      $('[data-language="' + language + '"]').click();
      links(language, count);
      assert(d.documentElement.dataset.theme === 'dark', 'theme retained');
      assert($('[data-platform="windows"]').getAttribute('aria-selected') === 'true', 'platform retained');
      assert($('#show-all-providers').getAttribute('aria-expanded') === 'true', 'expansion retained');
      assert($('#terminal-pause').getAttribute('aria-pressed') === 'true', 'terminal pause retained');
      assert(track.scrollLeft === trackPosition, 'carousel position retained');
      assert(JSON.stringify(styles()) === before, 'classes and computed styles retained');
    }
    switchTo('en', w.CODERIM_PROVIDERS.length);
    switchTo('ko', w.CODERIM_PROVIDERS.length);
    $('[data-filter="local"]').click();
    $('#provider-search').value = 'codex';
    $('#provider-search').dispatchEvent(new w.Event('input'));
    for (const language of ['en', 'ko']) {
      switchTo(language, 1);
      assert($('#provider-search').value === 'codex', 'search retained');
      assert($('[data-filter="local"]').getAttribute('aria-pressed') === 'true', 'filter retained');
    }
    $('#clear-search').click();
    links('ko', w.CODERIM_PROVIDERS.length);
    document.querySelector('#result').textContent = 'PASS ' + JSON.stringify([...files]);
  } catch (error) { document.querySelector('#result').textContent = 'FAIL ' + error.stack; }
};
</script>'''


class Handler(http.server.SimpleHTTPRequestHandler):
    def do_GET(self):
        if self.path == '/__language_test__':
            self.send_response(200)
            self.send_header('Content-Type', 'text/html; charset=utf-8')
            self.end_headers()
            self.wfile.write(FIXTURE.encode())
        else:
            super().do_GET()

    def log_message(self, *_):
        pass


chrome = shutil.which('google-chrome') or shutil.which('chromium')
if not chrome:
    chrome = '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'
assert Path(chrome).is_file(), 'Install Chrome/Chromium to run this browser regression'
with http.server.ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(Handler, directory=SITE)) as server:
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        with tempfile.TemporaryDirectory(prefix='coderim-site-test-') as profile:
            result = subprocess.run([
                chrome, '--headless', '--disable-gpu', '--no-first-run',
                f'--user-data-dir={profile}', '--dump-dom', '--virtual-time-budget=3000',
                f'http://127.0.0.1:{server.server_port}/__language_test__',
            ], capture_output=True, text=True, timeout=30, check=True)
        outcome = re.search(r'<pre id="result">(.*?)</pre>', result.stdout, flags=re.S)
        assert outcome, result.stdout
        outcome = unescape(outcome.group(1))
        assert outcome.startswith('PASS '), outcome
        paths = json.loads(outcome[5:])
        for path in paths:
            assert (SITE.parent / path).is_file(), f'Missing local guide: {path}'
        print(f'PASS: ko → en → ko, {len(paths)} guide paths, expanded/search/filter/theme/platform/pause/carousel/styles retained')
    finally:
        server.shutdown()
