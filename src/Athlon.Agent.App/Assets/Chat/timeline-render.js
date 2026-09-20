var MERMAID_RENDER_DEBOUNCE_MS = 250;
var mermaidState = { promise: null, ready: false };
var mermaidSources = new WeakMap();
var mermaidSeq = 0;
var mermaidRenderTimer = null;
var mermaidRenderRoots = [];

// Markdig emits a bare <pre class="mermaid">…</pre> for a ```mermaid fence, while the user-bubble
// path builds <pre><code class="language-mermaid">…</code></pre>. Match both shapes.
var MERMAID_SELECTOR = 'pre.mermaid, pre > code.language-mermaid';

/** Diagrams carry their source in the <pre> itself (Markdig) or in a child <code>. */
function mermaidSourceOf(pre) {
  if (!pre) return '';
  var code = pre.querySelector('code');
  return (code ? code.textContent : pre.textContent) || '';
}

/** The element that holds the diagram text for a matched node. */
function mermaidPreOf(node) {
  if (!node) return null;
  return node.tagName === 'PRE' ? node : node.parentElement;
}

function mermaidThemeName() {
  return (window.__chatAssets && window.__chatAssets.theme) || 'dark';
}

function initMermaidRuntime() {
  try {
    if (typeof window.mermaid === 'undefined') return;
    window.mermaid.initialize({
      startOnLoad: false,
      securityLevel: 'strict',
      theme: mermaidThemeName()
    });
  } catch (e) { }
}

function ensureMermaidLoaded() {
  if (mermaidState.promise) return mermaidState.promise;
  mermaidState.promise = new Promise(function (resolve) {
    if (typeof window.mermaid !== 'undefined') {
      initMermaidRuntime();
      mermaidState.ready = true;
      resolve(true);
      return;
    }
    var cfg = window.__chatAssets || {};
    if (!cfg.mermaidBase) {
      resolve(false);
      return;
    }
    var script = document.createElement('script');
    script.src = cfg.mermaidBase + (cfg.mermaid || 'mermaid.min.js') + (cfg.cache || '');
    script.onload = function () {
      initMermaidRuntime();
      mermaidState.ready = true;
      resolve(true);
    };
    script.onerror = function () { resolve(false); };
    document.head.appendChild(script);
  });
  return mermaidState.promise;
}

function scheduleMermaidRender(root) {
  if (!root) return;
  mermaidRenderRoots.push(root);
  if (mermaidRenderTimer) clearTimeout(mermaidRenderTimer);
  mermaidRenderTimer = setTimeout(function () {
    mermaidRenderTimer = null;
    var roots = mermaidRenderRoots;
    mermaidRenderRoots = [];
    renderMermaidBlocks(roots);
  }, MERMAID_RENDER_DEBOUNCE_MS);
}

function renderMermaidBlocks(roots) {
  var blocks = [];
  (roots && roots.length ? roots : [document]).forEach(function (root) {
    if (!root || typeof root.querySelectorAll !== 'function') return;
    root.querySelectorAll(MERMAID_SELECTOR).forEach(function (node) {
      var pre = mermaidPreOf(node);
      if (pre && pre.dataset.mermaidDone !== '1') blocks.push(pre);
    });
  });
  if (!blocks.length) return;
  // Deduplicate: <pre class="mermaid"> also matches the bare-pre part of the selector.
  var seen = [];
  blocks = blocks.filter(function (pre) {
    if (seen.indexOf(pre) >= 0) return false;
    seen.push(pre);
    return true;
  });
  ensureMermaidLoaded().then(function (loaded) {
    if (!loaded) return;
    blocks.forEach(function (pre) {
      if (pre.isConnected && pre.dataset.mermaidDone !== '1') renderOneMermaidBlock(pre);
    });
  });
}

function renderOneMermaidBlock(pre) {
  var source = mermaidSourceOf(pre);
  if (!source.trim()) return;
  pre.dataset.mermaidDone = '1';
  mermaidRenderFigure(pre, source, null);
}

