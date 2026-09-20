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
  liveContentOrdinal: 0,
  // performance.now() when the first replay batch of the current render arrived, so the final
  // replayComplete can report the in-page render time back to C# (session-switch profiling).
  replayStartAt: 0,
  // Per-session scroll positions, so switching back to a conversation restores where the user was
  // reading. Populated lazily by saveScroll.
  scrollBySession: {},
  // Session whose timeline is currently mounted (drives which slot saveScroll writes to).
  currentSessionId: null,
  // Non-null while a replay is restoring a saved position: scrollToBottom is suppressed until the
  // replay completes so the restore (not the auto-scroll) wins.
  pendingRestoreTop: null,
  // Content revision of the timeline currently mounted. C# sends the same fingerprint it caches
  // replay shards under, so a saved session DOM snapshot can be restored only while the content it
  // was rendered from is still current (any mismatch means the session changed -> replay instead).
  renderedRevision: null
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
  // While a replay is restoring a saved position, suppress auto-scroll so the restore wins.
  if (state.pendingRestoreTop !== null) return;
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

/** Remembers a session's current scroll position before switching away from it. */
function saveScroll(sessionId) {
  if (!sessionId) return;
  const scroller = getChatScroller();
  if (!scroller) return;
  state.scrollBySession[sessionId] = scroller.scrollTop;
}

/** Restores a session's saved scroll position; a session with nothing saved is left untouched. */
function restoreScroll(sessionId) {
  const scroller = getChatScroller();
  if (!scroller) return;
  const saved = sessionId ? state.scrollBySession[sessionId] : undefined;
  if (typeof saved === 'number') {
    const maxTop = Math.max(0, scroller.scrollHeight - scroller.clientHeight);
    scroller.scrollTop = Math.min(saved, maxTop);
  }
  state.autoScrollEnabled = isNearBottom();
}

/**
 * Called when the first replay batch for a session arrives: stash the outgoing session's scroll
 * and arm a restore for the incoming one. A cold session arms null, so the normal scroll-to-bottom
 * still runs and the timeline opens at the latest turn.
 */
function beginReplayScroll(sessionId) {
  if (sessionId) {
    if (state.currentSessionId && state.currentSessionId !== sessionId) {
      saveScroll(state.currentSessionId);
    }
    state.currentSessionId = sessionId;
  }
  const saved = sessionId ? state.scrollBySession[sessionId] : undefined;
  state.pendingRestoreTop = (typeof saved === 'number') ? saved : null;
}

/**
 * Per-session DOM snapshots.
 *
 * A snapshot keeps a session's rendered rows in a detached container plus the session-scoped part
 * of `state`, so switching back moves the existing nodes into #messages instead of rebuilding the
 * timeline (no Markdown / highlight / Mermaid work, node identity and listeners preserved).
 *
 * This is deliberately conservative: C# sends the content revision it expects, and a snapshot is
 * restored only when its revision matches, so any content change misses and the caller falls back
 * to the normal full replay. Rows are only ever moved out of #messages when switching *away*, so a
 * displayed session is never emptied.
 */
var SESSION_SNAPSHOT_LIMIT = 4;
var sessionSnapshots = new Map();

/** Session-scoped pieces of `state`, swapped in and out with a snapshot. */
var SESSION_SCOPED_KEYS = [
  'entries',
  'toolCalls',
  'liveTurn',
  'liveTurnStarted',
  'liveContentOrdinal',
  'assistantStarted',
  'reasoningStarted',
  'reasoningStartAt',
  'reasoningFinalizedMs',
  'currentAssistantEl',
  'currentReasoningEl',
  'hasOlderMessages',
  'lastScrollHeight'
];

function captureSessionState() {
  var captured = {};
  for (var i = 0; i < SESSION_SCOPED_KEYS.length; i++) {
    captured[SESSION_SCOPED_KEYS[i]] = state[SESSION_SCOPED_KEYS[i]];
  }
  return captured;
}

