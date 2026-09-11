const state = {
  currentAssistantEl: null,
  currentReasoningEl: null,
  assistantStarted: {},
  reasoningStarted: {},
  toolCalls: new Map(),
  trackReasoningDuration: true,
  reasoningStartAt: {},
  reasoningFinalizedMs: {},
  batching: false,
  pendingEnhancementRoots: [],
  scrollFrame: 0,
  scrollForcePending: false,
  autoScrollEnabled: true,
  batchTarget: null,
  hasOlderMessages: false,
  loadingOlder: false,
  lastScrollHeight: undefined,
  // Timeline entries keyed by their stable id (messageId / toolCallId / turn anchor). Each record
  // remembers the seq it was placed at so an update rewrites content in place instead of moving
  // the bubble. This map — not the DOM — is the source of truth for what the timeline contains.
  entries: new Map(),
  // Live ordering cursor, mirroring C# TimelineOrderPolicy for events that arrive without an
  // explicit seq. Replay always sends seq, so this only drives the live stream.
  liveTurn: 0,
  liveTurnStarted: false,
  liveContentOrdinal: 0
};

function t(key) {
  return (window.__chatI18n && window.__chatI18n[key]) || key;
}

function applyChatI18n() {
  document.querySelectorAll('.code-btn').forEach(function (btn) {
    if (btn.classList.contains('copied')) return;
    if (btn.dataset.i18n === 'preview') {
      btn.textContent = t('preview');
      return;
    }
    btn.textContent = t('copy');
  });
  document.querySelectorAll('[data-i18n]').forEach(function (element) {
    element.textContent = t(element.dataset.i18n);
  });
  document.querySelectorAll('.reasoning-label').forEach(function (label) {
    const row = label.closest('.reasoning-row');
    const messageId = row && row.dataset.messageId;
    if (messageId && state.reasoningFinalizedMs[messageId] !== undefined) {
      finalizeReasoningLabel(messageId);
    } else if (messageId && state.reasoningStartAt[messageId]) {
      updateReasoningThinkingLabel(messageId);
    } else if (!label.textContent || label.textContent.indexOf('思考') >= 0 || label.textContent.indexOf('Think') >= 0) {
      label.textContent = t('thinking');
    }
  });
}

