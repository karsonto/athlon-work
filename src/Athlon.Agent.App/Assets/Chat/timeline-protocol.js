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

  // Revise: focuses the composer and switches it into revision mode, so editing the plan is
  // discoverable instead of relying on the user guessing that typing here means "revise".
  var reviseBtn = document.createElement('button');
  reviseBtn.type = 'button';
  reviseBtn.className = 'plan-card-button';
  reviseBtn.dataset.i18n = 'planRevise';
  reviseBtn.textContent = t('planRevise');
  if (built) {
    reviseBtn.disabled = true;
  } else {
    reviseBtn.addEventListener('click', function () {
      post({ type: 'planRevise' });
    });
  }
  actions.appendChild(reviseBtn);

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
  // First batch of a render stamps the start; the last batch reports the delta back to C#.
  state.replayStartAt = performance.now();
  resetTimeline();
  var list = Array.isArray(events) ? events : [];
  var maxSeq = -1;
  var parseFailures = 0;
  for (const raw of list) {
    try {
      const event = typeof raw === 'string' ? JSON.parse(raw) : raw;
      if (event && typeof event.seq === 'number' && isFinite(event.seq) && event.seq > maxSeq) {
        maxSeq = event.seq;
      }
      handleEvent(event);
    } catch (e) {
      // Reported as well as logged: a partially-parsed replay silently renders fewer rows than the
      // transcript holds, which is indistinguishable from a short conversation in the UI.
      parseFailures++;
      console.warn('replayEvents parse failed', e);
    }
  }
  // Continue the live cursor from where the replay ended so a mid-turn reload's next live card
  // does not land in a stale turn band.
  syncLiveCursorFromSeq(maxSeq);
  state.trackReasoningDuration = true;
  endBatch(true);
  // Row count after the replay is the single best page-side signal that a render did nothing:
  // events went in, and #messages came out empty.
  var root = document.getElementById('messages');
  reportRenderIssue(
    'replayApplied',
    'events=' + list.length + ' rows=' + (root ? root.children.length : -1) + ' parseFailed=' + parseFailures);
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
  var parseFailures = 0;
  for (const raw of list) {
    try {
      const event = typeof raw === 'string' ? JSON.parse(raw) : raw;
      if (event && typeof event.seq === 'number' && isFinite(event.seq) && event.seq > maxSeq) {
        maxSeq = event.seq;
      }
      handleEvent(event);
    } catch (e) {
      parseFailures++;
      console.warn('appendEvents parse failed', e);
    }
  }
  state.batchTarget = null;
  state.trackReasoningDuration = true;
  if (parseFailures > 0) {
    reportRenderIssue('appendParseFailed', 'events=' + list.length + ' parseFailed=' + parseFailures);
  }
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
    beginReplayScroll(command.sessionId);
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
  } else if (command.command === 'switchSession') {
    // Fast path: swap a previously rendered session's DOM back in. A miss (or a revision mismatch)
    // tells C# to run the normal full replay instead.
    var restored = switchSessionSnapshot(command.sessionId, command.revision);
    post({
      type: restored ? 'snapshotRestored' : 'snapshotMiss',
      sessionId: command.sessionId,
      renderGeneration: Number.isInteger(command.renderGeneration) ? command.renderGeneration : null
    });
  } else if (command.command === 'invalidateSession') {
    invalidateSessionSnapshot(command.sessionId);
  } else if (command.command === 'reset') {
    beginBatch();
    resetTimeline();
    endBatch(false);
  }
  if (command.replayComplete && Number.isInteger(command.renderGeneration)) {
    // Restore a saved scroll position when switching back to a session; a cold session left
    // pendingRestoreTop null, so its normal scroll-to-bottom already ran during the last batch.
    if (state.pendingRestoreTop !== null) {
      state.pendingRestoreTop = null;
      restoreScroll(state.currentSessionId);
    }
    // Revision this timeline was rendered from: a later switchSession must match it exactly, so any
    // content change (turn, compaction, plan, i18n) invalidates the DOM snapshot.
    state.renderedRevision = command.revision || null;
    // In-page render time for the whole replayed timeline (first batch → last batch), used by
    // C# session-switch profiling. Zero when no replay started in this page lifetime.
    var renderMs = state.replayStartAt > 0 ? performance.now() - state.replayStartAt : 0;
    state.replayStartAt = 0;
    post({
      type: 'replayComplete',
      renderGeneration: command.renderGeneration,
      renderMs: Math.max(0, Math.round(renderMs))
    });
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