function applySessionState(captured) {
  if (!captured) return;
  for (var i = 0; i < SESSION_SCOPED_KEYS.length; i++) {
    var key = SESSION_SCOPED_KEYS[i];
    if (Object.prototype.hasOwnProperty.call(captured, key)) state[key] = captured[key];
  }
}

/** True when a restorable snapshot exists and its revision matches the one C# expects. */
function hasSessionSnapshot(sessionId, revision) {
  var entry = sessionId ? sessionSnapshots.get(sessionId) : null;
  if (!entry || !entry.revision) return false;
  if (revision && revision !== entry.revision) return false;
  return true;
}

/** Moves the currently mounted rows into a session's snapshot and records its scroll + state. */
function saveSessionSnapshot(sessionId, revision) {
  if (!sessionId) return null;
  var root = document.getElementById('messages');
  if (!root) return null;
  var entry = sessionSnapshots.get(sessionId) || { container: document.createElement('div') };
  while (root.firstChild) {
    entry.container.appendChild(root.firstChild);
  }
  if (!entry.container.firstChild) {
    sessionSnapshots.delete(sessionId);
    return null;
  }
  var scroller = getChatScroller();
  entry.scrollTop = scroller ? scroller.scrollTop : 0;
  entry.state = captureSessionState();
  entry.revision = revision || state.renderedRevision || null;
  // Re-insert so the least recently used session is the one evicted first.
  sessionSnapshots.delete(sessionId);
  sessionSnapshots.set(sessionId, entry);
  while (sessionSnapshots.size > SESSION_SNAPSHOT_LIMIT) {
    var oldest = sessionSnapshots.keys().next().value;
    sessionSnapshots.delete(oldest);
  }
  return entry;
}

/** Moves a saved snapshot back into #messages. False when nothing was (or may be) restored. */
function restoreSessionSnapshot(sessionId, revision) {
  if (!hasSessionSnapshot(sessionId, revision)) return false;
  var entry = sessionSnapshots.get(sessionId);
  var root = document.getElementById('messages');
  if (!root) return false;
  while (root.firstChild) {
    root.removeChild(root.firstChild);
  }
  var fragment = document.createDocumentFragment();
  while (entry.container.firstChild) {
    fragment.appendChild(entry.container.firstChild);
  }
  root.appendChild(fragment);
  applySessionState(entry.state);
  // Restored nodes kept their enhanced state, so only the lazy highlighter observer is re-armed;
  // enhanceCodeBlocks / renderMermaidBlocks are deliberately NOT re-run on this path.
  if (codeObserver) {
    var codes = root.querySelectorAll('pre > code');
    for (var i = 0; i < codes.length; i++) {
      if (!codes[i].dataset.hljsDone) codeObserver.observe(codes[i]);
    }
  }
  var scroller = getChatScroller();
  if (scroller) {
    var maxTop = Math.max(0, scroller.scrollHeight - scroller.clientHeight);
    scroller.scrollTop = Math.min(entry.scrollTop, maxTop);
  }
  state.autoScrollEnabled = isNearBottom();
  updateEmptyStateVisibility();
  return true;
}

/**
 * Stashes the outgoing session and restores the incoming one when it is a revision match.
 * Returns true when the swap happened; false means the caller must run a full replay.
 */
function switchSessionSnapshot(sessionId, revision) {
  if (!sessionId) return false;
  if (state.currentSessionId && state.currentSessionId !== sessionId) {
    saveSessionSnapshot(state.currentSessionId);
  }
  if (!restoreSessionSnapshot(sessionId, revision)) {
    return false;
  }
  state.currentSessionId = sessionId;
  state.pendingRestoreTop = null;
  return true;
}

/** Drops a session's snapshot (content changed, session closed, or a replay rebuilt the DOM). */
function invalidateSessionSnapshot(sessionId) {
  if (sessionId) {
    sessionSnapshots.delete(sessionId);
  } else {
    sessionSnapshots.clear();
  }
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
  // Markdig marks a ```mermaid fence as <pre class="mermaid"> (no <code> child). It renders
  // through renderMermaidBlocks(), and a code-block header would fight the SVG.
  if (pre.classList && pre.classList.contains('mermaid')) return;
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