function mermaidRenderFigure(pre, source, targetFigure) {
  var figure = targetFigure || document.createElement('div');
  figure.className = 'mermaid-figure';
  figure.dataset.mermaidDone = '1';
  mermaidSources.set(figure, source);
  var id = 'mermaid-' + (++mermaidSeq);
  var settle = function (svg) {
    figure.innerHTML = svg;
    if (!targetFigure && pre && pre.parentNode) pre.parentNode.replaceChild(figure, pre);
    scrollToBottom();
  };
  var fail = function () {
    // Degrade to the raw code block; a malformed diagram must never break the timeline.
    if (pre) pre.dataset.mermaidFailed = '1';
  };
  try {
    var result = window.mermaid.render(id, source);
    if (result && typeof result.then === 'function') {
      result.then(function (r) { settle(r && r.svg ? r.svg : ''); }).catch(fail);
    } else if (result && result.svg) {
      settle(result.svg);
    } else {
      fail();
    }
  } catch (e) {
    fail();
  }
}

/** Re-render every mounted diagram after a theme switch. */
function refreshMermaidTheme() {
  if (!mermaidState.ready) return;
  initMermaidRuntime();
  document.querySelectorAll('.mermaid-figure').forEach(function (figure) {
    var source = mermaidSources.get(figure);
    if (!source) return;
    mermaidRenderFigure(null, source, figure);
  });
}

function enhanceCodeBlocks(root) {
  const scope = root || document;
  scope.querySelectorAll('.md-root pre').forEach(function (pre, index) {
    if (pre.closest('.code-block')) return;
    pendingEnhanceQueue.push({ pre: pre, index: index });
  });
  scheduleEnhanceFlush();
}

function resetTimeline() {
  // The mounted rows are being rebuilt from a replay, so a snapshot saved for this session is
  // stale by definition. Other sessions' snapshots stay untouched — they are the per-session roots
  // a later switchSession can swap back in.
  invalidateSessionSnapshot(state.currentSessionId);
  const root = document.getElementById('messages');
  if (codeObserver) codeObserver.disconnect();
  // Drop any queued code blocks from a previous timeline; their nodes are about to be
  // removed, so flushing them later would be wasted work (guarded in enhanceOneCodeBlock).
  pendingEnhanceQueue.length = 0;
  enhanceScheduled = false;
  root.innerHTML = '';
  state.entries.clear();
  state.liveTurn = 0;
  state.liveContentOrdinal = 0;
  state.currentAssistantEl = null;
  state.currentReasoningEl = null;
  state.assistantStarted = {};
  state.reasoningStarted = {};
  state.reasoningStartAt = {};
  state.reasoningFinalizedMs = {};
  state.toolCalls.clear();
  state.hasOlderMessages = false;
  state.loadingOlder = false;
  state.lastScrollHeight = undefined;
  if (mermaidRenderTimer) {
    clearTimeout(mermaidRenderTimer);
    mermaidRenderTimer = null;
  }
  mermaidRenderRoots = [];
  mermaidSources = new WeakMap();
}

function beginBatch() {
  state.batching = true;
  if (state.scrollFrame) cancelAnimationFrame(state.scrollFrame);
  state.scrollFrame = 0;
  // Keep state.scrollForcePending so a force-scroll requested before a batch (e.g. user
  // message submit or session switch) survives into the batch's first endBatch below.
  state.pendingEnhancementRoots = [];
  document.documentElement.classList.add('replaying');
}

function endBatch(forceScroll) {
  state.batching = false;
  document.documentElement.classList.remove('replaying');
  const roots = state.pendingEnhancementRoots;
  state.pendingEnhancementRoots = [];
  roots.forEach(function (root) {
    enhanceCodeBlocks(root);
    scheduleMermaidRender(root);
  });
  updateEmptyStateVisibility();
  scrollToBottom(!!forceScroll);
}

function formatReasoningSeconds(ms) {
  return t('seconds').replace('{0}', String(Math.max(1, Math.round(ms / 1000))));
}

function findReasoningRow(messageId) {
  if (state.currentReasoningEl
      && String(state.currentReasoningEl.dataset.messageId || '') === String(messageId || '')) {
    return state.currentReasoningEl;
  }
  if (!messageId) return null;
  return document.querySelector('.reasoning-row[data-message-id="' + cssEscape(messageId) + '"]');
}

function setReasoningLabelOnRow(row, text) {
  if (!row) return;
  const label = row.querySelector('.reasoning-label');
  if (label) label.textContent = text;
}

function setReasoningLabel(messageId, text) {
  setReasoningLabelOnRow(findReasoningRow(messageId), text);
}

function getReasoningElapsedMs(messageId) {
  const start = state.reasoningStartAt[messageId];
  return start ? performance.now() - start : 0;
}