function cssEscape(value) {
  if (window.CSS && typeof CSS.escape === 'function') return CSS.escape(String(value));
  return String(value).replace(/\\/g, '\\\\').replace(/"/g, '\\"');
}

function decodeBase64Utf8(b64) {
  const binary = atob(b64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return new TextDecoder('utf-8').decode(bytes);
}

function resolveEventMarkdown(event) {
  if (event && event.markdownB64) return decodeBase64Utf8(event.markdownB64);
  if (event && event.markdown) return event.markdown;
  if (event && event.content) return event.content;
  return '';
}

function resolveEventHtml(event) {
  if (event && event.htmlB64) return decodeBase64Utf8(event.htmlB64);
  return (event && event.html) || '';
}

function resolveRenderedHtml(event, fallbackText) {
  const html = resolveEventHtml(event);
  if (html) return html;
  return '<pre>' + escapeHtml(resolveEventMarkdown(event) || fallbackText || '') + '</pre>';
}

function escapeHtml(text) {
  const div = document.createElement('div');
  div.textContent = text == null ? '' : String(text);
  return div.innerHTML;
}

function post(payload) {
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.postMessage(payload);
  }
}

var pendingToolDetailRequests = Object.create(null);
var toolDetailRequestSeq = 0;

// Streaming scroll coalescing: only write scrollTop during a pinned auto-scroll when the
// content grew by at least SCROLL_MERGE_THRESHOLD px. Sub-threshold growth (per-token
// height changes) is skipped to avoid forcing layout on every token.
var SCROLL_MERGE_THRESHOLD = 24;

// Code-highlight chunking: processing every `<pre>` at once during an endBatch can block
// the main thread on long conversations. We enqueue pre nodes and flush them in small
// slices via requestIdleCallback (falling back to setTimeout(0)), keeping the final
// highlighting result identical while spreading the work across idle frames.
var CODE_ENHANCE_BATCH_SIZE = 24;
var pendingEnhanceQueue = [];
var enhanceScheduled = false;

function requestToolDetailForEntry(entry, detailPanel) {
  if (!entry || entry.dataset.hydrated === '1' || entry.dataset.loading === '1') return;
  var messageId = entry.dataset.messageId || '';
  var toolCallId = entry.dataset.toolCallId || '';
  if (!messageId && !toolCallId) return;
  entry.dataset.loading = '1';
  if (detailPanel) detailPanel.textContent = '…';
  var requestId = 'td-' + (++toolDetailRequestSeq);
  pendingToolDetailRequests[requestId] = { entry: entry, panel: detailPanel };
  post({
    type: 'requestToolDetail',
    requestId: requestId,
    messageId: messageId || null,
    toolCallId: toolCallId || null
  });
}

function applyToolDetailPayload(payload) {
  if (!payload) return;
  var requestId = payload.requestId || '';
  var pending = requestId ? pendingToolDetailRequests[requestId] : null;
  var text = payload.content || '';
  if (pending) {
    delete pendingToolDetailRequests[requestId];
    if (pending.panel) {
      pending.panel.textContent = text || '(empty)';
    } else if (pending.entry && pending.entry.classList.contains('tool')) {
      var result = pending.entry.querySelector('.tool-result');
      var html = pending.entry.querySelector('.tool-result-html');
      if (result && html) {
        result.style.display = 'block';
        html.textContent = text;
      }
    }
    if (pending.entry) {
      pending.entry.dataset.hydrated = '1';
      delete pending.entry.dataset.loading;
    }
    return;
  }

  var toolCallId = payload.toolCallId || '';
  if (toolCallId) {
    var card = getToolCard(toolCallId);
    if (card) {
      var cardResult = card.querySelector('.tool-result');
      var cardHtml = card.querySelector('.tool-result-html');
      if (cardResult && cardHtml) {
        cardResult.style.display = 'block';
        cardHtml.textContent = text;
      }
      card.dataset.hydrated = '1';
      delete card.dataset.loading;
      if (payload.messageId) card.dataset.messageId = payload.messageId;
    }
  }

  if (payload.messageId) {
    var entries = document.querySelectorAll(
      '.turn-activity-item[data-message-id="' + payload.messageId + '"]');
    entries.forEach(function (entry) {
      var panel = entry.querySelector('.turn-activity-tool-detail');
      if (panel) panel.textContent = text || '(empty)';
      entry.dataset.hydrated = '1';
      delete entry.dataset.loading;
    });
  }
}

function requestToolDetailForToolCard(card) {
  if (!card || card.dataset.hydrated === '1' || card.dataset.loading === '1') return;
  var messageId = card.dataset.messageId || '';
  var toolCallId = card.getAttribute('data-tool-call-id') || '';
  if (!messageId && !toolCallId) return;
  card.dataset.loading = '1';
  var requestId = 'td-' + (++toolDetailRequestSeq);
  pendingToolDetailRequests[requestId] = { entry: card, panel: null };
  post({
    type: 'requestToolDetail',
    requestId: requestId,
    messageId: messageId || null,
    toolCallId: toolCallId || null
  });
}

const copyIconSvg =
  '<svg width="16" height="16" viewBox="0 0 16 16" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">' +
    '<rect x="5" y="5" width="9" height="9" rx="1.5" stroke="currentColor" stroke-width="1.25"></rect>' +
    '<rect x="2" y="2" width="9" height="9" rx="1.5" stroke="currentColor" stroke-width="1.25" fill="var(--chat-bg)"></rect>' +
  '</svg>';

function createCopyButton(onCopy) {
  const btn = document.createElement('button');
  btn.type = 'button';
  btn.className = 'message-action-btn';
  btn.setAttribute('aria-label', t('copy'));
  btn.innerHTML = copyIconSvg;
  btn.addEventListener('click', function (e) {
    e.preventDefault();
    e.stopPropagation();
    onCopy(btn);
  });
  return btn;
}

function copyMessageText(text, button) {
  if (!text) return;
  post({ type: 'copy', text: text });
  if (!button) return;
  button.classList.add('copied');
  button.setAttribute('aria-label', t('copied'));
  setTimeout(function () {
    button.classList.remove('copied');
    button.setAttribute('aria-label', t('copy'));
  }, 1600);
}

function resolveRowCopyText(row) {
  if (!row) return '';
  if (row.dataset.copyText) return row.dataset.copyText;
  const userText = row.querySelector('.user-text');
  if (userText) return userText.textContent || '';
  const content = row.querySelector('.message-content');
  return content ? (content.innerText || '') : '';
}

function updateCopyText(row, text) {
  if (!row) return;
  row.dataset.copyText = text == null ? '' : String(text);
}

function createMessageActions(row) {
  const actions = document.createElement('div');
  actions.className = 'message-actions';
  actions.appendChild(createCopyButton(function (button) {
    copyMessageText(resolveRowCopyText(row), button);
  }));
  return actions;
}

function getChatScroller() {
  return document.getElementById('chat-scroll');
}

function getMessageRoot() {
  return state.batchTarget || document.getElementById('messages');
}

/**
 * Mirror of C# TimelineOrderPolicy. Both sides compute the same number for the same entry, so a
 * live bubble and its replayed twin land in the identical slot. Keep these constants in sync with
 * src/Athlon.Agent.App/Services/TimelineOrderPolicy.cs.
 */
var SEQ_TURN_BAND = 1000000;
var SEQ_USER = 0;
var SEQ_ACTIVITY = 1000;
var SEQ_CONTENT = 2000;
var SEQ_FILES = 800000;
var SEQ_COMPACTION = 900000;
/**
 * Gap left for an older page when it is prepended. Older pages are shifted below the current
 * minimum by this much (plus their own span), which is far wider than any real turn band.
 */
var SEQ_PAGE_GAP = 1000000;

function seqForTurn(turnIndex, offset) {
  return Math.max(0, turnIndex) * SEQ_TURN_BAND + offset;
}

/**
 * Advances the live cursor to a new turn. Called when a user message opens a turn; replay never
 * calls this because its events already carry explicit seqs.
 */
function beginLiveTurn() {
  if (state.liveTurnStarted) state.liveTurn += 1;
  state.liveTurnStarted = true;
  state.liveContentOrdinal = 0;
  return state.liveTurn;
}

/**
 * Re-anchors the live cursor after a replay so live events that follow continue the numbering the
 * replay used. Without this, a mid-turn reload would place the next live card in the wrong turn
 * band and stack a duplicate beside the replayed one.
 */
function syncLiveCursorFromSeq(maxSeq) {
  if (typeof maxSeq !== 'number' || !isFinite(maxSeq) || maxSeq < 0) return;
  state.liveTurn = Math.floor(maxSeq / SEQ_TURN_BAND);
  state.liveTurnStarted = true;
  var withinContent = maxSeq - seqForTurn(state.liveTurn, SEQ_CONTENT);
  state.liveContentOrdinal = withinContent >= 0 ? withinContent + 1 : 0;
}

/** Next content slot (tool card / assistant reply) inside the current live turn. */
function nextLiveContentSeq() {
  return seqForTurn(state.liveTurn, SEQ_CONTENT + (state.liveContentOrdinal++));
}

/**
 * Places (or moves) a row so that #messages is always ascending by data-seq. This is the only
 * positioning decision in the timeline: no "insert after the last user row", no scanning for a
 * previous card to adopt. A missing seq keeps the node appended, which is what the tests and the
 * shell's inline replay rely on when they feed events without ordering metadata.
 */
function insertBySeq(row, seq) {
  var root = getMessageRoot();
  if (!root || !row) return;
  if (seq === undefined || seq === null || isNaN(seq)) {
    if (row.parentNode !== root) root.appendChild(row);
    return;
  }

  row.setAttribute('data-seq', String(seq));
  var inRoot = row.parentNode === root;

  // Fast path: a new node whose seq is at or past the last child appends in O(1). Replay and the
  // live stream both emit ascending seqs, so this is the hot path and keeps a full replay linear.
  var last = root.lastElementChild;
  if (!inRoot && (!last || Number(last.getAttribute('data-seq')) <= seq)) {
    root.appendChild(row);
    return;
  }

  // Fast path: already in place relative to its immediate neighbours.
  var previous = row.previousElementSibling;
  var next = row.nextElementSibling;
  if (inRoot
      && (!previous || Number(previous.getAttribute('data-seq')) <= seq)
      && (!next || Number(next.getAttribute('data-seq')) >= seq)) {
    return;
  }

  var children = root.children;
  var anchor = null;
  var found = false;
  for (var i = children.length - 1; i >= 0; i--) {
    var candidate = children[i];
    if (candidate === row) continue;
    var candidateSeq = Number(candidate.getAttribute('data-seq'));
    if (isNaN(candidateSeq) || candidateSeq <= seq) {
      anchor = candidate.nextElementSibling;
      found = true;
      break;
    }
  }

  // Every existing child sorts after this one: it belongs at the front.
  if (!found) anchor = children.length > 0 ? children[0] : null;

  if (anchor === row) return;
  root.insertBefore(row, anchor);
}

/**
 * Registers a timeline entry and inserts it at its seq. Returns the existing element when the key
 * is already present so callers can update content in place without reordering the timeline.
 */
function registerEntry(key, row, seq) {
  var normalized = key || '';
  var existing = normalized ? state.entries.get(normalized) : null;
  if (existing && existing.el && existing.el !== row) {
    return existing;
  }

  if (normalized) {
    state.entries.set(normalized, { el: row, seq: seq });
  }

  insertBySeq(row, seq);
  return null;
}

/** The row currently registered for a key, if any. */
function getEntryRow(key) {
  if (!key) return null;
  var record = state.entries.get(key);
  return record ? record.el : null;
}

/** Removes a keyed entry and its row from the timeline. */
function removeEntry(key) {
  if (!key) return false;
  var record = state.entries.get(key);
  if (!record) return false;
  state.entries.delete(key);
  if (record.el && record.el.parentNode) record.el.parentNode.removeChild(record.el);
  return true;
}

/**
 * Resolves the seq an event should be placed at. Replay and the C# live dispatcher always send an
 * explicit seq; a missing seq falls back to the JSON key order the shell test fixtures use.
 */
function resolveEventSeq(event, fallbackOffset) {
  if (event && typeof event.seq === 'number' && isFinite(event.seq)) return event.seq;
  if (fallbackOffset === undefined) return undefined;
  return seqForTurn(state.liveTurn, fallbackOffset);
}

function isNearBottom() {
  const scroller = getChatScroller();
  return !scroller || scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight <= 80;
}

function hasActiveSelection() {
  const selection = window.getSelection && window.getSelection();
  return !!selection && !selection.isCollapsed && String(selection).length > 0;
}

function scrollToBottom(force) {
  // Keep the force intention across batches: a later non-force call must not drop it,
  // and a force call must win past autoScrollEnabled / active selection.
  if (!force && !state.scrollForcePending && (!state.autoScrollEnabled || hasActiveSelection())) return;
  if (force) state.scrollForcePending = true;
  if (state.batching || state.scrollFrame) return;
  state.scrollFrame = requestAnimationFrame(function () {
    state.scrollFrame = 0;
    const shouldForce = state.scrollForcePending;
    state.scrollForcePending = false;
    const scroller = getChatScroller();
    if (state.batching) {
      // Batch in progress: preserve the force intention for the next endBatch.
      if (shouldForce) state.scrollForcePending = true;
      return;
    }
    if (!scroller
        || (!shouldForce && (!state.autoScrollEnabled || hasActiveSelection()))) {
      state.lastScrollHeight = undefined;
      return;
    }
    const newHeight = scroller.scrollHeight;
    // During streaming the height grows by a little each token. Reading scrollHeight
    // forces a layout; guard that we only write scrollTop when the content actually
    // grew by more than a small threshold while already pinned to the bottom. This
    // avoids re-laying-out the whole timeline on every streamed token. On failure the
    // original semantics (always snap to bottom) are preserved.
    if (!shouldForce && state.lastScrollHeight !== undefined) {
      const grew = newHeight - state.lastScrollHeight;
      if (grew > 0 && grew < SCROLL_MERGE_THRESHOLD && isNearBottom()) {
        state.lastScrollHeight = newHeight;
        return;
      }
    }
    scroller.scrollTop = newHeight;
    state.lastScrollHeight = newHeight;
  });
}

function updateEmptyStateVisibility() {
  if (state.batching) return;
  const emptyState = document.getElementById('empty-state');
  const root = document.getElementById('messages');
  if (!emptyState || !root) return;
  emptyState.style.display = root.children.length === 0 ? 'flex' : 'none';
}

function findAssistantBubbleRow(messageId) {
  if (!messageId) return null;
  const selector = '.message-row.assistant-row[data-message-id="' + cssEscape(messageId) + '"]';
  // Prefer the active batch root (DocumentFragment during prepend/append) — nodes there
  // are not queryable via document until attached.
  const root = getMessageRoot();
  if (root && root.querySelector) {
    const inRoot = root.querySelector(selector);
    if (inRoot) return inRoot;
  }
  return document.querySelector(selector);
}

function applyMarkdownHtml(node, html, enhance) {
  if (!node) return;
  node.classList.add('md-root');
  node.innerHTML = html || '';
  if (enhance === false) return;
  if (state.batching) {
    state.pendingEnhancementRoots.push(node);
  } else {
    enhanceCodeBlocks(node);
    scheduleMermaidRender(node);
  }
}

function applyAssistantHtml(messageId, html, createIfMissing, streaming, responseDurationMs, seq) {
  let row = findAssistantBubbleRow(messageId);
  if (!row && createIfMissing) {
    row = createAssistantRow(messageId);
    // A live assistant bubble claims the next content slot; a replayed one carries its seq so it
    // lands in the same place the streamed bubble occupied.
    registerEntry('msg:' + messageId, row, seq === undefined ? nextLiveContentSeq() : seq);
    state.assistantStarted[messageId] = true;
    state.currentAssistantEl = row;
  } else if (row && seq !== undefined) {
    insertBySeq(row, seq);
  }
  if (!row) return;
  // Query content on the row itself — do not re-query document (breaks DocumentFragment batches).
  applyMarkdownHtml(row.querySelector('.bubble > .message-content'), html, streaming !== true);
  if (streaming !== true) {
    // The files-changed card take its own seq slot, so no reordering is needed here.
    setMessageMeta(row, formatResponseDuration(responseDurationMs));
  }
  updateEmptyStateVisibility();
  scrollToBottom();
}

const codeObserver = typeof IntersectionObserver === 'function'
  ? new IntersectionObserver(function (entries, observer) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) return;
        const code = entry.target;
        observer.unobserve(code);
        if (typeof hljs !== 'undefined' && !code.dataset.hljsDone) {
          try {
            hljs.highlightElement(code);
            code.dataset.hljsDone = '1';
          } catch (e) {}
        }
      });
    }, { root: document.getElementById('chat-scroll'), rootMargin: '200px 0px' })
  : null;

