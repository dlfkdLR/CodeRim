(() => {
  'use strict';
  history.scrollRestoration = 'manual';

  const root = document.documentElement;
  const translatedNodes = [...document.querySelectorAll('[data-en]')];
  const originals = new Map(translatedNodes.map(node => [node, node.innerHTML]));
  const labelNodes = [...document.querySelectorAll('[data-en-label]')];
  const originalLabels = new Map(labelNodes.map(node => [node, node.getAttribute('aria-label')]));
  const search = document.getElementById('provider-search');
  const grid = document.getElementById('provider-grid');
  const count = document.getElementById('provider-count');
  const showAll = document.getElementById('show-all-providers');
  const menuButton = document.getElementById('menu-toggle');
  const nav = document.getElementById('primary-nav');
  const themeButton = document.getElementById('theme-toggle');
  const languageButton = document.getElementById('language-toggle');
  const providers = window.CODERIM_PROVIDERS || [];
  let language = 'ko';
  let filter = 'all';
  let expanded = false;
  let screen = 'usage';
  let toastTimer;

  const readSetting = key => {
    try { return localStorage.getItem(`coderim-site-${key}`); } catch { return null; }
  };
  const writeSetting = (key, value) => {
    try { localStorage.setItem(`coderim-site-${key}`, value); } catch { /* Browsing without storage remains supported. */ }
  };
  const t = (ko, en) => language === 'en' ? en : ko;
  const icon = name => {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.classList.add('icon');
    svg.setAttribute('aria-hidden', 'true');
    const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    use.setAttribute('href', `#i-${name}`);
    svg.append(use);
    return svg;
  };
  function glyph(provider) {
    const span = document.createElement('span');
    span.setAttribute('aria-hidden', 'true');
    if (provider.icon) {
      span.className = 'brand-glyph';
      span.style.setProperty('--brand-mark', `url('assets/providers/${provider.icon}')`);
    } else {
      span.className = 'monogram';
      span.textContent = provider.monogram || provider.name.slice(0, 2).toUpperCase();
    }
    return span;
  }

  function renderProviders() {
    const query = search.value.normalize('NFKC').trim().toLocaleLowerCase();
    const matches = providers.filter(provider => {
      const categoryMatches = filter === 'all' || provider.category === filter;
      const searchable = `${provider.name} ${provider.id} ${provider.ko} ${provider.en}`.normalize('NFKC').toLocaleLowerCase();
      return categoryMatches && (!query || searchable.includes(query));
    });
    const showEverything = expanded || !!query || filter !== 'all';
    const visible = showEverything ? matches : matches.slice(0, 12);
    const fragment = document.createDocumentFragment();
    for (const provider of visible) {
      const link = document.createElement('a');
      link.className = 'provider-card';
      link.href = `https://github.com/dlfkdLR/CodeRim/blob/main/docs/providers/${provider.id}.md`;
      link.title = language === 'en' ? provider.en : provider.ko;
      const copy = document.createElement('span');
      copy.className = 'provider-card-copy';
      const name = document.createElement('span');
      name.className = 'provider-card-name';
      name.textContent = provider.name;
      const kind = document.createElement('span');
      kind.className = 'provider-card-kind';
      kind.textContent = provider.category === 'local' ? t('로컬 기록 · 사용 한도', 'Local history & limits') : provider.category === 'api' ? t('API · 플랫폼 연결 안내', 'API & platform guide') : t('제공자 연결 안내', 'Connection guide');
      copy.append(name, kind);
      link.append(glyph(provider), copy, icon('arrow'));
      fragment.append(link);
    }
    grid.replaceChildren(fragment);
    document.getElementById('catalogue-empty').hidden = matches.length > 0;
    count.textContent = t(`${matches.length}개 제공자 중 ${visible.length}개 표시`, `${visible.length} of ${matches.length} providers`);
    showAll.hidden = !!query || filter !== 'all' || matches.length <= 12;
    showAll.setAttribute('aria-expanded', String(expanded));
    showAll.querySelector('span').textContent = expanded ? t('간단히 보기', 'Show fewer') : t(`${providers.length}개 제공자 모두 보기`, `Show all ${providers.length} providers`);
  }

  function renderFeatured() {
    const featured = ['codex', 'claude', 'cursor', 'copilot', 'gemini-cli'];
    const fragment = document.createDocumentFragment();
    for (const id of featured) {
      const provider = providers.find(item => item.id === id);
      if (!provider) continue;
      const link = document.createElement('a');
      link.href = `https://github.com/dlfkdLR/CodeRim/blob/main/docs/providers/${provider.id}.md`;
      link.className = 'featured-brand';
      link.append(glyph(provider), document.createTextNode(provider.name === 'GitHub Copilot' ? 'Copilot' : provider.name === 'Gemini CLI' ? 'Gemini' : provider.name));
      fragment.append(link);
    }
    document.getElementById('featured-brands').replaceChildren(fragment);
  }

  function updateThemeLabel() {
    const dark = root.dataset.theme === 'dark';
    themeButton.setAttribute('aria-label', dark ? t('밝은 테마로 전환', 'Switch to light theme') : t('어두운 테마로 전환', 'Switch to dark theme'));
    themeButton.querySelector('use').setAttribute('href', dark ? '#i-sun' : '#i-moon');
    document.querySelector('meta[name="theme-color"]').content = dark ? '#101010' : '#ffffff';
  }
  function setTheme(theme, persist = true) {
    root.dataset.theme = theme === 'dark' ? 'dark' : 'light';
    if (persist) writeSetting('theme', root.dataset.theme);
    updateThemeLabel();
  }

  function setMenu(open) {
    nav.classList.toggle('is-open', open);
    menuButton.setAttribute('aria-expanded', String(open));
    menuButton.setAttribute('aria-label', open ? t('메뉴 닫기', 'Close menu') : t('메뉴 열기', 'Open menu'));
    menuButton.querySelector('use').setAttribute('href', open ? '#i-close' : '#i-menu');
  }

  const screens = {
    usage: { alt: ['CodeRim의 Codex 사용량과 프로젝트별 작업 상태가 함께 보이는 패널', 'CodeRim panel showing Codex usage and project task status together'] },
    claude: { alt: ['CodeRim의 Claude Code 사용량과 프로젝트별 작업 상태가 함께 보이는 패널', 'CodeRim panel showing Claude Code usage and project task status together'] }
  };
  function updateScreen() {
    const data = screens[screen];
    document.getElementById('native-demo').dataset.provider = screen;
    document.querySelectorAll('[data-provider-card]').forEach(image => {
      const active = image.dataset.providerCard === screen;
      image.classList.toggle('is-active', active);
      image.setAttribute('aria-hidden', String(!active));
      image.alt = active ? data.alt[language === 'en' ? 1 : 0] : '';
    });
  }
  function setLanguageMenu(open, focusChoice = false) {
    const menu = document.getElementById('language-menu');
    menu.hidden = !open;
    languageButton.setAttribute('aria-expanded', String(open));
    if (open && focusChoice) menu.querySelector(`[data-language="${language}"]`).focus({ preventScroll: true });
  }

  function setLanguage(value, persist = true) {
    const headerHeight = document.querySelector('.site-header').offsetHeight;
    const viewportPoint = headerHeight + (window.innerHeight - headerHeight) / 2;
    const atPoint = persist ? document.elementFromPoint(window.innerWidth / 2, viewportPoint) : null;
    const anchor = atPoint?.closest('main article, main details, main section');
    const anchorTop = anchor?.getBoundingClientRect().top;
    language = value === 'en' ? 'en' : 'ko';
    root.lang = language;
    for (const node of translatedNodes) {
      if (language === 'ko') node.innerHTML = originals.get(node);
      else node.textContent = node.dataset.en;
    }
    document.querySelectorAll('[data-en-alt]').forEach(image => { image.dataset.koAlt ||= image.alt; image.alt = language === 'en' ? image.dataset.enAlt : image.dataset.koAlt; });
    for (const node of labelNodes) node.setAttribute('aria-label', language === 'en' ? node.dataset.enLabel : originalLabels.get(node));
    search.placeholder = t('도구 이름 검색', 'Search your tools');
    languageButton.querySelector('span').textContent = language.toUpperCase();
    languageButton.setAttribute('aria-label', t('언어 선택', 'Choose a language'));
    document.querySelectorAll('[data-language]').forEach(button => button.setAttribute('aria-checked', String(button.dataset.language === language)));
    const title = t('CodeRim — 코딩의 흐름은 그대로. 사용량은 한눈에.', 'CodeRim — Stay in your flow. Keep usage in sight.');
    document.title = title;
    document.querySelector('meta[property="og:title"]').content = title;
    const description = t('CodeRim은 AI 코딩 도구의 사용 한도, 초기화 시간, 로컬 토큰 기록을 화면 가장자리에서 보여주는 무료 오픈 소스 앱입니다. macOS와 Windows에서 만나보세요.', 'CodeRim is a free, open-source app that keeps AI coding limits, reset countdowns, and local token history at the edge of your screen. For macOS and Windows.');
    document.querySelector('meta[name="description"]').content = description;
    document.querySelector('meta[property="og:description"]').content = description;
    updateThemeLabel();
    setMenu(nav.classList.contains('is-open'));
    renderProviders();
    updateScreen();
    updateTerminalPauseLabel();
    updateTerminalDescription();
    updateFeatureCarousel();
    if (featureAnnouncement.textContent) announceFeatures();
    if (persist) {
      writeSetting('language', language);
      const url = new URL(location.href);
      if (language === 'en') url.searchParams.set('lang', 'en');
      else url.searchParams.delete('lang');
      history.replaceState(null, '', url);
      if (anchor) window.scrollTo({ top: window.scrollY + anchor.getBoundingClientRect().top - anchorTop, behavior: 'instant' });
    }
  }

  function notify(message) {
    const toast = document.getElementById('toast');
    clearTimeout(toastTimer);
    toast.textContent = message;
    toast.hidden = false;
    toastTimer = setTimeout(() => { toast.hidden = true; }, 3500);
  }
  async function copyCommand(button) {
    const text = document.getElementById(button.dataset.copy).textContent.trim();
    try {
      if (navigator.clipboard && window.isSecureContext) {
        await navigator.clipboard.writeText(text);
      } else {
        const field = document.createElement('textarea');
        field.value = text;
        field.style.position = 'fixed';
        field.style.left = '-9999px';
        document.body.append(field);
        field.select();
        let copied = false;
        try { copied = document.execCommand('copy'); } finally { field.remove(); button.focus(); }
        if (!copied) throw new Error('Clipboard unavailable');
      }
      button.querySelector('use').setAttribute('href', '#i-check');
      notify(t('설치 명령을 복사했습니다.', 'Install command copied.'));
      setTimeout(() => { button.querySelector('use').setAttribute('href', '#i-copy'); }, 2200);
    } catch {
      notify(t('복사할 수 없습니다. 표시된 명령을 직접 선택해 복사해 주세요.', 'Clipboard unavailable. Select the displayed command and copy it manually.'));
    }
  }

  function setPlatform(platform, moveFocus = false) {
    document.querySelectorAll('[data-platform]').forEach(button => {
      const selected = button.dataset.platform === platform;
      button.setAttribute('aria-selected', String(selected));
      button.tabIndex = selected ? 0 : -1;
      document.getElementById(button.getAttribute('aria-controls')).hidden = !selected;
      if (selected && moveFocus) button.focus();
    });
  }

  themeButton.addEventListener('click', () => setTheme(root.dataset.theme === 'dark' ? 'light' : 'dark'));
  languageButton.addEventListener('click', () => setLanguageMenu(languageButton.getAttribute('aria-expanded') !== 'true', true));
  languageButton.addEventListener('keydown', event => { if (event.key === 'ArrowDown') { event.preventDefault(); setLanguageMenu(true, true); } });
  const languageChoices = [...document.querySelectorAll('[data-language]')];
  languageChoices.forEach(button => {
    button.addEventListener('click', () => { setLanguage(button.dataset.language); setLanguageMenu(false); languageButton.focus({ preventScroll: true }); });
    button.addEventListener('keydown', event => {
      if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
      event.preventDefault();
      const index = languageChoices.indexOf(button);
      const target = event.key === 'Home' ? 0 : event.key === 'End' ? languageChoices.length - 1 : (index + (event.key === 'ArrowDown' ? 1 : -1) + languageChoices.length) % languageChoices.length;
      languageChoices[target].focus({ preventScroll: true });
    });
  });
  document.getElementById('language-menu').addEventListener('focusout', event => {
    if (!document.querySelector('.language-control').contains(event.relatedTarget)) setLanguageMenu(false);
  });
  document.addEventListener('click', event => { if (!event.target.closest('.language-control')) setLanguageMenu(false); });
  menuButton.addEventListener('click', () => setMenu(menuButton.getAttribute('aria-expanded') !== 'true'));
  nav.addEventListener('click', event => { if (event.target.closest('a')) setMenu(false); });
  document.addEventListener('click', event => { if (!event.target.closest('.site-header') && nav.classList.contains('is-open')) setMenu(false); });
  document.addEventListener('keydown', event => {
    if (event.key !== 'Escape') return;
    if (nav.classList.contains('is-open')) { setMenu(false); menuButton.focus(); }
    if (languageButton.getAttribute('aria-expanded') === 'true') { setLanguageMenu(false); languageButton.focus({ preventScroll: true }); }
  });
  const mobileQuery = window.matchMedia('(max-width: 620px)');
  mobileQuery.addEventListener('change', () => setMenu(false));
  search.addEventListener('input', renderProviders);
  document.querySelectorAll('[data-filter]').forEach(button => button.addEventListener('click', () => {
    filter = button.dataset.filter;
    document.querySelectorAll('[data-filter]').forEach(item => item.setAttribute('aria-pressed', String(item === button)));
    renderProviders();
  }));
  showAll.addEventListener('click', () => {
    expanded = !expanded;
    renderProviders();
    if (!expanded) document.getElementById('providers').scrollIntoView({ block: 'start', behavior: window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth' });
  });
  document.getElementById('clear-search').addEventListener('click', () => { search.value = ''; filter = 'all'; document.querySelectorAll('[data-filter]').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.filter === 'all'))); renderProviders(); search.focus(); });
  const terminalExamples = window.CODERIM_TERMINAL_EXAMPLES || [];
  const terminalCommand = document.getElementById('terminal-command-text');
  const terminalOutput = document.getElementById('terminal-output');
  const terminalPause = document.getElementById('terminal-pause');
  const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  const terminalState = { index: 0, paused: false, visible: false, phase: 'typing', character: 0, line: 0, timer: null };
  function updateTerminalPauseLabel() {
    terminalPause.hidden = reducedMotion.matches;
    terminalPause.setAttribute('aria-pressed', String(terminalState.paused));
    terminalPause.setAttribute('aria-label', terminalState.paused ? t('터미널 애니메이션 재생', 'Play terminal animation') : t('터미널 애니메이션 일시 정지', 'Pause terminal animation'));
    terminalPause.querySelector('use').setAttribute('href', terminalState.paused ? '#i-play' : '#i-pause');
  }
  function scheduleTerminal(delay) {
    clearTimeout(terminalState.timer);
    if (!terminalState.visible || terminalState.paused || document.hidden || reducedMotion.matches) return;
    terminalState.timer = setTimeout(tickTerminal, delay);
  }
  function updateTerminalDescription() {
    const data = terminalExamples[terminalState.index];
    if (data) document.getElementById('terminal-description').textContent = `${t('예시 명령과 출력', 'Example command and output')}: ${data.command}\n${data.output}`;
  }
  function selectTerminal(index) {
    clearTimeout(terminalState.timer);
    terminalState.index = index;
    terminalState.phase = 'typing';
    terminalState.character = 0;
    terminalState.line = 0;
    const data = terminalExamples[index];
    if (!data) return;
    document.getElementById('terminal-demo').dataset.example = String(index);
    updateTerminalDescription();
    document.querySelector('.terminal-body').scrollTop = 0;
    if (reducedMotion.matches || terminalState.paused) {
      terminalCommand.textContent = data.command;
      terminalOutput.textContent = data.output;
      terminalState.phase = 'hold';
    } else {
      terminalCommand.textContent = '';
      terminalOutput.textContent = '';
      scheduleTerminal(150);
    }
  }
  function tickTerminal() {
    if (!terminalState.visible || terminalState.paused || document.hidden || reducedMotion.matches) return;
    const data = terminalExamples[terminalState.index];
    if (!data) return;
    if (terminalState.phase === 'typing') {
      terminalCommand.textContent = data.command.slice(0, ++terminalState.character);
      if (terminalState.character >= data.command.length) terminalState.phase = 'output';
      scheduleTerminal(terminalState.phase === 'output' ? 300 : 32);
    } else if (terminalState.phase === 'output') {
      const lines = data.output.split('\n');
      terminalOutput.textContent = lines.slice(0, ++terminalState.line).join('\n');
      const body = document.querySelector('.terminal-body');
      if (terminalState.index === 2) body.scrollTop = Math.max(0, body.scrollHeight - body.clientHeight);
      if (terminalState.line >= lines.length) terminalState.phase = 'hold';
      scheduleTerminal(terminalState.phase === 'hold' ? 1800 : 55);
    } else {
      selectTerminal((terminalState.index + 1) % terminalExamples.length);
    }
  }
  terminalPause.addEventListener('click', () => {
    terminalState.paused = !terminalState.paused;
    updateTerminalPauseLabel();
    if (terminalState.paused) clearTimeout(terminalState.timer);
    else scheduleTerminal(150);
  });
  new IntersectionObserver(entries => {
    terminalState.visible = entries[0].isIntersecting && entries[0].intersectionRatio >= 0.2;
    if (terminalState.visible) scheduleTerminal(150);
    else clearTimeout(terminalState.timer);
  }, { threshold: 0.2 }).observe(document.getElementById('terminal-demo'));
  document.addEventListener('visibilitychange', () => { if (document.hidden) clearTimeout(terminalState.timer); else scheduleTerminal(150); });
  reducedMotion.addEventListener('change', () => { updateTerminalPauseLabel(); selectTerminal(terminalState.index); });
  selectTerminal(0);

  // Geometry is measured from NotchViewModel by render-motion.swift. Springs and
  // staggers follow NotchMotion.swift; the outline follows SideNotchShape.swift.
  const notchGeometry = {
  "cardOffset": 9.025641025641026,
  "cellExtent": 71.11623931623932,
  "cellOffset": 10.52991452991453,
  "corner": 29.634188034188032,
  "curl": 38.73504273504273,
  "depth": 69.94871794871794,
  "length": 296.0820512820513,
  "orbAlong": 416.45299145299145,
  "orbInset": 38.73504273504273,
  "orbMergeScale": 1.5921052631578947,
  "panelHeight": 536.8239316239317,
  "panelWidth": 334.3247863247863,
  "restingDepth": 9.777777777777779,
  "restingLength": 78.97435897435898,
  "ring0": 207.24273504273503,
  "ring1": 309.7606837606837,
  "slack": 120.37094017094017
};
  const nativeDemo = document.getElementById('native-demo');
  const cardStack = nativeDemo.querySelector('.native-card-stack');
  const nativeCards = [...nativeDemo.querySelectorAll('[data-provider-card]')];
  const workingRings = [...nativeDemo.querySelectorAll('[data-provider-rings]')];
  const nativeNotch = nativeDemo.querySelector('.native-notch-image');
  const notchPath = document.getElementById('native-notch-shape');
  const notchClip = document.querySelector('#native-notch-clip path');
  const nativeCells = [document.getElementById('native-codex-cell'), document.getElementById('native-claude-cell')];
  const nativeOrb = document.getElementById('native-orb');
  const demoState = { elapsed: 0, visible: false, frame: null, lastTime: null };
  let cardPositions = { codex: 25, claude: 128, offset: 10 };
  const clamp = value => Math.max(0, Math.min(1, value));
  function spring(response, damping, seconds) {
    if (seconds <= 0) return 0;
    const frequency = 2 * Math.PI / response;
    const damped = frequency * Math.sqrt(1 - damping * damping);
    return 1 - Math.exp(-damping * frequency * seconds) *
      (Math.cos(damped * seconds) + damping / Math.sqrt(1 - damping * damping) * Math.sin(damped * seconds));
  }
  function nativeOutline(progress) {
    const g = notchGeometry;
    const depth = g.restingDepth + (g.depth - g.restingDepth) * progress;
    const length = g.restingLength + (g.length - g.restingLength) * progress;
    const left = g.panelWidth - depth;
    const top = g.slack + (g.length - length) / 2;
    const right = g.panelWidth;
    const bottom = top + length;
    const wanted = Math.max(0, Math.min(g.corner, depth / 2));
    const curl = Math.max(0, Math.min(g.curl, length / 2, depth - wanted));
    const corner = Math.max(0, Math.min(wanted, (length - 2 * curl) / 2));
    return `M ${right} ${top} A ${curl} ${curl} 0 0 1 ${right - curl} ${top + curl} H ${left + corner} A ${corner} ${corner} 0 0 0 ${left} ${top + curl + corner} V ${bottom - curl - corner} A ${corner} ${corner} 0 0 0 ${left + corner} ${bottom - curl} H ${right - curl} A ${curl} ${curl} 0 0 1 ${right} ${bottom} Z`;
  }
  function updateCardPositions() {
    const notchStyle = getComputedStyle(nativeNotch);
    const notchScale = parseFloat(notchStyle.height) / notchGeometry.panelHeight;
    // Position each complete native card by its actual height and provider ring.
    const position = (image, center) => {
      const ratio = image.naturalHeight && image.naturalWidth
        ? image.naturalHeight / image.naturalWidth
        : Number(image.getAttribute('height')) / Number(image.getAttribute('width'));
      const height = cardStack.offsetWidth * ratio;
      return Math.max(12, Math.min(nativeDemo.clientHeight - height - 12,
        parseFloat(notchStyle.top) + center * notchScale - height / 2));
    };
    cardPositions = {
      codex: position(nativeCards[0], notchGeometry.ring0),
      claude: position(nativeCards[1], notchGeometry.ring1),
      offset: notchGeometry.cardOffset * cardStack.offsetWidth / 254
    };
    renderDemo();
  }
  function renderDemo() {
    const seconds = (demoState.elapsed % 7900) / 1000;
    let opening = 0, content = [0, 0], orb = 0, cardOpacity = 0, cardX = cardPositions.offset;
    let cardY = cardPositions.codex, blends = [1, 0], phase = 'rest';
    if (reducedMotion.matches) {
      opening = orb = cardOpacity = 1; content = [1, 1]; cardX = 0; phase = 'static';
    } else if (seconds >= 0.5 && seconds < 6.2) {
      const sinceOpen = seconds - 0.5;
      opening = spring(0.42, 0.78, sinceOpen);
      content = [0, 1].map(index => spring(0.36, 0.82, sinceOpen - index * 0.045));
      orb = spring(0.36, 0.82, sinceOpen - 0.09);
      phase = 'unfold';
      if (seconds >= 1.1) {
        const arrival = spring(0.5, 0.86, seconds - 1.1);
        cardOpacity = clamp(arrival);
        cardX = cardPositions.offset * (1 - arrival);
        phase = 'codex';
      }
      if (seconds >= 3.4) {
        const glide = spring(0.5, 0.86, seconds - 3.4);
        cardY = cardPositions.codex + (cardPositions.claude - cardPositions.codex) * glide;
        const fadeTime = clamp((seconds - 3.4) / 0.16);
        const crossfade = (1 - Math.cos(Math.PI * fadeTime)) / 2;
        blends = [1 - crossfade, crossfade];
        phase = 'claude';
      }
      if (seconds >= 5.7) {
        const departure = spring(0.5, 0.86, seconds - 5.7);
        cardOpacity = clamp(1 - departure);
        cardX = cardPositions.offset * departure;
        phase = 'card-hide';
      }
    } else if (seconds >= 6.2 && seconds < 6.9) {
      const sinceClose = seconds - 6.2;
      opening = 1 - spring(0.42, 0.78, sinceClose);
      content = [0, 1].map(index => 1 - spring(0.36, 0.82, sinceClose - index * 0.045));
      orb = 1 - Math.pow(clamp(sinceClose / 0.2), 2);
      cardY = cardPositions.claude; blends = [0, 1]; phase = 'fold';
    }
    const outline = nativeOutline(opening);
    notchPath.setAttribute('d', outline);
    notchClip.setAttribute('d', outline);
    nativeCells.forEach((cell, index) => {
      cell.setAttribute('opacity', String(clamp(content[index])));
      cell.setAttribute('transform', `translate(${notchGeometry.cellOffset * (1 - content[index])} 0)`);
    });
    nativeOrb.setAttribute('opacity', String(clamp(orb)));
    const orbScale = 1 + (1 - orb) * (notchGeometry.orbMergeScale - 1);
    const orbX = notchGeometry.panelWidth - notchGeometry.orbInset;
    nativeOrb.setAttribute('transform', `translate(${orbX} ${notchGeometry.orbAlong}) scale(${orbScale}) translate(${-orbX} ${-notchGeometry.orbAlong})`);
    cardStack.style.opacity = String(cardOpacity);
    cardStack.style.transform = `translate(${cardX}px, ${cardY}px)`;
    nativeCards.forEach((card, index) => { card.style.opacity = String(blends[index]); });
    workingRings.forEach((rings, index) => { rings.style.opacity = String(blends[index]); });
    const provider = nativeCards[blends.indexOf(Math.max(...blends))].dataset.providerCard;
    if (screen !== provider) { screen = provider; updateScreen(); }
    nativeDemo.dataset.demoPhase = phase;
  }
  function tickDemo(time) {
    demoState.frame = null;
    if (demoState.lastTime !== null) demoState.elapsed += time - demoState.lastTime;
    demoState.lastTime = time;
    renderDemo();
    demoState.frame = requestAnimationFrame(tickDemo);
  }
  function syncDemoPlayback() {
    const play = demoState.visible && !document.hidden && !reducedMotion.matches;
    nativeDemo.dataset.demoPaused = String(!play);
    if (!play) {
      if (demoState.frame !== null) cancelAnimationFrame(demoState.frame);
      demoState.frame = null; demoState.lastTime = null;
    } else if (demoState.frame === null) {
      demoState.lastTime = null;
      demoState.frame = requestAnimationFrame(tickDemo);
    }
  }
  new IntersectionObserver(entries => { demoState.visible = entries[0].isIntersecting && entries[0].intersectionRatio >= 0.15; syncDemoPlayback(); }, { threshold: 0.15 }).observe(nativeDemo);
  new ResizeObserver(updateCardPositions).observe(cardStack);
  document.addEventListener('visibilitychange', syncDemoPlayback);
  reducedMotion.addEventListener('change', () => { demoState.elapsed = 0; renderDemo(); syncDemoPlayback(); });
  updateCardPositions();

  document.querySelectorAll('[data-copy]').forEach(button => button.addEventListener('click', () => copyCommand(button)));
  const platformTabs = [...document.querySelectorAll('[data-platform]')];
  platformTabs.forEach(button => {
    button.addEventListener('click', () => setPlatform(button.dataset.platform));
    button.addEventListener('keydown', event => {
      if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
      event.preventDefault();
      const index = platformTabs.indexOf(button);
      const target = event.key === 'Home' ? 0 : event.key === 'End' ? platformTabs.length - 1 : (index + (event.key === 'ArrowRight' ? 1 : -1) + platformTabs.length) % platformTabs.length;
      setPlatform(platformTabs[target].dataset.platform, true);
    });
  });

  function scrollToSection(target, smooth = true) {
    const header = document.querySelector('.site-header').offsetHeight;
    let offset = header + 24;
    if (target.id === 'download') {
      const available = window.innerHeight - header - 48;
      offset += Math.max(0, (available - target.offsetHeight) / 2);
    }
    const top = Math.max(0, window.scrollY + target.getBoundingClientRect().top - offset);
    window.scrollTo({ top, behavior: smooth && !window.matchMedia('(prefers-reduced-motion: reduce)').matches ? 'smooth' : 'instant' });
    if (target.id === 'main') { target.tabIndex = -1; target.focus({ preventScroll: true }); }
  }
  document.querySelectorAll('a[href^="#"]').forEach(link => link.addEventListener('click', event => {
    const target = document.getElementById(link.getAttribute('href').slice(1));
    if (!target) return;
    event.preventDefault();
    setMenu(false);
    history.pushState(null, '', link.getAttribute('href'));
    scrollToSection(target);
  }));
  window.addEventListener('load', () => {
    const target = document.getElementById(location.hash.slice(1));
    if (target) scrollToSection(target, false);
  }, { once: true });
  window.addEventListener('popstate', () => {
    const target = document.getElementById(location.hash.slice(1));
    if (target) scrollToSection(target, false);
  });

  const featureTrack = document.getElementById('feature-track');
  const featureCards = [...featureTrack.querySelectorAll('.feature-card')];
  const featurePrev = document.getElementById('feature-prev');
  const featureNext = document.getElementById('feature-next');
  const featurePosition = document.getElementById('feature-position');
  const featureAnnouncement = document.getElementById('feature-announcement');
  let featureScrollFrame = 0;
  let featureTarget = null;
  let featureSettleTimer;

  function featureLayout() {
    const style = getComputedStyle(featureTrack);
    const gap = parseFloat(style.gap) || 0;
    const contentWidth = featureTrack.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
    const step = featureCards[0].getBoundingClientRect().width + gap;
    const visible = Math.max(1, Math.floor((contentWidth + gap + 1) / step));
    const maxStart = featureCards.length - visible;
    const atEnd = featureTrack.scrollLeft >= featureTrack.scrollWidth - featureTrack.clientWidth - 2;
    const index = atEnd ? maxStart : Math.min(maxStart, Math.max(0, Math.round(featureTrack.scrollLeft / step)));
    return { step, visible, maxStart, index };
  }
  function updateFeatureCarousel() {
    const { visible, index } = featureLayout();
    const end = Math.min(featureCards.length, index + visible);
    const pad = value => String(value).padStart(2, '0');
    featurePosition.textContent = `${pad(index + 1)}${visible > 1 ? '–' + pad(end) : ''} / ${pad(featureCards.length)}`;
    document.getElementById('feature-progress-fill').style.width = `${end / featureCards.length * 100}%`;
    featurePrev.disabled = featureTrack.scrollLeft <= 2;
    featureNext.disabled = featureTrack.scrollLeft >= featureTrack.scrollWidth - featureTrack.clientWidth - 2;
  }
  function moveFeatures(direction, edge = null) {
    const { step, visible, maxStart, index } = featureLayout();
    const start = featureTarget ?? index;
    featureTarget = edge === 'first' ? 0 : edge === 'last' ? maxStart : Math.max(0, Math.min(maxStart, start + direction * visible));
    featureTrack.scrollTo({ left: featureTarget * step, behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth' });
    clearTimeout(featureSettleTimer);
    featureSettleTimer = setTimeout(settleFeatures, 500);
  }
  function settleFeatures() {
    featureTarget = null;
    updateFeatureCarousel();
    announceFeatures();
  }
  function announceFeatures() {
    const { index, visible } = featureLayout();
    const names = featureCards.slice(index, index + visible).map(card => card.querySelector('h3').textContent).join(', ');
    const announcement = t(`표시 중: ${names}`, `Showing: ${names}`);
    if (featureAnnouncement.textContent !== announcement) featureAnnouncement.textContent = announcement;
  }
  featurePrev.addEventListener('click', () => moveFeatures(-1));
  featureNext.addEventListener('click', () => moveFeatures(1));
  featureTrack.addEventListener('keydown', event => {
    if (event.target !== featureTrack || !['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault();
    moveFeatures(event.key === 'ArrowRight' ? 1 : -1, event.key === 'Home' ? 'first' : event.key === 'End' ? 'last' : null);
  });
  featureTrack.addEventListener('scroll', () => {
    if (!featureScrollFrame) featureScrollFrame = requestAnimationFrame(() => { featureScrollFrame = 0; updateFeatureCarousel(); });
    clearTimeout(featureSettleTimer);
    featureSettleTimer = setTimeout(settleFeatures, 160);
  }, { passive: true });
  featureTrack.addEventListener('pointerdown', () => { featureTarget = null; });
  new ResizeObserver(() => { featureTarget = null; updateFeatureCarousel(); }).observe(featureTrack);

  renderFeatured();
  setTheme(readSetting('theme') || 'light', false);
  const requestedLanguage = new URL(location.href).searchParams.get('lang');
  setLanguage(requestedLanguage || readSetting('language') || 'ko', false);
  if (/Win/i.test(navigator.platform || '')) setPlatform('windows');
})();