function updateReasoningThinkingLabel(messageId) {
  if (!state.trackReasoningDuration) {
    setReasoningLabel(messageId, t('thinking'));
    return;
  }
  setReasoningLabel(
    messageId,
    t('thinking') + ' (' + formatReasoningSeconds(getReasoningElapsedMs(messageId)) + ')');
}

function finalizeReasoningLabel(messageId) {
  if (!messageId) return;
  const row = findReasoningRow(messageId);
  if (!row) return;
  if (!state.trackReasoningDuration) {
    setReasoningLabelOnRow(row, t('thought'));
    delete state.reasoningStartAt[messageId];
    delete state.reasoningFinalizedMs[messageId];
    return;
  }
  if (state.reasoningFinalizedMs[messageId] !== undefined) {
    return;
  }
  const ms = getReasoningElapsedMs(messageId);
  state.reasoningFinalizedMs[messageId] = ms;
  setReasoningLabelOnRow(row, t('thought') + ' (' + formatReasoningSeconds(ms) + ')');
  delete state.reasoningStartAt[messageId];
}

function openImagePreview(url, fileName) {
  var lightbox = document.getElementById('image-lightbox');
  if (!lightbox || !url) return;
  var img = lightbox.querySelector('.image-lightbox-img');
  if (img) {
    img.src = url;
    img.alt = fileName || '';
  }
  lightbox.hidden = false;
  document.body.style.overflow = 'hidden';
}

function closeImagePreview() {
  var lightbox = document.getElementById('image-lightbox');
  if (!lightbox) return;
  lightbox.hidden = true;
  var img = lightbox.querySelector('.image-lightbox-img');
  if (img) {
    img.removeAttribute('src');
    img.alt = '';
  }
  document.body.style.overflow = '';
}

function createMessageMeta(text) {
  const meta = document.createElement('div');
  meta.className = 'message-meta';
  meta.textContent = text || '';
  return meta;
}

function setMessageMeta(row, text) {
  if (!row) return;
  const stack = row.querySelector('.message-stack');
  if (!stack) return;
  let meta = stack.querySelector('.message-meta');
  if (!text) {
    if (meta && meta.parentNode) meta.parentNode.removeChild(meta);
    return;
  }
  if (!meta) {
    meta = createMessageMeta(text);
    const actions = stack.querySelector('.message-actions');
    if (actions) stack.insertBefore(meta, actions);
    else stack.appendChild(meta);
  } else {
    meta.textContent = text;
  }
}

function formatResponseDuration(durationMs) {
  if (!durationMs || durationMs <= 0) return '';
  var secondsLabel = formatReasoningSeconds(durationMs);
  return (t('responseDuration') || 'Took {0}').replace('{0}', secondsLabel);
}

function createUserRow(content, images, startedAt, mentions, messageId) {
  const row = document.createElement('div');
  row.className = 'message-row user';
  if (messageId) row.dataset.messageId = messageId;
  const stack = document.createElement('div');
  stack.className = 'message-stack';
  const bubble = document.createElement('div');
  bubble.className = 'bubble';

  if (images && images.length) {
    const gallery = document.createElement('div');
    gallery.className = 'user-images';
    images.forEach(function (image) {
      if (!image || !image.url) return;
      const thumb = document.createElement('img');
      thumb.className = 'user-image-thumb';
      thumb.src = image.url;
      thumb.alt = image.fileName || '';
      thumb.title = image.fileName || '';
      thumb.addEventListener('click', function (e) {
        e.preventDefault();
        e.stopPropagation();
        openImagePreview(image.url, image.fileName);
      });
      gallery.appendChild(thumb);
    });
    if (gallery.childNodes.length) bubble.appendChild(gallery);
  }

  if (content) {
    const text = document.createElement('div');
    text.className = 'message-content user-text';
    fillUserText(text, content, mentions);
    bubble.appendChild(text);
    // Diagrams only render once attached; during a replay batch let endBatch flush them, else
    // renderMermaidBlocks would skip the node because pre.isConnected is still false.
    if (text.querySelector(MERMAID_SELECTOR)) {
      if (state.batching) {
        state.pendingEnhancementRoots.push(text);
      } else {
        scheduleMermaidRender(text);
      }
    }
  }

  stack.appendChild(bubble);
  if (startedAt) stack.appendChild(createMessageMeta(startedAt));
  stack.appendChild(createMessageActions(row));
  row.appendChild(stack);
  updateCopyText(row, content || '');
  return row;
}