function enhanceOneCodeBlock(pre, index) {
  // Guard against stale nodes: a resetTimeline() between enqueue and flush detaches the
  // pre from the DOM, and a pre already wrapped in a .code-block must never be re-wrapped.
  if (!pre.isConnected || pre.closest('.code-block')) return;
  const code = pre.querySelector('code');
  if (!code) return;

  const raw = code.textContent || '';
  const className = code.className || '';
  const match = className.match(/language-([\w#+-]+)/i);
  const language = match ? match[1] : t('code');

  if (String(language).toLowerCase() === 'mermaid') {
    // Diagrams render through renderMermaidBlocks(); a code-block header would fight the SVG.
    return;
  }

  const wrapper = document.createElement('div');
  wrapper.className = 'code-block';

  const header = document.createElement('div');
  header.className = 'code-block-header';

  const label = document.createElement('span');
  label.textContent = language;

  const actions = document.createElement('div');
  actions.className = 'code-block-actions';

  const langKey = (match ? match[1] : '').toLowerCase();
  if (langKey === 'html' || langKey === 'htm') {
    const previewBtn = document.createElement('button');
    previewBtn.type = 'button';
    previewBtn.className = 'code-btn';
    previewBtn.dataset.i18n = 'preview';
    previewBtn.textContent = t('preview');
    previewBtn.addEventListener('click', function () {
      post({ type: 'preview', html: raw });
    });
    actions.appendChild(previewBtn);
  }

  const copyBtn = document.createElement('button');
  copyBtn.type = 'button';
  copyBtn.className = 'code-btn';
  copyBtn.textContent = t('copy');
  copyBtn.addEventListener('click', function () {
    post({ type: 'copy', text: raw, blockId: String(index) });
    copyBtn.textContent = t('copied');
    copyBtn.classList.add('copied');
    setTimeout(function () {
      copyBtn.textContent = t('copy');
      copyBtn.classList.remove('copied');
    }, 1600);
  });
  actions.appendChild(copyBtn);

  header.appendChild(label);
  header.appendChild(actions);

  pre.parentNode.insertBefore(wrapper, pre);
  wrapper.appendChild(header);
  wrapper.appendChild(pre);
  if (codeObserver) {
    codeObserver.observe(code);
  } else if (typeof hljs !== 'undefined' && !code.dataset.hljsDone) {
    try {
      hljs.highlightElement(code);
      code.dataset.hljsDone = '1';
    } catch (e) {}
  }
}

function flushEnhanceQueue() {
  enhanceScheduled = false;
  const slice = pendingEnhanceQueue.splice(0, CODE_ENHANCE_BATCH_SIZE);
  for (let i = 0; i < slice.length; i++) {
    enhanceOneCodeBlock(slice[i].pre, slice[i].index);
  }
  if (pendingEnhanceQueue.length > 0) {
    scheduleEnhanceFlush();
  }
}

function scheduleEnhanceFlush() {
  if (enhanceScheduled) return;
  enhanceScheduled = true;
  if (typeof requestIdleCallback === 'function') {
    requestIdleCallback(function () { flushEnhanceQueue(); }, { timeout: 400 });
  } else {
    setTimeout(function () { flushEnhanceQueue(); }, 0);
  }
}

// --- Mermaid diagrams -------------------------------------------------------
// The bundled runtime (~2.5 MB) is lazy-loaded the first time a ```mermaid block appears.
var MERMAID_RENDER_DEBOUNCE_MS = 250;
var mermaidState = { promise: null, ready: false };
var mermaidSources = new WeakMap();
var mermaidSeq = 0;
var mermaidRenderTimer = null;
var mermaidRenderRoots = [];

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
    root.querySelectorAll('pre > code.language-mermaid').forEach(function (code) {
      var pre = code.parentElement;
      if (pre && pre.dataset.mermaidDone !== '1') blocks.push(pre);
    });
  });
  if (!blocks.length) return;
  ensureMermaidLoaded().then(function (loaded) {
    if (!loaded) return;
    blocks.forEach(function (pre) {
      if (pre.isConnected && pre.dataset.mermaidDone !== '1') renderOneMermaidBlock(pre);
    });
  });
}

