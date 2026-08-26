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
  batchTarget: null
};

function t(key) {
  return (window.__chatI18n && window.__chatI18n[key]) || key;
}

function applyChatI18n() {
  const loadOlder = document.getElementById('load-older');
  if (loadOlder) loadOlder.textContent = t('loadOlder');
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

function ensureMessageActions(row) {
  if (!row || row.querySelector('.message-actions')) return;
  const stack = row.querySelector('.message-stack');
  if (!stack) return;
  stack.appendChild(createMessageActions(row));
}

function getChatScroller() {
  return document.getElementById('chat-scroll');
}

function getMessageRoot() {
  return state.batchTarget || document.getElementById('messages');
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
  if (state.batching || (!force && (!state.autoScrollEnabled || hasActiveSelection()))) return;
  if (force) state.scrollForcePending = true;
  if (state.scrollFrame) return;
  state.scrollFrame = requestAnimationFrame(function () {
    state.scrollFrame = 0;
    const shouldForce = state.scrollForcePending;
    state.scrollForcePending = false;
    const scroller = getChatScroller();
    if (state.batching || !scroller
        || (!shouldForce && (!state.autoScrollEnabled || hasActiveSelection()))) return;
    scroller.scrollTop = scroller.scrollHeight;
  });
}

function updateEmptyStateVisibility() {
  if (state.batching) return;
  const emptyState = document.getElementById('empty-state');
  const root = document.getElementById('messages');
  if (!emptyState || !root) return;
  emptyState.style.display = root.children.length === 0 ? 'flex' : 'none';
}

function findAssistantContentNode(messageId) {
  if (!messageId) return null;
  const row = findAssistantBubbleRow(messageId);
  return row ? row.querySelector('.bubble > .message-content') : null;
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
  }
}