var FILE_CHIP_DOCUMENT = 'M4,2 L4,14 L12,14 L12,6 L8,6 L6,4 L4,4 Z M6,4 L6,6 L8,6';
var FILE_CHIP_FOLDER = 'M2,5 L2,14 L14,14 L14,7 L9,7 L7,5 Z';
var FILE_CHIP_GIT = 'M8,2 C5.2,2 3,4.2 3,7 C3,9.8 5.2,12 8,12 C10.8,12 13,9.8 13,7 C13,4.2 10.8,2 8,2 Z M8,4.5 L9.8,6.3 L7.5,8.6 L6.2,7.3 L8,5.5 Z';
var FILE_CHIP_MSBUILD = 'M3,12 L6,4 L9,12 M11,4 L14,12 M10.5,10 L11.5,10';
var FILE_CHIP_BADGES = {
  CSharp: 'C#',
  Project: 'P',
  Solution: 'S',
  Markdown: 'M',
  Json: '{}',
  Xml: '</>',
  Html: '<>',
  Css: '#',
  JavaScript: 'JS',
  TypeScript: 'TS',
  Python: 'Py',
  Shell: '\u25B6',
  PowerShell: '>_',
  Yaml: 'Y',
  Docker: 'D',
  Image: '\u25A3',
  Config: 'cfg'
};

function fileChipColorVar(kind) {
  var key = String(kind || 'File').toLowerCase();
  if (key === 'powershell') key = 'shell';
  return 'var(--file-icon-' + key + ', var(--file-icon-file))';
}

function createFileChipIcon(kind) {
  var wrap = document.createElement('span');
  wrap.className = 'file-chip-icon';
  wrap.setAttribute('aria-hidden', 'true');
  wrap.style.color = fileChipColorVar(kind);
  var svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 16 16');
  var path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
  var normalized = String(kind || 'File');
  if (normalized === 'Folder') {
    path.setAttribute('d', FILE_CHIP_FOLDER);
    path.setAttribute('fill', 'currentColor');
  } else if (normalized === 'Git') {
    path.setAttribute('d', FILE_CHIP_GIT);
    path.setAttribute('fill', 'currentColor');
  } else if (normalized === 'MsBuild') {
    path.setAttribute('d', FILE_CHIP_MSBUILD);
    path.setAttribute('fill', 'none');
    path.setAttribute('stroke', 'currentColor');
    path.setAttribute('stroke-width', '1.15');
    path.setAttribute('stroke-linecap', 'round');
  } else {
    path.setAttribute('d', FILE_CHIP_DOCUMENT);
    path.setAttribute('fill', 'currentColor');
    if (FILE_CHIP_BADGES[normalized]) path.setAttribute('opacity', '0.35');
    else if (normalized === 'Placeholder') path.setAttribute('opacity', '0.7');
  }
  svg.appendChild(path);
  wrap.appendChild(svg);
  var badgeText = FILE_CHIP_BADGES[normalized];
  if (badgeText) {
    var badge = document.createElement('span');
    badge.className = 'file-chip-badge' + (badgeText.length > 2 ? ' is-long' : '');
    badge.textContent = badgeText;
    wrap.appendChild(badge);
  }
  return wrap;
}

function createFileChip(mention) {
  var kind = String((mention && mention.kind) || 'file').toLowerCase();
  if (kind === 'skill' || kind === 'mcp') {
    return createTypedMentionChip(kind, mention);
  }

  var chip = document.createElement('span');
  chip.className = 'file-chip';
  var path = (mention && (mention.path || mention.fileName)) || '';
  if (path) chip.title = path;
  chip.appendChild(createFileChipIcon(mention && mention.iconKind));
  var name = document.createElement('span');
  name.className = 'file-chip-name';
  name.textContent = (mention && mention.fileName) || path;
  chip.appendChild(name);
  return chip;
}

function createTypedMentionChip(kind, mention) {
  var chip = document.createElement('span');
  chip.className = 'file-chip file-chip-' + kind;
  var path = (mention && (mention.path || mention.fileName)) || '';
  var insert = kind === 'skill' ? '//skill:' + path : '//mcp:' + path;
  chip.title = insert;
  var type = document.createElement('span');
  type.className = 'file-chip-type';
  type.textContent = kind === 'skill' ? '技能' : 'MCP';
  chip.appendChild(type);
  var name = document.createElement('span');
  name.className = 'file-chip-name';
  name.textContent = (mention && mention.fileName) || path;
  chip.appendChild(name);
  return chip;
}