function renderOneMermaidBlock(pre) {
  var code = pre.querySelector('code');
  var source = code ? code.textContent || '' : '';
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

function fillUserText(el, content, mentions) {
  var text = content || '';
  var items = Array.isArray(mentions)
    ? mentions.slice().sort(function (a, b) { return (a.start || 0) - (b.start || 0); })
    : [];
  if (!items.length) {
    el.textContent = text;
    return;
  }

  var last = 0;
  items.forEach(function (mention) {
    var start = Math.max(0, mention.start | 0);
    var length = Math.max(0, mention.length | 0);
    if (start < last || length <= 0 || start >= text.length) return;
    if (start > last) el.appendChild(document.createTextNode(text.slice(last, start)));
    el.appendChild(createFileChip(mention));
    last = Math.min(text.length, start + length);
  });
  if (last < text.length) el.appendChild(document.createTextNode(text.slice(last)));
  if (!el.childNodes.length) el.textContent = text;
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

function ensureToolApprovalPanel(card, event) {
  const body = card.querySelector('.tool-body');
  if (!body) return null;

  let panel = body.querySelector('.tool-approval');
  if (panel) return panel;

  panel = document.createElement('div');
  panel.className = 'tool-approval';
  body.prepend(panel);

  const title = document.createElement('div');
  title.className = 'tool-approval-title';
  title.dataset.i18n = 'approvalTitle';
  title.textContent = t('approvalTitle');
  panel.appendChild(title);

  const description = document.createElement('div');
  description.className = 'tool-approval-description';
  description.dataset.i18n = 'approvalDescription';
  description.textContent = t('approvalDescription');
  panel.appendChild(description);

  const argumentsPre = document.createElement('pre');
  argumentsPre.className = 'tool-pre tool-approval-arguments';
  panel.appendChild(argumentsPre);

  const actions = document.createElement('div');
  actions.className = 'tool-approval-actions';
  const deny = document.createElement('button');
  deny.type = 'button';
  deny.className = 'tool-approval-button deny';
  deny.dataset.i18n = 'deny';
  deny.textContent = t('deny');
  const approve = document.createElement('button');
  approve.type = 'button';
  approve.className = 'tool-approval-button approve';
  approve.dataset.i18n = 'approve';
  approve.textContent = t('approve');

  function submit(approved) {
    deny.disabled = true;
    approve.disabled = true;
    post({ type: 'toolApproval', toolCallId: event.toolCallId, approved: approved });
  }
  deny.addEventListener('click', function () { submit(false); });
  approve.addEventListener('click', function () { submit(true); });
  actions.appendChild(deny);
  actions.appendChild(approve);
  panel.appendChild(actions);
  return panel;
}

function showToolApproval(event) {
  let card = getToolCard(event.toolCallId);
  if (!card) {
    createToolCard(event.toolCallId, event.toolName);
    card = getToolCard(event.toolCallId);
  }
  if (!card) return;

  card.open = true;
  card.dataset.awaitingApproval = 'true';
  const badge = card.querySelector('.tool-status');
  if (badge) {
    badge.textContent = t('approvalPending');
    badge.className = 'tool-status running';
  }

  const panel = ensureToolApprovalPanel(card, event);
  const argumentsPre = panel && panel.querySelector('.tool-approval-arguments');
  if (argumentsPre) argumentsPre.textContent = event.arguments || '';
  const argsPre = card.querySelector('.tool-args');
  if (argsPre && event.arguments) argsPre.textContent = event.arguments;
  scrollToBottom(true);
}

function resolveToolApproval(event) {
  const card = getToolCard(event.toolCallId);
  const panel = card && card.querySelector('.tool-approval');
  if (!card || !panel) return;

  delete card.dataset.awaitingApproval;
  const badge = card.querySelector('.tool-status');
  if (badge) {
    badge.textContent = t(event.approved ? 'allowedStatus' : 'deniedStatus');
    badge.className = 'tool-status ' + (event.approved ? 'success' : 'failed');
  }
  const actions = panel.querySelector('.tool-approval-actions');
  if (actions) actions.remove();
  let result = panel.querySelector('.tool-approval-result');
  if (!result) {
    result = document.createElement('div');
    result.className = 'tool-approval-result';
    panel.appendChild(result);
  }
  const decisionKey = event.approved ? 'approved' : 'denied';
  result.dataset.i18n = decisionKey;
  result.textContent = t(decisionKey);
  result.className = 'tool-approval-result ' + decisionKey;
}

function renderDiffLines(lines) {
  if (!lines || !lines.length) {
    return '<div class="diff-empty">' + escapeHtml(t('noDiffAvailable')) + '</div>';
  }
  return lines.map(function (line) {
    var kind = (line.kind || '').toLowerCase();
    if (kind === 'collapsed') {
      var label = (t('unmodifiedLines') || '{0} unmodified lines').replace('{0}', String(line.count || 0));
      return '<div class="diff-line collapsed">' + escapeHtml(label) + '</div>';
    }
    var css = kind === 'added' ? 'add'
      : kind === 'removed' ? 'del'
      : kind === 'hunkheader' ? 'hunk'
      : kind === 'header' ? 'header'
      : 'ctx';
    var prefix = kind === 'added' ? '+'
      : kind === 'removed' ? '-'
      : kind === 'hunkheader' || kind === 'header' ? ''
      : ' ';
    return '<div class="diff-line ' + css + '">' +
      '<span class="diff-line-prefix">' + escapeHtml(prefix) + '</span>' +
      '<span class="diff-line-text">' + escapeHtml(line.text || '') + '</span></div>';
  }).join('');
}

function filesChangedTitle(count) {
  if (count === 1) return t('filesChangedOne') || '1 File Changed';
  return (t('filesChangedMany') || '{0} Files Changed').replace('{0}', String(count));
}

/**
 * Title for a single-edit card: the edited file's name is the headline, so each edit reads as one
 * line on the timeline instead of a count.
 */
function filesChangedTitleFor(files) {
  if (files && files.length === 1) {
    var first = files[0];
    return first.displayName || first.path || filesChangedTitle(1);
  }
  return filesChangedTitle(files ? files.length : 0);
}

function joinSummaryParts(parts) {
  if (!parts.length) return '';
  if (parts.length === 1) return parts[0];
  if (parts.length === 2) return parts[0] + ', ' + parts[1];
  return parts.slice(0, -1).join(', ') + ', ' + parts[parts.length - 1];
}

function turnActivitySummaryText(event) {
  var parts = [];
  var explored = event.exploredFileCount || 0;
  var searches = event.searchCount || 0;
  var commands = event.commandCount || 0;
  var thoughts = event.thoughtCount || 0;
  if (explored === 1) parts.push(t('exploredFilesOne') || 'explored 1 file');
  else if (explored > 1) parts.push((t('exploredFilesMany') || 'explored {0} files').replace('{0}', String(explored)));
  if (searches === 1) parts.push(t('searchesOne') || '1 search');
  else if (searches > 1) parts.push((t('searchesMany') || '{0} searches').replace('{0}', String(searches)));
  if (commands === 1) parts.push(t('commandsOne') || 'ran 1 command');
  else if (commands > 1) parts.push((t('commandsMany') || 'ran {0} commands').replace('{0}', String(commands)));
  var joined = joinSummaryParts(parts);
  if (joined) return joined;
  if (thoughts === 1) return t('thoughtsOne') || 'Thought';
  if (thoughts > 1) return (t('thoughtsMany') || '{0} thoughts').replace('{0}', String(thoughts));
  // In-flight tools often have items before counts are finalized — never fall back to "已思考".
  var items = event.items || [];
  if (items.length) {
    var first = items[0] || {};
    var line = ((first.verb || '') + ' ' + (first.detail || first.path || '')).trim();
    if (line) {
      if (items.length === 1) return line;
      return line.replace(/…+\s*$/u, '') + '…';
    }
  }
  return t('thinking') || 'Working…';
}

/**
 * Stable key for the turn-activity fold. Prefers the C#-supplied entryId (which is anchored to the
 * turn's user message) so a live fold and its replayed twin share one key. Falls back to the live
 * turn index for events that predate the entryId field.
 */
function turnActivityEntryKey(event) {
  if (event && event.entryId) return event.entryId;
  return 'activity:' + state.liveTurn;
}

/**
 * Stable key for the files-changed card. Same anchoring rule as the activity fold.
 */
function filesChangedEntryKey(event) {
  if (event && event.entryId) return event.entryId;
  return 'files:' + state.liveTurn;
}

/**
 * Renders (or refreshes) one file-edit card in the timeline.
 *
 * Every file edit is its own card, keyed by the tool call id C# derives from the transcript, so a
 * live publish and its replayed twin are the same entry and overwrite in place instead of stacking.
 * Replay carries the edit's own content seq; a live card claims the next content slot so it lands
 * where the edit happened rather than being deferred to the end of the turn.
 */
function appendFilesChangedCard(event) {
  state.currentAssistantEl = null;
  state.currentReasoningEl = null;
  var files = event.files || [];
  var key = filesChangedEntryKey(event);
  var existingRow = getEntryRow(key);

  // Empty payload with no card yet: nothing to show.
  if (!files.length && !existingRow) return;

  var hasExplicitSeq = event && typeof event.seq === 'number' && isFinite(event.seq);
  var openPaths = {};
  var row = existingRow;
  var card;
  if (row) {
    card = row.querySelector('.files-changed-card');
    if (!card) {
      row = null;
    } else {
      card.querySelectorAll('.files-changed-item.open').forEach(function (item) {
        var path = item.getAttribute('data-path') || '';
        if (path) openPaths[path] = true;
      });
      card.innerHTML = '';
    }
  }

  if (!row) {
    row = document.createElement('div');
    row.className = 'message-row assistant files-changed-host';
    card = document.createElement('div');
    card.className = 'files-changed-card';
    row.appendChild(card);
    // A live card claims the next content slot; a replayed card carries its edit's own seq so it
    // lands exactly where the edit happened in the turn.
    registerEntry(key, row, hasExplicitSeq ? event.seq : nextLiveContentSeq());
  } else if (hasExplicitSeq) {
    insertBySeq(row, event.seq);
  }

  if (!files.length) {
    // Sealed-and-empty: the card stays as the turn's marker.
    updateEmptyStateVisibility();
    return;
  }

  card.setAttribute('data-turn-key', key);

  var title = document.createElement('div');
  title.className = 'files-changed-title';
  title.textContent = filesChangedTitleFor(files);
  card.appendChild(title);

  var list = document.createElement('div');
  list.className = 'files-changed-list';

  files.forEach(function (file) {
    var item = document.createElement('div');
    item.className = 'files-changed-item';
    item.setAttribute('data-path', file.path || '');
    if (openPaths[file.path || '']) item.classList.add('open');

    var button = document.createElement('button');
    button.type = 'button';
    button.className = 'files-changed-row';
    button.title = file.path || file.displayName || '';

    var name = document.createElement('span');
    name.className = 'files-changed-name';
    name.textContent = file.displayName || file.path || '';
    button.appendChild(name);

    var counts = document.createElement('span');
    counts.className = 'files-changed-counts';
    if ((file.added || 0) > 0) {
      var a = document.createElement('span');
      a.className = 'turn-activity-add';
      a.textContent = '+' + file.added;
      counts.appendChild(a);
    }
    if ((file.removed || 0) > 0) {
      var d = document.createElement('span');
      d.className = 'turn-activity-del';
      d.textContent = '-' + file.removed;
      counts.appendChild(d);
    }
    button.appendChild(counts);
    item.appendChild(button);

    var diff = document.createElement('div');
    diff.className = 'files-changed-diff';
    diff.innerHTML = renderDiffLines(file.lines || []);
    button.addEventListener('click', function (e) {
      e.preventDefault();
      e.stopPropagation();
      item.classList.toggle('open');
      scrollToBottom();
    });
    item.appendChild(diff);
    list.appendChild(item);
  });

  card.appendChild(list);
  updateEmptyStateVisibility();
  scrollToBottom();
}

function formatWorkedFor(durationMs) {
  if (!durationMs || durationMs <= 0) return '';
  var secondsLabel = formatReasoningSeconds(durationMs);
  return (t('workedFor') || 'Worked for {0}').replace('{0}', secondsLabel);
}

function syncTurnActivityChevron(details) {
  if (!details) return;
  var chevron = details.querySelector('.turn-activity-chevron');
  if (!chevron) return;
  chevron.textContent = details.open ? '∨' : '›';
}

function scrollTurnActivityThoughts(details) {
  if (!details || !details.open) return;
  details.querySelectorAll('.turn-activity-thought').forEach(function (el) {
    el.scrollTop = el.scrollHeight;
  });
}

/**
 * Renders (or refreshes) the turn-activity fold at its seq slot.
 *
 * The fold is keyed by the turn it belongs to, so a live fold that keeps growing and the fold
 * rebuilt from the transcript are the same entry: the second render overwrites the first's
 * contents and leaves the row where it is. No "insert after the last user row" step is needed —
 * the seq (TimelineOrderPolicy's activity slot, just after the turn's user message) already
 * places it, which is what makes the fold anchored to the turn's first activity instead of
 * jumping to the end of the turn when it is finalized.
 */
function appendTurnActivityCard(event) {
  state.currentAssistantEl = null;
  state.currentReasoningEl = null;
  var items = event.items || [];
  if (!items.length && !(event.exploredFileCount || event.searchCount || event.commandCount || event.thoughtCount)) {
    return;
  }

  var key = turnActivityEntryKey(event);
  var seq = resolveEventSeq(event, SEQ_ACTIVITY);
  var row = getEntryRow(key);
  var details = row ? row.querySelector('.turn-activity') : null;
  // Preserve a fold the user already opened; a fresh/replayed fold starts collapsed.
  var keepOpen = !!(details && details.open && event.upsert === true);

  if (!details) {
    row = document.createElement('div');
    row.className = 'message-row assistant turn-activity-host';
    details = document.createElement('details');
    details.className = 'turn-activity';
    details.addEventListener('toggle', function () {
      syncTurnActivityChevron(details);
      if (details.open) {
        details.classList.add('is-expanded');
        scrollTurnActivityThoughts(details);
        scrollToBottom();
      } else {
        details.classList.remove('is-expanded');
      }
    });
    row.appendChild(details);
    registerEntry(key, row, seq);
  } else {
    details.innerHTML = '';
    insertBySeq(row, seq);
  }

  var summary = document.createElement('summary');
  var summaryText = document.createElement('span');
  summaryText.className = 'turn-activity-summary-text';
  summaryText.textContent = turnActivitySummaryText(event);
  summary.appendChild(summaryText);

  var chevron = document.createElement('span');
  chevron.className = 'turn-activity-chevron';
  chevron.textContent = '›';
  summary.appendChild(chevron);
  details.appendChild(summary);

  var body = document.createElement('div');
  body.className = 'turn-activity-body';

  var workedFor = formatWorkedFor(event.durationMs);
  if (workedFor) {
    var duration = document.createElement('div');
    duration.className = 'turn-activity-duration';
    duration.textContent = workedFor;
    body.appendChild(duration);
  }

  items.forEach(function (item) {
    var hasDiff = item.lines && item.lines.length;
    var hasThought = item.kind === 'thought' && item.body;
    var hasNarration = item.kind === 'narration' && item.body;
    var entry = document.createElement('div');
    entry.className = 'turn-activity-item'
      + (hasDiff ? ' has-diff' : '')
      + (hasThought ? ' has-thought' : '')
      + (hasNarration ? ' has-narration' : '');

    if (hasThought || hasNarration) {
      var thoughtLabel = document.createElement('div');
      thoughtLabel.className = 'turn-activity-thought-label';
      thoughtLabel.textContent = item.verb || (hasNarration ? (t('said') || 'Said') : (t('thought') || 'Thought'));
      entry.appendChild(thoughtLabel);

      var thought = document.createElement('div');
      thought.className = 'turn-activity-thought';
      thought.textContent = item.body || '';
      entry.appendChild(thought);
      body.appendChild(entry);
      return;
    }

    var button = document.createElement('button');
    button.type = 'button';
    button.className = 'turn-activity-row';
    button.title = item.path || item.detail || '';

    var line = document.createElement('span');
    line.className = 'turn-activity-line';
    var verbText = item.verb || '';
    var detailText = item.detail || item.path || '';
    line.textContent = verbText && detailText
      ? (verbText + ' ' + detailText)
      : (verbText || detailText);
    button.appendChild(line);

    if (item.status) {
      var status = document.createElement('span');
      status.className = 'turn-activity-status tool-status';
      applyToolStatusBadge(status, item.status);
      if (item.statusLabel) status.textContent = item.statusLabel;
      button.appendChild(status);
    }

    entry.appendChild(button);

    if (hasDiff) {
      var diff = document.createElement('div');
      diff.className = 'turn-activity-diff';
      diff.innerHTML = renderDiffLines(item.lines);
      button.addEventListener('click', function (e) {
        e.preventDefault();
        e.stopPropagation();
        entry.classList.toggle('open');
        scrollToBottom();
      });
      entry.appendChild(diff);
    } else if (item.body || item.messageId || item.toolCallId) {
      var detailPanel = document.createElement('pre');
      detailPanel.className = 'turn-activity-tool-detail';
      if (item.body) {
        detailPanel.textContent = item.body;
        entry.dataset.hydrated = '1';
      } else {
        detailPanel.textContent = '…';
        entry.dataset.hydrated = '0';
      }
      if (item.messageId) entry.dataset.messageId = item.messageId;
      if (item.toolCallId) entry.dataset.toolCallId = item.toolCallId;
      button.addEventListener('click', function (e) {
        e.preventDefault();
        e.stopPropagation();
        var opening = !entry.classList.contains('open');
        entry.classList.toggle('open');
        if (opening && entry.dataset.hydrated !== '1') {
          requestToolDetailForEntry(entry, detailPanel);
        }
        scrollToBottom();
      });
      entry.appendChild(detailPanel);
    }

    body.appendChild(entry);
  });

  details.appendChild(body);
  // A live fold preserves an already-opened state; a final/replayed fold starts collapsed.
  details.open = keepOpen;
  if (details.open) {
    details.classList.add('is-expanded');
  } else {
    details.classList.remove('is-expanded');
  }
  syncTurnActivityChevron(details);
  updateEmptyStateVisibility();
  scrollTurnActivityThoughts(details);
  scrollToBottom();
}

function upsertCompactionCheckpoint(event) {
  const id = event.id || 'compaction';
  const key = 'compaction:' + id;
  const seq = resolveEventSeq(event, SEQ_COMPACTION);
  let row = getEntryRow(key);
  let details = row ? row.querySelector('.compaction-checkpoint') : null;
  if (!details) {
    state.currentAssistantEl = null;
    state.currentReasoningEl = null;
    row = document.createElement('div');
    row.className = 'message-row assistant compaction-row';
    details = document.createElement('details');
    details.className = 'compaction-checkpoint';
    details.dataset.compactionId = id;
    details.innerHTML =
      '<summary><span class="compaction-title"></span><span class="tool-status"></span></summary>' +
      '<div class="compaction-body">' +
      '<div class="compaction-summary"></div>' +
      '<details class="compaction-tech"><summary class="compaction-tech-label"></summary>' +
      '<pre class="compaction-detail"></pre></details>' +
      '</div>';
    row.appendChild(details);
    registerEntry(key, row, seq);
  } else {
    insertBySeq(row, seq);
  }
  const title = details.querySelector('.compaction-title');
  if (title) title.textContent = event.title || '';
  applyToolStatusBadge(
    details.querySelector('.tool-status'),
    event.running ? 'running' : (event.status || 'succeeded'));
  const summary = details.querySelector('.compaction-summary');
  if (summary) {
    summary.textContent = event.summary || '';
    summary.style.display = event.summary ? 'block' : 'none';
  }
  const tech = details.querySelector('.compaction-tech');
  const techLabel = details.querySelector('.compaction-tech-label');
  const detail = details.querySelector('.compaction-detail');
  if (techLabel) techLabel.textContent = event.detailsLabel || '';
  const techText = [event.header, event.detail].filter(Boolean).join('\n\n');
  if (detail) detail.textContent = techText;
  if (tech) tech.style.display = techText && !event.running ? 'block' : 'none';
  scrollToBottom();
}

function isPlanSpecialTool(name) {
  return name === 'ask_plan_clarification' || name === 'publish_plan';
}

function getPlanClarifyCard(requestId) {
  if (!requestId) return null;
  return document.querySelector('.plan-clarify-card[data-request-id="' + cssEscape(requestId) + '"]');
}

function showPlanClarify(event) {
  if (!event || !event.requestId || !event.questions || !event.questions.length) return;
  var existing = getPlanClarifyCard(event.requestId);
  var card;
  if (existing) {
    card = existing;
  } else {
    state.currentAssistantEl = null;
    state.currentReasoningEl = null;
    var row = document.createElement('div');
    row.className = 'message-row assistant plan-row';
    card = document.createElement('div');
    card.className = 'plan-clarify-card';
    row.appendChild(card);
    getMessageRoot().appendChild(row);
    updateEmptyStateVisibility();
  }
  card.dataset.requestId = event.requestId;
  card.innerHTML = '';

  var title = document.createElement('div');
  title.className = 'plan-card-title';
  title.dataset.i18n = 'planClarifyTitle';
  title.textContent = t('planClarifyTitle');
  card.appendChild(title);

  (event.questions || []).forEach(function (question) {
    var block = document.createElement('div');
    block.className = 'plan-clarify-question';
    block.dataset.questionId = question.id || '';
    block.dataset.allowMultiple = question.allowMultiple ? '1' : '0';
    var prompt = document.createElement('div');
    prompt.className = 'plan-clarify-prompt';
    prompt.textContent = question.prompt || '';
    block.appendChild(prompt);
    var options = document.createElement('div');
    options.className = 'plan-clarify-options';
    (question.options || []).forEach(function (option) {
      var btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'plan-clarify-option';
      btn.dataset.optionId = option.id || '';
      btn.textContent = option.label || option.id || '';
      btn.addEventListener('click', function () {
        if (card.dataset.resolved === '1') return;
        if (block.dataset.allowMultiple === '1') {
          btn.classList.toggle('selected');
        } else {
          options.querySelectorAll('.plan-clarify-option').forEach(function (other) {
            other.classList.toggle('selected', other === btn);
          });
        }
      });
      options.appendChild(btn);
    });
    block.appendChild(options);
    card.appendChild(block);
  });

  if (event.allowFreeText !== false) {
    var note = document.createElement('textarea');
    note.className = 'plan-clarify-notes';
    note.rows = 2;
    note.placeholder = t('planClarifyNotes');
    note.dataset.i18nPlaceholder = 'planClarifyNotes';
    card.appendChild(note);
  }

  var actions = document.createElement('div');
  actions.className = 'plan-card-actions';
  var submit = document.createElement('button');
  submit.type = 'button';
  submit.className = 'plan-card-button primary';
  submit.dataset.i18n = 'planClarifySubmit';
  submit.textContent = t('planClarifySubmit');
  submit.addEventListener('click', function () {
    submitPlanClarify(card);
  });
  actions.appendChild(submit);
  card.appendChild(actions);

  if (event.resolved) {
    applyPlanClarifyResolved(card, event.summary);
  }
  scrollToBottom(true);
}

function submitPlanClarify(card) {
  if (!card || card.dataset.resolved === '1') return;
  var selections = {};
  var hasSelection = false;
  card.querySelectorAll('.plan-clarify-question').forEach(function (block) {
    var qid = block.dataset.questionId || '';
    var ids = [];
    block.querySelectorAll('.plan-clarify-option.selected').forEach(function (btn) {
      if (btn.dataset.optionId) ids.push(btn.dataset.optionId);
    });
    if (qid && ids.length) {
      selections[qid] = ids;
      hasSelection = true;
    }
  });
  var notes = card.querySelector('.plan-clarify-notes');
  var freeText = notes && notes.value ? String(notes.value).trim() : '';
  if (!hasSelection && !freeText) return;
  post({
    type: 'planClarifyAnswer',
    requestId: card.dataset.requestId,
    selections: selections,
    freeText: freeText
  });
}

function applyPlanClarifyResolved(card, summary) {
  if (!card) return;
  card.dataset.resolved = '1';
  card.querySelectorAll('button, textarea').forEach(function (el) {
    el.disabled = true;
  });
  var result = card.querySelector('.plan-clarify-result');
  if (!result) {
    result = document.createElement('div');
    result.className = 'plan-clarify-result';
    card.appendChild(result);
  }
  result.textContent = summary || t('planClarifyAnswered');
  result.dataset.i18n = summary ? '' : 'planClarifyAnswered';
}

function resolvePlanClarify(event) {
  var card = getPlanClarifyCard(event && event.requestId);
  if (!card) return;
  applyPlanClarifyResolved(card, event && event.summary);
}

function resolvePlanMarkdown(event) {
  if (event && event.markdownB64) return decodeBase64Utf8(event.markdownB64);
  if (event && event.markdown) return event.markdown;
  if (event && event.overview) return event.overview;
  return '';
}

/** Stable entry key for a plan-ready card. Replay and the live dispatcher use the same run id. */
function planReadyEntryKey(event) {
  return 'plan:' + ((event && event.runId) || 'plan');
}

/** Removes a plan-ready card that is no longer the active plan. */
function clearPlanReady(event) {
  var runId = event && event.runId;
  if (runId) {
    if (!removeEntry('plan:' + runId)) removePlanReadyCardRows(runId);
  } else {
    document.querySelectorAll('.plan-ready-card').forEach(function (card) {
      var id = card.dataset.runId || 'plan';
      if (!removeEntry('plan:' + id)) removePlanReadyCardRows(id);
    });
  }
  updateEmptyStateVisibility();
}

/** DOM fallback for a card that was never registered as a keyed entry. */
function removePlanReadyCardRows(runId) {
  document.querySelectorAll('.plan-ready-card[data-run-id="' + cssEscape(runId) + '"]')
    .forEach(function (card) {
      var row = card.closest('.message-row') || card;
      if (row.parentNode) row.parentNode.removeChild(row);
    });
}

function showPlanReady(event) {
  if (!event) return;
  var runId = event.runId || 'plan';
  var key = planReadyEntryKey(event);
  var built = event.built === true;
  var row = getEntryRow(key);
  var card = row ? row.querySelector('.plan-ready-card') : null;
  var seq = resolveEventSeq(event);
  if (card) {
    card.innerHTML = '';
    // A missing seq means "keep the current position"; insertBySeq would treat that as append.
    if (typeof seq === 'number') insertBySeq(row, seq);
  } else {
    // The timeline shows one plan card: a revision mints a new run id, so drop the superseded one.
    document.querySelectorAll('.plan-ready-card').forEach(function (other) {
      removeEntry('plan:' + (other.dataset.runId || 'plan'));
    });
    state.currentAssistantEl = null;
    state.currentReasoningEl = null;
    row = document.createElement('div');
    row.className = 'message-row assistant plan-row';
    card = document.createElement('div');
    card.className = 'plan-ready-card';
    row.appendChild(card);
    // Placed by seq: the plan card belongs to the turn whose publish_plan call produced it, so a
    // later turn's smaller seq can no longer overtake it, and a full replay re-emits it at the
    // same slot instead of dropping it.
    registerEntry(key, row, seq);
    updateEmptyStateVisibility();
  }
  card.dataset.runId = runId;
  if (event.planPath) card.dataset.planPath = event.planPath;
  if (built) card.dataset.built = '1';
  else card.removeAttribute('data-built');

  var title = document.createElement('div');
  title.className = 'plan-card-title';
  title.textContent = event.title || t('planReadyTitle');
  card.appendChild(title);

  var body = document.createElement('div');
  body.className = 'plan-ready-body md-root';
  applyMarkdownHtml(body, resolveRenderedHtml(event, resolvePlanMarkdown(event)));
  card.appendChild(body);

  var actions = document.createElement('div');
  actions.className = 'plan-card-actions';
  if (event.planPath) {
    var openBtn = document.createElement('button');
    openBtn.type = 'button';
    openBtn.className = 'plan-card-button';
    openBtn.dataset.i18n = 'planOpenEditor';
    openBtn.textContent = t('planOpenEditor');
    openBtn.addEventListener('click', function () {
      post({ type: 'planOpenEditor', path: card.dataset.planPath });
    });
    actions.appendChild(openBtn);
  }
  var buildBtn = document.createElement('button');
  buildBtn.type = 'button';
  buildBtn.className = 'plan-card-button primary';
  buildBtn.dataset.i18n = 'planBuild';
  buildBtn.textContent = t('planBuild');
  if (built) {
    buildBtn.disabled = true;
  } else {
    buildBtn.addEventListener('click', function () {
      if (buildBtn.disabled) return;
      buildBtn.disabled = true;
      card.dataset.built = '1';
      post({ type: 'planBuild' });
    });
  }
  actions.appendChild(buildBtn);
  card.appendChild(actions);
}

function appendOverflowSkipped(event) {
  state.currentAssistantEl = null;
  state.currentReasoningEl = null;
  const row = document.createElement('div');
  row.className = 'message-row assistant status-row';
  const el = document.createElement('div');
  el.className = 'overflow-skipped';
  el.textContent = event.message || '';
  row.appendChild(el);
  getMessageRoot().appendChild(row);
  updateEmptyStateVisibility();
  scrollToBottom();
}

function handleEvent(event) {
  if (!event || !event.type) return;
  switch (event.type) {
    case 'RESET_TIMELINE':
      resetTimeline();
      updateEmptyStateVisibility();
      break;
    case 'USER_MESSAGE':
      // A new user message opens a new turn band in the live cursor; the previous turn's cards
      // stay exactly where their seq put them, so nothing needs to be "sealed" or re-parented.
      appendMessage(
        'user',
        event.content || '',
        false,
        event.images || [],
        event.startedAt || '',
        event.mentions || [],
        event.seq,
        event.messageId);
      break;
    case 'FILES_CHANGED':
      appendFilesChangedCard(event);
      break;
    case 'TURN_ACTIVITY':
      appendTurnActivityCard(event);
      break;
    case 'COMPACTION_CHECKPOINT':
      upsertCompactionCheckpoint(event);
      break;
    case 'OVERFLOW_RETRY_SKIPPED':
      appendOverflowSkipped(event);
      break;
    case 'RUN_STARTED':
      state.currentAssistantEl = null;
      state.currentReasoningEl = null;
      break;
    case 'REASONING_MESSAGE_START':
    case 'REASONING_MESSAGE_CONTENT':
    case 'REASONING_MESSAGE_END':
      // Reasoning is folded into TURN_ACTIVITY; ignore standalone thought bubbles.
      break;
    case 'TEXT_MESSAGE_START':
      state.currentAssistantEl = null;
      state.assistantStarted[event.messageId] = false;
      break;
    case 'TEXT_MESSAGE_CONTENT':
      // Plain-text deltas are unused for display; live Markdown arrives via STATIC_ASSISTANT_HTML.
      finalizeReasoningLabel(event.messageId);
      if (!state.assistantStarted[event.messageId]) ensureAssistantBubble(event.messageId);
      break;
    case 'TEXT_MESSAGE_END':
      state.currentAssistantEl = null;
      break;
    case 'STATIC_ASSISTANT_HTML':
      applyAssistantHtml(
        event.messageId,
        resolveRenderedHtml(event),
        event.createIfMissing !== false,
        event.streaming === true,
        event.responseDurationMs,
        event.seq);
      updateCopyText(
        findAssistantBubbleRow(event.messageId),
        resolveEventMarkdown(event));
      if (!event.streaming) state.currentAssistantEl = null;
      break;
    case 'REMOVE_ASSISTANT_BUBBLES': {
      var ids = event.messageIds || [];
      ids.forEach(function (id) {
        if (!removeEntry('msg:' + id)) {
          var row = findAssistantBubbleRow(id);
          if (row && row.parentNode) row.parentNode.removeChild(row);
        }
        delete state.assistantStarted[id];
      });
      state.currentAssistantEl = null;
      break;
    }
    case 'TOOL_CALL_START':
      if (isPlanSpecialTool(event.toolCallName)) {
        state.currentAssistantEl = null;
        break;
      }
      createToolCard(event.toolCallId, event.toolCallName, event.seq);
      break;
    case 'TOOL_CALL_ARGS': {
      const card = getToolCard(event.toolCallId);
      const pre = card && card.querySelector('.tool-args');
      // delta is the full JSON snapshot built so far, not an incremental chunk
      if (pre) pre.textContent = event.delta || '';
      scrollToBottom();
      break;
    }
    case 'TOOL_CALL_END': {
      const card = getToolCard(event.toolCallId);
      if (!card) break;
      const normalized = (event.status || 'running').toLowerCase();
      if (normalized === 'awaiting_approval' || normalized === 'approval_denied') {
        applyToolStatusBadge(card.querySelector('.tool-status'), normalized);
        if (normalized === 'awaiting_approval') {
          card.dataset.awaitingApproval = 'true';
        }
        break;
      }
      const panel = card.querySelector('.tool-approval');
      const hasPendingApproval = panel && panel.querySelector('.tool-approval-actions');
      if (card.dataset.awaitingApproval !== 'true' && !hasPendingApproval) {
        applyToolStatusBadge(card.querySelector('.tool-status'), event.status || 'running');
      }
      break;
    }
    case 'TOOL_APPROVAL_REQUEST':
      showToolApproval(event);
      break;
    case 'TOOL_APPROVAL_RESOLVED':
      resolveToolApproval(event);
      break;
    case 'PLAN_CLARIFY_REQUEST':
      showPlanClarify(event);
      break;
    case 'PLAN_CLARIFY_RESOLVED':
      resolvePlanClarify(event);
      break;
    case 'PLAN_READY':
      showPlanReady(event);
      break;
    case 'PLAN_CLEARED':
      clearPlanReady(event);
      break;
    case 'TOOL_CALL_OUTPUT': {
      const card = getToolCard(event.toolCallId);
      const result = card && card.querySelector('.tool-result');
      const html = card && card.querySelector('.tool-result-html');
      if (result && html) {
        result.style.display = 'block';
        html.textContent += event.delta || '';
      }
      scrollToBottom();
      break;
    }
    case 'TOOL_CALL_RESULT': {
      const card = getToolCard(event.toolCallId);
      if (!card) break;
      if (event.messageId) card.dataset.messageId = event.messageId;
      applyToolStatusBadge(card.querySelector('.tool-status'), event.status || 'succeeded');
      if (event.header) {
        let header = card.querySelector('.tool-header');
        if (!header) {
          header = document.createElement('div');
          header.className = 'tool-header';
          card.querySelector('.tool-body').prepend(header);
        }
        header.textContent = event.header;
      }
      if (event.summary) {
        let summary = card.querySelector('.tool-summary-text');
        if (!summary) {
          summary = document.createElement('div');
          summary.className = 'tool-summary-text';
          card.querySelector('.tool-body').insertBefore(summary, card.querySelector('.tool-result'));
        }
        summary.textContent = event.summary;
      }
      const result = card.querySelector('.tool-result');
      const html = card.querySelector('.tool-result-html');
      if (result && html) {
        result.style.display = 'block';
        applyMarkdownHtml(html, resolveRenderedHtml(event, event.content || ''));
      }
      var contentText = event.content || '';
      var needsHydration = !!(event.messageId || event.toolCallId)
        && (contentText.indexOf('[Tool result evicted') >= 0 || contentText.length < 80);
      if (needsHydration) {
        card.dataset.hydrated = '0';
        if (!card.dataset.bindHydrate) {
          card.dataset.bindHydrate = '1';
          card.addEventListener('toggle', function () {
            if (card.open && card.dataset.hydrated !== '1') {
              requestToolDetailForToolCard(card);
            }
          });
        }
      } else {
        card.dataset.hydrated = '1';
      }
      scrollToBottom();
      break;
    }
  }
}

function applyThemeTokensToRoot(tokensCss) {
  var root = document.documentElement;
  root.style.cssText = '';
  tokensCss.replace(/(--[\\w-]+)\\s*:\\s*([^;]+);/g, function(_, name, value) {
    root.style.setProperty(name.trim(), value.trim());
  });
}

function syncThemeSurfaces() {
  var rootStyle = getComputedStyle(document.documentElement);
  var chatBg = rootStyle.getPropertyValue('--chat-bg').trim();
  var assistantText = rootStyle.getPropertyValue('--assistant-text').trim();
  if (chatBg) {
    document.documentElement.style.backgroundColor = chatBg;
    document.body.style.backgroundColor = chatBg;
    var scroller = document.getElementById('chat-scroll');
    if (scroller) scroller.style.backgroundColor = chatBg;
  }
  if (assistantText) {
    document.body.style.color = assistantText;
  }
}

function applyThemeUpdate(highlightHref, tokensB64, syntaxB64) {
  var link = document.querySelector('head link[rel="stylesheet"]');
  if (link) {
    link.href = highlightHref;
  }
  var tokensCss = decodeBase64Utf8(tokensB64);
  var tokensEl = document.getElementById('chat-theme-tokens');
  if (tokensEl) {
    tokensEl.textContent = tokensCss;
  }
  var syntaxEl = document.getElementById('chat-code-syntax');
  if (syntaxEl) {
    syntaxEl.textContent = decodeBase64Utf8(syntaxB64);
  }
  applyThemeTokensToRoot(tokensCss);
  syncThemeSurfaces();
}

/**
 * Renders a full authoritative timeline. Every entry the events carry lands at its own seq, and
 * the live cursor is re-anchored afterwards so a mid-turn reload's next live event continues the
 * replay's numbering instead of landing in a stale turn band.
 */
function replayEvents(events) {
  beginBatch();
  state.trackReasoningDuration = false;
  resetTimeline();
  var list = Array.isArray(events) ? events : [];
  var maxSeq = -1;
  for (const raw of list) {
    try {
      const event = typeof raw === 'string' ? JSON.parse(raw) : raw;
      if (event && typeof event.seq === 'number' && isFinite(event.seq) && event.seq > maxSeq) {
        maxSeq = event.seq;
      }
      handleEvent(event);
    } catch (e) { console.warn('replayEvents parse failed', e); }
  }
  // Continue the live cursor from where the replay ended so a mid-turn reload's next live card
  // does not land in a stale turn band.
  syncLiveCursorFromSeq(maxSeq);
  state.trackReasoningDuration = true;
  endBatch(true);
}

function appendEvents(events) {
  const root = document.getElementById('messages');
  if (!root) return;
  const fragment = document.createDocumentFragment();
  beginBatch();
  state.batchTarget = fragment;
  state.trackReasoningDuration = false;
  var list = Array.isArray(events) ? events : [];
  var maxSeq = -1;
  for (const raw of list) {
    try {
      const event = typeof raw === 'string' ? JSON.parse(raw) : raw;
      if (event && typeof event.seq === 'number' && isFinite(event.seq) && event.seq > maxSeq) {
        maxSeq = event.seq;
      }
      handleEvent(event);
    } catch (e) { console.warn('appendEvents parse failed', e); }
  }
  state.batchTarget = null;
  state.trackReasoningDuration = true;
  root.appendChild(fragment);
  // Merge the freshly appended rows into the global seq order (older pages can interleave).
  sortMessageRoot();
  syncLiveCursorFromSeq(maxSeq);
  endBatch(false);
}

/**
 * Re-establishes ascending order for rows that were appended without absolute positioning (e.g.
 * a paged append whose seqs predate the current tail). Stable by construction: rows keep the
 * relative order they were inserted in when their seqs tie.
 */
function sortMessageRoot() {
  var root = document.getElementById('messages');
  if (!root || root.children.length < 2) return;
  var rows = Array.prototype.slice.call(root.children);
  var needsSort = false;
  for (var i = 1; i < rows.length; i++) {
    if (Number(rows[i].getAttribute('data-seq')) < Number(rows[i - 1].getAttribute('data-seq'))) {
      needsSort = true;
      break;
    }
  }
  if (!needsSort) return;
  rows.sort(function (a, b) {
    var left = Number(a.getAttribute('data-seq'));
    var right = Number(b.getAttribute('data-seq'));
    if (isNaN(left)) left = Number.MAX_SAFE_INTEGER;
    if (isNaN(right)) right = Number.MAX_SAFE_INTEGER;
    return left - right;
  });
  for (var j = 0; j < rows.length; j++) root.appendChild(rows[j]);
}

function setOlderMessagesAvailable(available) {
  state.hasOlderMessages = !!available;
  state.loadingOlder = false;
}

function maybeLoadOlderOnScroll() {
  if (state.loadingOlder || !state.hasOlderMessages) return;
  const scroller = getChatScroller();
  if (!scroller || scroller.scrollTop > 160) return;
  state.loadingOlder = true;
  post({ type: 'loadOlder' });
}

function prependEvents(events, hasOlderMessages) {
  const scroller = getChatScroller();
  const root = document.getElementById('messages');
  if (!scroller || !root) return;
  const previousHeight = scroller.scrollHeight;
  const previousTop = scroller.scrollTop;
  const fragment = document.createDocumentFragment();
  // An older page is projected with the same 0-based turn bands as the current window, so its
  // seqs would interleave with (and be overtaken by) the newer rows. Shift the whole page below
  // the current minimum: relative order inside the page is preserved and the page stays "older".
  const minSeq = findMinSeq(root);
  const offset = minSeq - SEQ_PAGE_GAP;
  beginBatch();
  state.batchTarget = fragment;
  for (const raw of events) {
    try {
      const event = typeof raw === 'string' ? JSON.parse(raw) : raw;
      if (event && typeof event.seq === 'number' && isFinite(event.seq)) {
        event.seq += offset;
      }
      handleEvent(event);
    } catch (e) { console.warn('prependEvents parse failed', e); }
  }
  state.batchTarget = null;
  root.insertBefore(fragment, root.firstChild);
  endBatch(false);
  setOlderMessagesAvailable(!!hasOlderMessages);
  scroller.scrollTop = previousTop + (scroller.scrollHeight - previousHeight);
  state.currentAssistantEl = null;
  state.currentReasoningEl = null;
}

/** Smallest data-seq currently rendered, or 0 when the timeline is empty. */
function findMinSeq(root) {
  var scope = root || document.getElementById('messages');
  if (!scope) return 0;
  var min = Infinity;
  for (var i = 0; i < scope.children.length; i++) {
    var value = Number(scope.children[i].getAttribute('data-seq'));
    if (!isNaN(value) && value < min) min = value;
  }
  return isFinite(min) ? min : 0;
}

function handleWebMessage(message) {
  const command = typeof message === 'string' ? JSON.parse(message) : message;
  if (!command || !command.command) return;
  if (command.command === 'replay') {
    replayEvents(Array.isArray(command.events) ? command.events : []);
  } else if (command.command === 'append') {
    appendEvents(Array.isArray(command.events) ? command.events : []);
  } else if (command.command === 'prepend') {
    prependEvents(
      Array.isArray(command.events) ? command.events : [],
      !!command.hasOlderMessages);
  } else if (command.command === 'event') {
    // Single real-time event on the same channel as replay. Shares handleEvent, so live and
    // replayed entries go through one renderer and one seq-based placement rule.
    var immediate = Array.isArray(command.events) ? command.events : [];
    for (var i = 0; i < immediate.length; i++) {
      try {
        var parsed = typeof immediate[i] === 'string' ? immediate[i] : JSON.stringify(immediate[i]);
        handleEvent(typeof parsed === 'string' ? JSON.parse(parsed) : parsed);
      } catch (e) { console.warn('event parse failed', e); }
    }
  } else if (command.command === 'historyAvailability') {
    setOlderMessagesAvailable(!!command.hasOlderMessages);
  } else if (command.command === 'toolDetail') {
    applyToolDetailPayload(command);
  } else if (command.command === 'reset') {
    beginBatch();
    resetTimeline();
    endBatch(false);
  }
  if (command.replayComplete && Number.isInteger(command.renderGeneration)) {
    post({ type: 'replayComplete', renderGeneration: command.renderGeneration });
  }
}

if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener('message', function (event) {
    try { handleWebMessage(event.data); }
    catch (e) { console.warn('chat web message failed', e); }
  });
}

const chatScroller = getChatScroller();
if (chatScroller) {
  chatScroller.addEventListener('scroll', function () {
    state.autoScrollEnabled = isNearBottom();
    maybeLoadOlderOnScroll();
  }, { passive: true });
  chatScroller.addEventListener('wheel', function (e) {
    if (e.deltaY < 0) state.autoScrollEnabled = false;
  }, { passive: true });
  chatScroller.addEventListener('touchmove', function () {
    if (!isNearBottom()) state.autoScrollEnabled = false;
  }, { passive: true });
}
document.addEventListener('selectionchange', function () {
  if (hasActiveSelection()) state.autoScrollEnabled = false;
  else if (isNearBottom()) state.autoScrollEnabled = true;
});

(function bindExternalLinks() {
  var root = document.getElementById('messages');
  if (!root) return;
  root.addEventListener('click', function (e) {
    var target = e.target;
    if (!target || typeof target.closest !== 'function') return;
    var anchor = target.closest('a[href]');
    if (!anchor) return;
    var href = anchor.getAttribute('href');
    if (!href || href.charAt(0) === '#') return;
    e.preventDefault();
    e.stopPropagation();
    post({ type: 'openUrl', url: anchor.href });
  });
})();

(function bindImageLightbox() {
  var lightbox = document.getElementById('image-lightbox');
  if (!lightbox) return;
  var backdrop = lightbox.querySelector('.image-lightbox-backdrop');
  var closeBtn = lightbox.querySelector('.image-lightbox-close');
  if (backdrop) backdrop.addEventListener('click', closeImagePreview);
  if (closeBtn) closeBtn.addEventListener('click', closeImagePreview);
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && !lightbox.hidden) closeImagePreview();
  });
})();