function applyAssistantHtml(messageId, html, createIfMissing, streaming, responseDurationMs) {
  let row = findAssistantBubbleRow(messageId);
  if (!row && createIfMissing) {
    row = createAssistantRow(messageId);
    getMessageRoot().appendChild(row);
    state.assistantStarted[messageId] = true;
    state.currentAssistantEl = row;
  }
  if (!row) return;
  // Query content on the row itself — do not re-query document (breaks DocumentFragment batches).
  applyMarkdownHtml(row.querySelector('.bubble > .message-content'), html, streaming !== true);
  if (streaming !== true) {
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

function enhanceCodeBlocks(root) {
  const scope = root || document;
  scope.querySelectorAll('.md-root pre').forEach(function (pre, index) {
    if (pre.closest('.code-block')) return;
    const code = pre.querySelector('code');
    if (!code) return;

    const raw = code.textContent || '';
    const className = code.className || '';
    const match = className.match(/language-([\w#+-]+)/i);
    const language = match ? match[1] : t('code');

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
  });
}

function resetTimeline() {
  const root = document.getElementById('messages');
  if (codeObserver) codeObserver.disconnect();
  root.innerHTML = '';
  state.currentAssistantEl = null;
  state.currentReasoningEl = null;
  state.assistantStarted = {};
  state.reasoningStarted = {};
  state.reasoningStartAt = {};
  state.reasoningFinalizedMs = {};
  state.toolCalls.clear();
}

function beginBatch() {
  state.batching = true;
  if (state.scrollFrame) cancelAnimationFrame(state.scrollFrame);
  state.scrollFrame = 0;
  state.scrollForcePending = false;
  state.pendingEnhancementRoots = [];
  document.documentElement.classList.add('replaying');
}

function endBatch(forceScroll) {
  state.batching = false;
  document.documentElement.classList.remove('replaying');
  const roots = state.pendingEnhancementRoots;
  state.pendingEnhancementRoots = [];
  roots.forEach(function (root) { enhanceCodeBlocks(root); });
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

function createUserRow(content, images, startedAt) {
  const row = document.createElement('div');
  row.className = 'message-row user';
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
    text.textContent = content;
    bubble.appendChild(text);
  }

  stack.appendChild(bubble);
  if (startedAt) stack.appendChild(createMessageMeta(startedAt));
  stack.appendChild(createMessageActions(row));
  row.appendChild(stack);
  updateCopyText(row, content || '');
  return row;
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

function appendMessage(role, content, append, images, startedAt) {
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
    getMessageRoot().appendChild(createUserRow(content, images, startedAt));
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
  if (state.currentAssistantEl && state.assistantStarted[messageId]) return;
  const row = createAssistantRow(messageId);
  getMessageRoot().appendChild(row);
  state.currentAssistantEl = row;
  state.assistantStarted[messageId] = true;
  updateEmptyStateVisibility();
}

function ensureReasoningBubble(messageId) {
  if (state.currentReasoningEl && state.reasoningStarted[messageId]) return;
  const row = createReasoningRow(messageId);
  getMessageRoot().appendChild(row);
  state.currentReasoningEl = row;
  state.reasoningStarted[messageId] = true;
  updateEmptyStateVisibility();
}

function createToolCard(toolCallId, toolName) {
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
  getMessageRoot().appendChild(row);
  state.toolCalls.set(toolCallId, details);
  updateEmptyStateVisibility();
  scrollToBottom();
}

function getToolCard(toolCallId) {
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

function findLatestFilesChangedCardInCurrentTurn() {
  var root = getMessageRoot();
  if (!root) return null;
  var rows = root.querySelectorAll('.message-row');
  var lastUser = -1;
  for (var i = 0; i < rows.length; i++) {
    if (rows[i].classList.contains('user')) lastUser = i;
  }
  var latest = null;
  for (var j = lastUser + 1; j < rows.length; j++) {
    var card = rows[j].querySelector('.files-changed-card');
    if (card) latest = card;
  }
  return latest;
}

function findFilesChangedTargetCard(upsert) {
  var live = document.querySelector('.files-changed-card[data-live="1"]');
  if (live) return live;
  if (!upsert) return null;
  // Live upsert after a timeline reload: reuse the latest unsealed card so we do not
  // stack a second card that repeats the same paths.
  var latest = findLatestFilesChangedCardInCurrentTurn();
  if (latest && !latest.hasAttribute('data-sealed')) return latest;
  return null;
}

/**
 * Place the files-changed row after the current turn's activity (or user),
 * and before the first final assistant bubble — so it scrolls with history.
 */
function placeFilesChangedRow(row) {
  var root = getMessageRoot();
  if (!root || !row) return;
  var rows = Array.prototype.slice.call(root.querySelectorAll('.message-row'));
  var lastUserIdx = -1;
  for (var i = 0; i < rows.length; i++) {
    if (rows[i].classList.contains('user')) lastUserIdx = i;
  }

  var insertAfter = lastUserIdx >= 0 ? rows[lastUserIdx] : null;
  var insertBefore = null;
  for (var j = lastUserIdx + 1; j < rows.length; j++) {
    var candidate = rows[j];
    if (candidate === row) continue;
    if (candidate.querySelector('.turn-activity')) {
      insertAfter = candidate;
      continue;
    }
    if (candidate.querySelector('.files-changed-card')) continue;
    if (candidate.classList.contains('assistant')
        && candidate.querySelector('.bubble > .message-content')) {
      insertBefore = candidate;
      break;
    }
  }

  if (insertBefore && insertBefore.parentNode === root) {
    root.insertBefore(row, insertBefore);
    return;
  }
  if (insertAfter && insertAfter.parentNode === root) {
    root.insertBefore(row, insertAfter.nextSibling);
    return;
  }
  root.appendChild(row);
}

function appendFilesChangedCard(event) {
  state.currentAssistantEl = null;
  state.currentReasoningEl = null;
  var files = event.files || [];
  if (!files.length) return;

  var existing = findFilesChangedTargetCard(event.upsert === true);
  var card = existing;
  var sealingLiveCard = !!(existing && existing.getAttribute('data-live') === '1');
  var openPaths = {};
  var row = null;
  if (existing) {
    existing.querySelectorAll('.files-changed-item.open').forEach(function (item) {
      var path = item.getAttribute('data-path') || '';
      if (path) openPaths[path] = true;
    });
    card.innerHTML = '';
    row = existing.closest('.message-row') || existing.parentNode;
  } else {
    row = document.createElement('div');
    row.className = 'message-row assistant files-changed-host';
    card = document.createElement('div');
    card.className = 'files-changed-card';
    if (event.upsert) card.setAttribute('data-live', '1');
    row.appendChild(card);
  }

  var title = document.createElement('div');
  title.className = 'files-changed-title';
  title.textContent = filesChangedTitle(files.length);
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
  if (event.upsert) {
    card.setAttribute('data-live', '1');
    card.removeAttribute('data-sealed');
  } else {
    card.removeAttribute('data-live');
    // Only mark sealed when closing a live card; replay creates plain cards so a later
    // live upsert can reuse them instead of stacking duplicates.
    if (sealingLiveCard) card.setAttribute('data-sealed', '1');
  }
  placeFilesChangedRow(row);
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

function insertAfterLastUserRow(row) {
  var root = getMessageRoot();
  var users = root.querySelectorAll('.message-row.user');
  var lastUser = users.length ? users[users.length - 1] : null;
  if (lastUser && lastUser.parentNode === root) {
    var anchor = lastUser.nextSibling;
    while (anchor && anchor.nodeType === 1 && anchor.classList.contains('turn-activity-host')) {
      anchor = anchor.nextSibling;
    }
    root.insertBefore(row, anchor);
    return;
  }
  root.appendChild(row);
}

function scrollTurnActivityThoughts(details) {
  if (!details || !details.open) return;
  details.querySelectorAll('.turn-activity-thought').forEach(function (el) {
    el.scrollTop = el.scrollHeight;
  });
}

function findLatestTurnActivityInCurrentTurn() {
  var root = getMessageRoot();
  if (!root) return null;
  var rows = root.querySelectorAll('.message-row');
  var lastUser = -1;
  for (var i = 0; i < rows.length; i++) {
    if (rows[i].classList.contains('user')) lastUser = i;
  }
  var latest = null;
  for (var j = lastUser + 1; j < rows.length; j++) {
    var card = rows[j].querySelector('.turn-activity');
    if (card) latest = card;
  }
  return latest;
}

function findTurnActivityTargetCard(upsert) {
  var live = document.querySelector('.turn-activity[data-live="1"]');
  if (live) return live;
  if (!upsert) return null;
  // After a conversation switch the replayed card has no data-live. Reuse it so
  // the next thought upsert does not stack a second fold.
  return findLatestTurnActivityInCurrentTurn();
}

function appendTurnActivityCard(event) {
  state.currentAssistantEl = null;
  state.currentReasoningEl = null;
  var items = event.items || [];
  if (!items.length && !(event.exploredFileCount || event.searchCount || event.commandCount || event.thoughtCount)) return;

  // One live card for the whole turn; sealing (upsert=false) finalizes it.
  var existing = findTurnActivityTargetCard(event.upsert === true);
  var details = existing;
  // Stay collapsed by default; keep expanded only if the user already opened it.
  var keepOpen = !!(existing && existing.open);
  if (!details) {
    var row = document.createElement('div');
    row.className = 'message-row assistant turn-activity-host';
    details = document.createElement('details');
    details.className = 'turn-activity';
    if (event.upsert) details.setAttribute('data-live', '1');
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
    insertAfterLastUserRow(row);
  } else {
    details.innerHTML = '';
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
    }

    body.appendChild(entry);
  });

  details.appendChild(body);
  // Default collapsed; live upserts preserve a user-opened fold; seal always collapses.
  if (event.upsert) {
    details.setAttribute('data-live', '1');
    details.open = keepOpen;
  } else {
    details.removeAttribute('data-live');
    details.open = false;
  }
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
  let details = document.querySelector('[data-compaction-id="' + id + '"]');
  if (!details) {
    state.currentAssistantEl = null;
    state.currentReasoningEl = null;
    const row = document.createElement('div');
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
    getMessageRoot().appendChild(row);
    updateEmptyStateVisibility();
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
      (function () {
        var liveActivity = document.querySelector('.turn-activity[data-live="1"]');
        if (liveActivity) liveActivity.removeAttribute('data-live');
        var liveFiles = document.querySelector('.files-changed-card[data-live="1"]');
        if (liveFiles) {
          liveFiles.removeAttribute('data-live');
          liveFiles.setAttribute('data-sealed', '1');
        }
      })();
      appendMessage('user', event.content || '', false, event.images || [], event.startedAt || '');
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
        event.responseDurationMs);
      updateCopyText(
        findAssistantBubbleRow(event.messageId),
        resolveEventMarkdown(event));
      if (!event.streaming) state.currentAssistantEl = null;
      break;
    case 'REMOVE_ASSISTANT_BUBBLES': {
      var ids = event.messageIds || [];
      ids.forEach(function (id) {
        var row = findAssistantBubbleRow(id);
        if (row && row.parentNode) row.parentNode.removeChild(row);
        delete state.assistantStarted[id];
      });
      state.currentAssistantEl = null;
      break;
    }
    case 'TOOL_CALL_START':
      createToolCard(event.toolCallId, event.toolCallName);
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

function replayEvents(events) {
  beginBatch();
  state.trackReasoningDuration = false;
  resetTimeline();
  for (const raw of events) {
    try {
      const event = typeof raw === 'string' ? JSON.parse(raw) : raw;
      handleEvent(event);
    } catch (e) { console.warn('replayEvents parse failed', e); }
  }
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
  for (const raw of events) {
    try {
      const event = typeof raw === 'string' ? JSON.parse(raw) : raw;
      handleEvent(event);
    } catch (e) { console.warn('appendEvents parse failed', e); }
  }
  state.batchTarget = null;
  state.trackReasoningDuration = true;
  root.appendChild(fragment);
  endBatch(true);
}

function setOlderMessagesAvailable(available) {
  const button = document.getElementById('load-older');
  if (!button) return;
  button.hidden = !available;
  button.disabled = false;
  button.textContent = t('loadOlder');
}

function prependEvents(events, hasOlderMessages) {
  const scroller = getChatScroller();
  const root = document.getElementById('messages');
  if (!scroller || !root) return;
  const previousHeight = scroller.scrollHeight;
  const previousTop = scroller.scrollTop;
  const fragment = document.createDocumentFragment();
  beginBatch();
  state.batchTarget = fragment;
  for (const raw of events) {
    try {
      const event = typeof raw === 'string' ? JSON.parse(raw) : raw;
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

function handleWebMessage(message) {
  const command = typeof message === 'string' ? JSON.parse(message) : message;
  if (!command || !command.command) return;
  if (command.command === 'replay' || command.command === 'replaceSurface') {
    replayEvents(Array.isArray(command.events) ? command.events : []);
  } else if (command.command === 'append' || command.command === 'appendEvents') {
    appendEvents(Array.isArray(command.events) ? command.events : []);
  } else if (command.command === 'prepend') {
    prependEvents(
      Array.isArray(command.events) ? command.events : [],
      !!command.hasOlderMessages);
  } else if (command.command === 'historyAvailability') {
    setOlderMessagesAvailable(!!command.hasOlderMessages);
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
  }, { passive: true });
}
const loadOlderButton = document.getElementById('load-older');
if (loadOlderButton) {
  loadOlderButton.textContent = t('loadOlder');
  loadOlderButton.addEventListener('click', function () {
    loadOlderButton.disabled = true;
    post({ type: 'loadOlder' });
  });
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