// A user paste may carry a ```mermaid fence. Only the fence is special-cased: everything else
// stays literal text, and the runtime (shared with the assistant timeline) swaps the raw block
// for an SVG figure in place.
var USER_MERMAID_FENCE = /```[ \t]*mermaid[ \t]*\r?\n([\s\S]*?)```/gi;

/**
 * Splits user text into literal runs and Mermaid fences. Returns null when there is no fence,
 * so the common case keeps the plain-text fast path.
 */
function splitUserMermaidSegments(text) {
  var source = text || '';
  var segments = [];
  var cursor = 0;
  USER_MERMAID_FENCE.lastIndex = 0;
  var match = USER_MERMAID_FENCE.exec(source);
  if (!match) return null;
  while (match) {
    if (match.index > cursor) {
      segments.push({ kind: 'text', start: cursor, end: match.index });
    }
    segments.push({ kind: 'mermaid', source: (match[1] || '').trim() });
    cursor = USER_MERMAID_FENCE.lastIndex;
    match = USER_MERMAID_FENCE.exec(source);
  }
  if (cursor < source.length) {
    segments.push({ kind: 'text', start: cursor, end: source.length });
  }
  return segments;
}

/** A chart-shaped placeholder for renderMermaidBlocks(); failure leaves it as the raw fence. */
function createUserMermaidBlock(source) {
  var pre = document.createElement('pre');
  var code = document.createElement('code');
  code.className = 'language-mermaid';
  // textContent only: the diagram source is user input and must never be parsed as HTML.
  code.textContent = source;
  pre.appendChild(code);
  return pre;
}

/**
 * Renders the [segStart, segEnd) window of `text` into `el`, splicing mention chips in by their
 * absolute offsets. Mention offsets are relative to the whole message, so text segments after a
 * diagram must stay offset-correct — re-length the segment rather than rebasing the mentions.
 */
function fillUserTextRange(el, text, segStart, segEnd, mentions) {
  if (segEnd <= segStart) return;
  if (!mentions.length) {
    el.appendChild(document.createTextNode(text.slice(segStart, segEnd)));
    return;
  }

  var last = segStart;
  mentions.forEach(function (mention) {
    var start = Math.max(segStart, Math.max(0, mention.start | 0));
    var length = Math.max(0, mention.length | 0);
    var end = Math.min(segEnd, Math.max(0, mention.start | 0) + length);
    if (start < last || end <= start || start >= segEnd) return;
    if (start > last) el.appendChild(document.createTextNode(text.slice(last, start)));
    el.appendChild(createFileChip(mention));
    last = end;
  });
  if (last < segEnd) el.appendChild(document.createTextNode(text.slice(last, segEnd)));
}

function fillUserText(el, content, mentions) {
  var text = content || '';
  var items = Array.isArray(mentions)
    ? mentions.slice().sort(function (a, b) { return (a.start || 0) - (b.start || 0); })
    : [];
  var segments = splitUserMermaidSegments(text);
  if (!segments) {
    fillUserTextRange(el, text, 0, text.length, items);
    if (!el.childNodes.length && text) el.appendChild(document.createTextNode(text));
    return;
  }

  segments.forEach(function (segment) {
    if (segment.kind === 'mermaid') {
      el.appendChild(createUserMermaidBlock(segment.source));
    } else {
      fillUserTextRange(el, text, segment.start, segment.end, items);
    }
  });
  if (!el.childNodes.length && text) el.appendChild(document.createTextNode(text));
}

function createAssistantRow(messageId) {
  const row = document.createElement('div');
  row.className = 'message-row assistant assistant-row';
  row.dataset.messageId = messageId || '';
  const stack = document.createElement('div');
  stack.className = 'message-stack';
  const bubble = document.createElement('div');
  bubble.className = 'bubble';
  const content = document.createElement('div');
  content.className = 'message-content md-root';
  bubble.appendChild(content);
  stack.appendChild(bubble);
  stack.appendChild(createMessageActions(row));
  row.appendChild(stack);
  return row;
}

function createReasoningRow(messageId) {
  const row = document.createElement('div');
  row.className = 'message-row assistant reasoning-row';
  row.dataset.messageId = messageId || '';
  row.innerHTML =
    '<details class="reasoning-block" open>' +
      '<summary><span class="reasoning-chevron">›</span><span class="reasoning-label">' + t('thinking') + '</span></summary>' +
      '<div class="reasoning-content message-content"></div>' +
    '</details>';
  return row;
}

function appendMessage(role, content, append, images, startedAt, mentions, seq, messageId) {
  if (append && role === 'assistant' && state.currentAssistantEl) {
    const el = state.currentAssistantEl.querySelector('.message-content');
    el.textContent += content;
    scrollToBottom();
    return;
  }
  if (append && role === 'reasoning' && state.currentReasoningEl) {
    const el = state.currentReasoningEl.querySelector('.reasoning-content');
    el.textContent += content;
    scrollToBottom();
    return;
  }

  if (role === 'user') {
    if (seq === undefined) {
      beginLiveTurn();
      seq = seqForTurn(state.liveTurn, SEQ_USER);
    }

    var userRow = createUserRow(content, images, startedAt, mentions, messageId);
    registerEntry('msg:' + (messageId || ''), userRow, seq);
  } else if (role === 'assistant') {
    const row = createAssistantRow('');
    row.querySelector('.message-content').textContent = content;
    getMessageRoot().appendChild(row);
    state.currentAssistantEl = row;
  } else if (role === 'reasoning') {
    const row = createReasoningRow('');
    row.querySelector('.reasoning-content').textContent = content;
    getMessageRoot().appendChild(row);
    state.currentReasoningEl = row;
  }
  updateEmptyStateVisibility();
  scrollToBottom();
}

function ensureAssistantBubble(messageId) {
  const existing = getEntryRow('msg:' + messageId);
  if (existing) {
    state.currentAssistantEl = existing;
    state.assistantStarted[messageId] = true;
    return;
  }
  if (state.currentAssistantEl && state.assistantStarted[messageId]) return;
  const row = createAssistantRow(messageId);
  registerEntry('msg:' + messageId, row, nextLiveContentSeq());
  state.currentAssistantEl = row;
  state.assistantStarted[messageId] = true;
  updateEmptyStateVisibility();
}

function createToolCard(toolCallId, toolName, seq) {
  state.currentAssistantEl = null;
  state.currentReasoningEl = null;
  const row = document.createElement('div');
  row.className = 'message-row assistant tool-row';
  const details = document.createElement('details');
  details.className = 'message tool';
  details.dataset.toolCallId = toolCallId;
  details.innerHTML =
    '<summary><span>' + escapeHtml(toolName || 'unknown') + '</span>' +
    '<span class="tool-status running">running</span></summary>' +
    '<div class="tool-body">' +
    '<div class="tool-section-label">arguments</div>' +
    '<pre class="tool-pre tool-args"></pre>' +
    '<div class="tool-result" style="display:none">' +
    '<div class="tool-section-label">result</div>' +
    '<div class="tool-result-html md-root"></div>' +
    '</div></div>';
  row.appendChild(details);
  registerEntry('tool:' + toolCallId, row, seq === undefined ? nextLiveContentSeq() : seq);
  state.toolCalls.set(toolCallId, details);
  updateEmptyStateVisibility();
  scrollToBottom();
  return details;
}

/** Existing tool card element for a call id, in the live map or already in the DOM. */
function getToolCard(toolCallId) {
  var registered = getEntryRow('tool:' + toolCallId);
  if (registered) {
    var details = registered.querySelector('.message.tool');
    if (details) return details;
  }
  return state.toolCalls.get(toolCallId) || document.querySelector('[data-tool-call-id="' + toolCallId + '"]');
}

function applyToolStatusBadge(badge, status) {
  if (!badge) return;
  const normalized = (status || 'succeeded').toLowerCase();
  if (normalized === 'awaiting_approval') {
    badge.textContent = t('approvalPending');
    badge.className = 'tool-status running';
    return;
  }
  if (normalized === 'approval_denied') {
    badge.textContent = t('deniedStatus');
    badge.className = 'tool-status failed';
    return;
  }
  const cssClass = normalized === 'succeeded' || normalized === 'success'
    ? 'success'
    : normalized === 'failed' || normalized === 'failure'
      ? 'failed'
      : normalized === 'cancelled' || normalized === 'canceled'
        ? 'cancelled'
        : normalized === 'running'
          ? 'running'
          : normalized === 'preparing'
            ? 'running'
            : 'success';
  const label = cssClass === 'success'
    ? 'success'
    : cssClass === 'failed'
      ? 'failed'
      : cssClass === 'cancelled'
        ? 'cancelled'
        : cssClass === 'running'
          ? 'running'
          : normalized;
  badge.textContent = label;
  badge.className = 'tool-status ' + cssClass;
}

