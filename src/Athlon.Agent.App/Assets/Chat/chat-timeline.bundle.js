(() => {
  // timeline-store.js
  var HEIGHT = {
    USER: 72,
    ASSISTANT: 120,
    TURN_ACTIVITY: 48,
    TOOL: 64,
    FILES_CHANGED: 40,
    COMPACTION: 56,
    PLAN: 160,
    OVERFLOW: 40,
    STATUS: 40
  };
  function estimateHeight(type, event) {
    switch (type) {
      case "USER":
        return HEIGHT.USER + (event.images && event.images.length ? 80 : 0);
      case "STATIC_ASSISTANT_HTML":
      case "ASSISTANT":
        return HEIGHT.ASSISTANT;
      case "TURN_ACTIVITY":
        return event && event.upsert ? 52 : HEIGHT.TURN_ACTIVITY;
      case "TOOL":
        return HEIGHT.TOOL;
      case "FILES_CHANGED":
        return HEIGHT.FILES_CHANGED + (event.files && event.files.length || 0) * 28;
      case "COMPACTION_CHECKPOINT":
        return HEIGHT.COMPACTION;
      case "PLAN_CLARIFY":
      case "PLAN_READY":
        return HEIGHT.PLAN;
      case "OVERFLOW_RETRY_SKIPPED":
        return HEIGHT.OVERFLOW;
      default:
        return 80;
    }
  }
  function cloneEvent(event) {
    return JSON.parse(JSON.stringify(event));
  }
  var TimelineItemStore = class _TimelineItemStore {
    constructor() {
      this.items = [];
      this.currentTurnId = null;
      this.indexById = /* @__PURE__ */ new Map();
      this.toolIndexByCallId = /* @__PURE__ */ new Map();
    }
    get count() {
      return this.items.length;
    }
    clear() {
      this.items = [];
      this.currentTurnId = null;
      this.indexById.clear();
      this.toolIndexByCallId.clear();
    }
    /** @returns {TimelineItem[]} */
    snapshot() {
      return this.items.slice();
    }
    rebuildIndex() {
      this.indexById.clear();
      this.toolIndexByCallId.clear();
      this.items.forEach((item, index) => {
        this.indexById.set(item.id, index);
        if (item.toolCallId) this.toolIndexByCallId.set(item.toolCallId, index);
        if (item.type === "TOOL" && item.event && item.event.toolCallId) {
          this.toolIndexByCallId.set(item.event.toolCallId, index);
        }
      });
    }
    /** @param {TimelineItem} item @param {number} [at] */
    insertItem(item, at) {
      const index = at == null ? this.items.length : at;
      this.items.splice(index, 0, item);
      this.rebuildIndex();
      return item;
    }
    /** @param {string} id @param {Partial<TimelineItem>} patch */
    upsertItem(id, patch) {
      const index = this.indexById.get(id);
      if (index != null) {
        const existing = this.items[index];
        Object.assign(existing, patch, { version: existing.version + 1 });
        if (patch.event) {
          existing.estimatedHeight = estimateHeight(existing.type, existing.event);
        }
        return existing;
      }
      const item = (
        /** @type {TimelineItem} */
        {
          id,
          version: 1,
          live: false,
          turnId: this.currentTurnId,
          estimatedHeight: 80,
          ...patch
        }
      );
      if (!item.estimatedHeight || item.estimatedHeight === 80) {
        item.estimatedHeight = estimateHeight(item.type, item.event);
      }
      this.insertItem(item);
      return item;
    }
    /** @param {string} id */
    removeItem(id) {
      const index = this.indexById.get(id);
      if (index == null) return;
      this.items.splice(index, 1);
      this.rebuildIndex();
    }
    /** @param {string} itemId @param {string} afterId */
    moveItemAfter(itemId, afterId) {
      const from = this.indexById.get(itemId);
      const after = this.indexById.get(afterId);
      if (from == null || after == null || from === after) return;
      const [item] = this.items.splice(from, 1);
      const newAfter = this.indexById.get(afterId);
      if (newAfter == null) {
        this.items.push(item);
      } else {
        this.items.splice(newAfter + 1, 0, item);
      }
      this.rebuildIndex();
    }
    sealLiveItems() {
      for (const item of this.items) {
        if (item.live) {
          item.live = false;
          item.version += 1;
          if (item.type === "TURN_ACTIVITY" && item.event) {
            item.event = cloneEvent(item.event);
            item.event.upsert = false;
          }
          if (item.type === "FILES_CHANGED" && item.event) {
            item.event = cloneEvent(item.event);
            item.event.upsert = false;
          }
        }
      }
    }
    /** @param {string} turnId */
    activityId(turnId) {
      return "activity:" + turnId;
    }
    /** @param {string} turnId */
    filesId(turnId) {
      return "files:" + turnId;
    }
    /** @param {string} messageId */
    assistantId(messageId) {
      return "assistant:" + messageId;
    }
    /** @param {string} toolCallId */
    toolId(toolCallId) {
      return "tool:" + toolCallId;
    }
    /** @param {AgUiEvent} event @returns {{ removedIds?: string[], remeasure?: boolean }} */
    applyEvent(event) {
      if (!event || !event.type) return {};
      switch (event.type) {
        case "RESET_TIMELINE":
          this.clear();
          return { reset: true };
        case "USER_MESSAGE": {
          this.sealLiveItems();
          const messageId = event.messageId || "user-" + this.items.length;
          this.currentTurnId = messageId;
          this.upsertItem("user:" + messageId, {
            type: "USER",
            event: cloneEvent(event),
            turnId: messageId,
            live: false,
            messageId
          });
          return { scrollBottom: true };
        }
        case "TURN_ACTIVITY": {
          const turnId = this.currentTurnId || "orphan";
          const id = this.activityId(turnId);
          this.upsertItem(id, {
            type: "TURN_ACTIVITY",
            event: cloneEvent(event),
            turnId,
            live: event.upsert === true
          });
          return { scrollBottom: true };
        }
        case "FILES_CHANGED": {
          const turnId = this.currentTurnId || "orphan";
          const id = this.filesId(turnId);
          if (!event.files || !event.files.length) {
            if (event.upsert !== true) {
              const idx = this.indexById.get(id);
              if (idx != null) {
                const item = this.items[idx];
                item.live = false;
                item.version += 1;
                item.event = cloneEvent(event);
              }
            }
            return { scrollBottom: true };
          }
          this.upsertItem(id, {
            type: "FILES_CHANGED",
            event: cloneEvent(event),
            turnId,
            live: event.upsert === true
          });
          return { scrollBottom: true };
        }
        case "STATIC_ASSISTANT_HTML": {
          const messageId = event.messageId || "";
          const id = this.assistantId(messageId);
          this.upsertItem(id, {
            type: "ASSISTANT",
            event: cloneEvent(event),
            turnId: this.currentTurnId,
            live: event.streaming === true,
            messageId
          });
          if (event.streaming !== true && this.currentTurnId) {
            const filesItemId = this.filesId(this.currentTurnId);
            if (this.indexById.has(filesItemId)) {
              this.moveItemAfter(filesItemId, id);
            }
          }
          return { scrollBottom: true };
        }
        case "REMOVE_ASSISTANT_BUBBLES": {
          const ids = event.messageIds || [];
          ids.forEach((mid) => this.removeItem(this.assistantId(mid)));
          return {};
        }
        case "TOOL_CALL_START": {
          if (event.toolCallName === "ask_plan_clarification" || event.toolCallName === "publish_plan") {
            return {};
          }
          const toolCallId = event.toolCallId || "";
          this.upsertItem(this.toolId(toolCallId), {
            type: "TOOL",
            event: cloneEvent(event),
            turnId: this.currentTurnId,
            live: true,
            toolCallId,
            toolState: { phase: "start" }
          });
          return { scrollBottom: true };
        }
        case "TOOL_CALL_ARGS":
        case "TOOL_CALL_END":
        case "TOOL_CALL_OUTPUT":
        case "TOOL_CALL_RESULT":
        case "TOOL_APPROVAL_REQUEST":
        case "TOOL_APPROVAL_RESOLVED": {
          const toolCallId = event.toolCallId || "";
          const id = this.toolId(toolCallId);
          const index = this.indexById.get(id);
          if (index == null) return { scrollBottom: true };
          const item = this.items[index];
          if (!item.toolState) item.toolState = {};
          item.toolState[event.type] = cloneEvent(event);
          item.version += 1;
          if (event.type === "TOOL_CALL_RESULT") item.live = false;
          return { scrollBottom: true, patchToolId: toolCallId };
        }
        case "COMPACTION_CHECKPOINT": {
          const cid = event.id || "compaction";
          this.upsertItem("compaction:" + cid, {
            type: "COMPACTION",
            event: cloneEvent(event),
            turnId: null,
            live: false
          });
          return { scrollBottom: true };
        }
        case "OVERFLOW_RETRY_SKIPPED": {
          this.upsertItem("overflow:" + this.items.length, {
            type: "OVERFLOW",
            event: cloneEvent(event),
            turnId: this.currentTurnId,
            live: false
          });
          return { scrollBottom: true };
        }
        case "PLAN_CLARIFY_REQUEST": {
          const requestId = event.requestId || "plan";
          this.upsertItem("plan-clarify:" + requestId, {
            type: "PLAN_CLARIFY",
            event: cloneEvent(event),
            turnId: this.currentTurnId,
            live: !event.resolved
          });
          return { scrollBottom: true };
        }
        case "PLAN_CLARIFY_RESOLVED": {
          const requestId = event.requestId || "";
          const id = "plan-clarify:" + requestId;
          const index = this.indexById.get(id);
          if (index != null) {
            const item = this.items[index];
            item.event = { ...item.event, ...cloneEvent(event), resolved: true };
            item.live = false;
            item.version += 1;
          }
          return {};
        }
        case "PLAN_READY": {
          const runId = event.runId || "plan";
          this.upsertItem("plan-ready:" + runId, {
            type: "PLAN_READY",
            event: cloneEvent(event),
            turnId: this.currentTurnId,
            live: false
          });
          return { scrollBottom: true };
        }
        default:
          return {};
      }
    }
    /**
     * Replay/prepend/append: apply events in order.
     * @param {Array<string | AgUiEvent>} rawEvents
     * @param {{ prepend?: boolean, append?: boolean }} [options]
     * - default (replay): clear then ingest
     * - append: keep existing items (used by batched replay follow-up slices)
     * - prepend: ingest into a temp store and insert at the head
     */
    ingestEvents(rawEvents, options) {
      const prepend = !!(options && options.prepend);
      const append = !!(options && options.append);
      if (!prepend && !append) {
        this.clear();
      }
      const batchStore = prepend ? new _TimelineItemStore() : this;
      for (const raw of rawEvents) {
        try {
          const event = typeof raw === "string" ? JSON.parse(raw) : raw;
          if (!event || !event.type) continue;
          if (event.type === "RESET_TIMELINE") {
            if (!prepend && !append) this.clear();
            continue;
          }
          batchStore.applyEvent(event);
        } catch (_e) {
        }
      }
      if (prepend && batchStore.items.length) {
        this.items = batchStore.items.concat(this.items);
        this.rebuildIndex();
      }
    }
    /** Estimated pixel height of the whole list (includes row gap). */
    estimateTotalSize(gap = 20) {
      let total = 0;
      for (const item of this.items) {
        total += (item.estimatedHeight || 80) + gap;
      }
      return total;
    }
  };

  // timeline-dom.js
  var state = {
    currentAssistantEl: null,
    currentReasoningEl: null,
    assistantStarted: {},
    reasoningStarted: {},
    toolCalls: /* @__PURE__ */ new Map(),
    trackReasoningDuration: true,
    reasoningStartAt: {},
    reasoningFinalizedMs: {},
    batching: false,
    pendingEnhancementRoots: [],
    scrollFrame: 0,
    scrollForcePending: false,
    autoScrollEnabled: true,
    batchTarget: null,
    virtualRender: false,
    // When the virtualizer applies an incremental event to a row that is already
    // mounted, this points at that row. Card builders update it in place instead of
    // scanning the document or creating new rows (order is owned by the store).
    patchRow: null
  };
  function targetHostRow() {
    return state.patchRow || null;
  }
  function isFragmentBuild() {
    return state.virtualRender === true;
  }
  function t(key) {
    return window.__chatI18n && window.__chatI18n[key] || key;
  }
  function applyChatI18n() {
    const loadOlder = document.getElementById("load-older");
    if (loadOlder) loadOlder.textContent = t("loadOlder");
    document.querySelectorAll(".code-btn").forEach(function(btn) {
      if (btn.classList.contains("copied")) return;
      if (btn.dataset.i18n === "preview") {
        btn.textContent = t("preview");
        return;
      }
      btn.textContent = t("copy");
    });
    document.querySelectorAll("[data-i18n]").forEach(function(element) {
      element.textContent = t(element.dataset.i18n);
    });
    document.querySelectorAll(".reasoning-label").forEach(function(label) {
      const row = label.closest(".reasoning-row");
      const messageId = row && row.dataset.messageId;
      if (messageId && state.reasoningFinalizedMs[messageId] !== void 0) {
        finalizeReasoningLabel(messageId);
      } else if (messageId && state.reasoningStartAt[messageId]) {
        updateReasoningThinkingLabel(messageId);
      } else if (!label.textContent || label.textContent.indexOf("\u601D\u8003") >= 0 || label.textContent.indexOf("Think") >= 0) {
        label.textContent = t("thinking");
      }
    });
  }
  function cssEscape(value) {
    if (window.CSS && typeof CSS.escape === "function") return CSS.escape(String(value));
    return String(value).replace(/\\/g, "\\\\").replace(/"/g, '\\"');
  }
  function decodeBase64Utf8(b64) {
    const binary = atob(b64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return new TextDecoder("utf-8").decode(bytes);
  }
  function resolveEventMarkdown(event) {
    if (event && event.markdownB64) return decodeBase64Utf8(event.markdownB64);
    if (event && event.markdown) return event.markdown;
    if (event && event.content) return event.content;
    return "";
  }
  function resolveEventHtml(event) {
    if (event && event.htmlB64) return decodeBase64Utf8(event.htmlB64);
    return event && event.html || "";
  }
  function resolveRenderedHtml(event, fallbackText) {
    const html = resolveEventHtml(event);
    if (html) return html;
    return "<pre>" + escapeHtml(resolveEventMarkdown(event) || fallbackText || "") + "</pre>";
  }
  function escapeHtml(text) {
    const div = document.createElement("div");
    div.textContent = text == null ? "" : String(text);
    return div.innerHTML;
  }
  function post(payload) {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(payload);
    }
  }
  var pendingToolDetailRequests = /* @__PURE__ */ Object.create(null);
  var toolDetailRequestSeq = 0;
  function requestToolDetailForEntry(entry, detailPanel) {
    if (!entry || entry.dataset.hydrated === "1" || entry.dataset.loading === "1") return;
    var messageId = entry.dataset.messageId || "";
    var toolCallId = entry.dataset.toolCallId || "";
    if (!messageId && !toolCallId) return;
    entry.dataset.loading = "1";
    if (detailPanel) detailPanel.textContent = "\u2026";
    var requestId = "td-" + ++toolDetailRequestSeq;
    pendingToolDetailRequests[requestId] = { entry, panel: detailPanel };
    post({
      type: "requestToolDetail",
      requestId,
      messageId: messageId || null,
      toolCallId: toolCallId || null
    });
  }
  function applyToolDetailPayload(payload) {
    if (!payload) return;
    var requestId = payload.requestId || "";
    var pending = requestId ? pendingToolDetailRequests[requestId] : null;
    var text = payload.content || "";
    if (pending) {
      delete pendingToolDetailRequests[requestId];
      if (pending.panel) {
        pending.panel.textContent = text || "(empty)";
      } else if (pending.entry && pending.entry.classList.contains("tool")) {
        var result = pending.entry.querySelector(".tool-result");
        var html = pending.entry.querySelector(".tool-result-html");
        if (result && html) {
          result.style.display = "block";
          html.textContent = text;
        }
      }
      if (pending.entry) {
        pending.entry.dataset.hydrated = "1";
        delete pending.entry.dataset.loading;
      }
      return;
    }
    var toolCallId = payload.toolCallId || "";
    if (toolCallId) {
      var card = getToolCard(toolCallId);
      if (card) {
        var cardResult = card.querySelector(".tool-result");
        var cardHtml = card.querySelector(".tool-result-html");
        if (cardResult && cardHtml) {
          cardResult.style.display = "block";
          cardHtml.textContent = text;
        }
        card.dataset.hydrated = "1";
        delete card.dataset.loading;
        if (payload.messageId) card.dataset.messageId = payload.messageId;
      }
    }
    if (payload.messageId) {
      var entries = document.querySelectorAll(
        '.turn-activity-item[data-message-id="' + payload.messageId + '"]'
      );
      entries.forEach(function(entry) {
        var panel = entry.querySelector(".turn-activity-tool-detail");
        if (panel) panel.textContent = text || "(empty)";
        entry.dataset.hydrated = "1";
        delete entry.dataset.loading;
      });
    }
  }
  function requestToolDetailForToolCard(card) {
    if (!card || card.dataset.hydrated === "1" || card.dataset.loading === "1") return;
    var messageId = card.dataset.messageId || "";
    var toolCallId = card.getAttribute("data-tool-call-id") || "";
    if (!messageId && !toolCallId) return;
    card.dataset.loading = "1";
    var requestId = "td-" + ++toolDetailRequestSeq;
    pendingToolDetailRequests[requestId] = { entry: card, panel: null };
    post({
      type: "requestToolDetail",
      requestId,
      messageId: messageId || null,
      toolCallId: toolCallId || null
    });
  }
  var copyIconSvg = '<svg width="16" height="16" viewBox="0 0 16 16" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true"><rect x="5" y="5" width="9" height="9" rx="1.5" stroke="currentColor" stroke-width="1.25"></rect><rect x="2" y="2" width="9" height="9" rx="1.5" stroke="currentColor" stroke-width="1.25" fill="var(--chat-bg)"></rect></svg>';
  function createCopyButton(onCopy) {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "message-action-btn";
    btn.setAttribute("aria-label", t("copy"));
    btn.innerHTML = copyIconSvg;
    btn.addEventListener("click", function(e) {
      e.preventDefault();
      e.stopPropagation();
      onCopy(btn);
    });
    return btn;
  }
  function copyMessageText(text, button) {
    if (!text) return;
    post({ type: "copy", text });
    if (!button) return;
    button.classList.add("copied");
    button.setAttribute("aria-label", t("copied"));
    setTimeout(function() {
      button.classList.remove("copied");
      button.setAttribute("aria-label", t("copy"));
    }, 1600);
  }
  function resolveRowCopyText(row) {
    if (!row) return "";
    if (row.dataset.copyText) return row.dataset.copyText;
    const userText = row.querySelector(".user-text");
    if (userText) return userText.textContent || "";
    const content = row.querySelector(".message-content");
    return content ? content.innerText || "" : "";
  }
  function updateCopyText(row, text) {
    if (!row) return;
    row.dataset.copyText = text == null ? "" : String(text);
  }
  function createMessageActions(row) {
    const actions = document.createElement("div");
    actions.className = "message-actions";
    actions.appendChild(createCopyButton(function(button) {
      copyMessageText(resolveRowCopyText(row), button);
    }));
    return actions;
  }
  function getChatScroller() {
    return document.getElementById("chat-scroll");
  }
  function getMessageRoot() {
    return state.batchTarget || document.getElementById("messages");
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
    if (state.batching || !force && (!state.autoScrollEnabled || hasActiveSelection())) return;
    if (force) state.scrollForcePending = true;
    if (state.scrollFrame) return;
    state.scrollFrame = requestAnimationFrame(function() {
      state.scrollFrame = 0;
      const shouldForce = state.scrollForcePending;
      state.scrollForcePending = false;
      const scroller = getChatScroller();
      if (state.batching || !scroller || !shouldForce && (!state.autoScrollEnabled || hasActiveSelection())) return;
      scroller.scrollTop = scroller.scrollHeight;
    });
  }
  function updateEmptyStateVisibility() {
    if (state.batching) return;
    const emptyState = document.getElementById("empty-state");
    const windowEl = document.getElementById("virtual-window");
    if (!emptyState || !windowEl) return;
    emptyState.style.display = windowEl.children.length === 0 ? "flex" : "none";
  }
  function findAssistantBubbleRow(messageId) {
    if (!messageId) return null;
    const selector = '.message-row.assistant-row[data-message-id="' + cssEscape(messageId) + '"]';
    const root = getMessageRoot();
    if (root && root.querySelector) {
      const inRoot = root.querySelector(selector);
      if (inRoot) return inRoot;
    }
    return document.querySelector(selector);
  }
  function applyMarkdownHtml(node, html, enhance) {
    if (!node) return;
    node.classList.add("md-root");
    node.innerHTML = html || "";
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
    applyMarkdownHtml(row.querySelector(".bubble > .message-content"), html, streaming !== true);
    if (streaming !== true) {
      setMessageMeta(row, formatResponseDuration(responseDurationMs));
    }
    updateEmptyStateVisibility();
    scrollToBottom();
  }
  var codeObserver = typeof IntersectionObserver === "function" ? new IntersectionObserver(function(entries, observer) {
    entries.forEach(function(entry) {
      if (!entry.isIntersecting) return;
      const code = entry.target;
      observer.unobserve(code);
      if (typeof hljs !== "undefined" && !code.dataset.hljsDone) {
        try {
          hljs.highlightElement(code);
          code.dataset.hljsDone = "1";
        } catch (e) {
        }
      }
    });
  }, { root: document.getElementById("chat-scroll"), rootMargin: "200px 0px" }) : null;
  function enhanceCodeBlocks(root) {
    const scope = root || document;
    scope.querySelectorAll(".md-root pre").forEach(function(pre, index) {
      if (pre.closest(".code-block")) return;
      const code = pre.querySelector("code");
      if (!code) return;
      const raw = code.textContent || "";
      const className = code.className || "";
      const match = className.match(/language-([\w#+-]+)/i);
      const language = match ? match[1] : t("code");
      const wrapper = document.createElement("div");
      wrapper.className = "code-block";
      const header = document.createElement("div");
      header.className = "code-block-header";
      const label = document.createElement("span");
      label.textContent = language;
      const actions = document.createElement("div");
      actions.className = "code-block-actions";
      const langKey = (match ? match[1] : "").toLowerCase();
      if (langKey === "html" || langKey === "htm") {
        const previewBtn = document.createElement("button");
        previewBtn.type = "button";
        previewBtn.className = "code-btn";
        previewBtn.dataset.i18n = "preview";
        previewBtn.textContent = t("preview");
        previewBtn.addEventListener("click", function() {
          post({ type: "preview", html: raw });
        });
        actions.appendChild(previewBtn);
      }
      const copyBtn = document.createElement("button");
      copyBtn.type = "button";
      copyBtn.className = "code-btn";
      copyBtn.textContent = t("copy");
      copyBtn.addEventListener("click", function() {
        post({ type: "copy", text: raw, blockId: String(index) });
        copyBtn.textContent = t("copied");
        copyBtn.classList.add("copied");
        setTimeout(function() {
          copyBtn.textContent = t("copy");
          copyBtn.classList.remove("copied");
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
      } else if (typeof hljs !== "undefined" && !code.dataset.hljsDone) {
        try {
          hljs.highlightElement(code);
          code.dataset.hljsDone = "1";
        } catch (e) {
        }
      }
    });
  }
  function resetTimeline() {
    const windowEl = document.getElementById("virtual-window");
    if (codeObserver) codeObserver.disconnect();
    if (windowEl) windowEl.innerHTML = "";
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
    document.documentElement.classList.add("replaying");
  }
  function endBatch(forceScroll) {
    state.batching = false;
    document.documentElement.classList.remove("replaying");
    const roots = state.pendingEnhancementRoots;
    state.pendingEnhancementRoots = [];
    roots.forEach(function(root) {
      enhanceCodeBlocks(root);
    });
    updateEmptyStateVisibility();
    scrollToBottom(!!forceScroll);
  }
  function formatReasoningSeconds(ms) {
    return t("seconds").replace("{0}", String(Math.max(1, Math.round(ms / 1e3))));
  }
  function findReasoningRow(messageId) {
    if (state.currentReasoningEl && String(state.currentReasoningEl.dataset.messageId || "") === String(messageId || "")) {
      return state.currentReasoningEl;
    }
    if (!messageId) return null;
    return document.querySelector('.reasoning-row[data-message-id="' + cssEscape(messageId) + '"]');
  }
  function setReasoningLabelOnRow(row, text) {
    if (!row) return;
    const label = row.querySelector(".reasoning-label");
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
      setReasoningLabel(messageId, t("thinking"));
      return;
    }
    setReasoningLabel(
      messageId,
      t("thinking") + " (" + formatReasoningSeconds(getReasoningElapsedMs(messageId)) + ")"
    );
  }
  function finalizeReasoningLabel(messageId) {
    if (!messageId) return;
    const row = findReasoningRow(messageId);
    if (!row) return;
    if (!state.trackReasoningDuration) {
      setReasoningLabelOnRow(row, t("thought"));
      delete state.reasoningStartAt[messageId];
      delete state.reasoningFinalizedMs[messageId];
      return;
    }
    if (state.reasoningFinalizedMs[messageId] !== void 0) {
      return;
    }
    const ms = getReasoningElapsedMs(messageId);
    state.reasoningFinalizedMs[messageId] = ms;
    setReasoningLabelOnRow(row, t("thought") + " (" + formatReasoningSeconds(ms) + ")");
    delete state.reasoningStartAt[messageId];
  }
  function openImagePreview(url, fileName) {
    var lightbox = document.getElementById("image-lightbox");
    if (!lightbox || !url) return;
    var img = lightbox.querySelector(".image-lightbox-img");
    if (img) {
      img.src = url;
      img.alt = fileName || "";
    }
    lightbox.hidden = false;
    document.body.style.overflow = "hidden";
  }
  function closeImagePreview() {
    var lightbox = document.getElementById("image-lightbox");
    if (!lightbox) return;
    lightbox.hidden = true;
    var img = lightbox.querySelector(".image-lightbox-img");
    if (img) {
      img.removeAttribute("src");
      img.alt = "";
    }
    document.body.style.overflow = "";
  }
  function createMessageMeta(text) {
    const meta = document.createElement("div");
    meta.className = "message-meta";
    meta.textContent = text || "";
    return meta;
  }
  function setMessageMeta(row, text) {
    if (!row) return;
    const stack = row.querySelector(".message-stack");
    if (!stack) return;
    let meta = stack.querySelector(".message-meta");
    if (!text) {
      if (meta && meta.parentNode) meta.parentNode.removeChild(meta);
      return;
    }
    if (!meta) {
      meta = createMessageMeta(text);
      const actions = stack.querySelector(".message-actions");
      if (actions) stack.insertBefore(meta, actions);
      else stack.appendChild(meta);
    } else {
      meta.textContent = text;
    }
  }
  function formatResponseDuration(durationMs) {
    if (!durationMs || durationMs <= 0) return "";
    var secondsLabel = formatReasoningSeconds(durationMs);
    return (t("responseDuration") || "Took {0}").replace("{0}", secondsLabel);
  }
  function createUserRow(content, images, startedAt, mentions) {
    const row = document.createElement("div");
    row.className = "message-row user";
    const stack = document.createElement("div");
    stack.className = "message-stack";
    const bubble = document.createElement("div");
    bubble.className = "bubble";
    if (images && images.length) {
      const gallery = document.createElement("div");
      gallery.className = "user-images";
      images.forEach(function(image) {
        if (!image || !image.url) return;
        const thumb = document.createElement("img");
        thumb.className = "user-image-thumb";
        thumb.src = image.url;
        thumb.alt = image.fileName || "";
        thumb.title = image.fileName || "";
        thumb.addEventListener("click", function(e) {
          e.preventDefault();
          e.stopPropagation();
          openImagePreview(image.url, image.fileName);
        });
        gallery.appendChild(thumb);
      });
      if (gallery.childNodes.length) bubble.appendChild(gallery);
    }
    if (content) {
      const text = document.createElement("div");
      text.className = "message-content user-text";
      fillUserText(text, content, mentions);
      bubble.appendChild(text);
    }
    stack.appendChild(bubble);
    if (startedAt) stack.appendChild(createMessageMeta(startedAt));
    stack.appendChild(createMessageActions(row));
    row.appendChild(stack);
    updateCopyText(row, content || "");
    return row;
  }
  var FILE_CHIP_DOCUMENT = "M4,2 L4,14 L12,14 L12,6 L8,6 L6,4 L4,4 Z M6,4 L6,6 L8,6";
  var FILE_CHIP_FOLDER = "M2,5 L2,14 L14,14 L14,7 L9,7 L7,5 Z";
  var FILE_CHIP_GIT = "M8,2 C5.2,2 3,4.2 3,7 C3,9.8 5.2,12 8,12 C10.8,12 13,9.8 13,7 C13,4.2 10.8,2 8,2 Z M8,4.5 L9.8,6.3 L7.5,8.6 L6.2,7.3 L8,5.5 Z";
  var FILE_CHIP_MSBUILD = "M3,12 L6,4 L9,12 M11,4 L14,12 M10.5,10 L11.5,10";
  var FILE_CHIP_BADGES = {
    CSharp: "C#",
    Project: "P",
    Solution: "S",
    Markdown: "M",
    Json: "{}",
    Xml: "</>",
    Html: "<>",
    Css: "#",
    JavaScript: "JS",
    TypeScript: "TS",
    Python: "Py",
    Shell: "\u25B6",
    PowerShell: ">_",
    Yaml: "Y",
    Docker: "D",
    Image: "\u25A3",
    Config: "cfg"
  };
  function fileChipColorVar(kind) {
    var key = String(kind || "File").toLowerCase();
    if (key === "powershell") key = "shell";
    return "var(--file-icon-" + key + ", var(--file-icon-file))";
  }
  function createFileChipIcon(kind) {
    var wrap = document.createElement("span");
    wrap.className = "file-chip-icon";
    wrap.setAttribute("aria-hidden", "true");
    wrap.style.color = fileChipColorVar(kind);
    var svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("viewBox", "0 0 16 16");
    var path = document.createElementNS("http://www.w3.org/2000/svg", "path");
    var normalized = String(kind || "File");
    if (normalized === "Folder") {
      path.setAttribute("d", FILE_CHIP_FOLDER);
      path.setAttribute("fill", "currentColor");
    } else if (normalized === "Git") {
      path.setAttribute("d", FILE_CHIP_GIT);
      path.setAttribute("fill", "currentColor");
    } else if (normalized === "MsBuild") {
      path.setAttribute("d", FILE_CHIP_MSBUILD);
      path.setAttribute("fill", "none");
      path.setAttribute("stroke", "currentColor");
      path.setAttribute("stroke-width", "1.15");
      path.setAttribute("stroke-linecap", "round");
    } else {
      path.setAttribute("d", FILE_CHIP_DOCUMENT);
      path.setAttribute("fill", "currentColor");
      if (FILE_CHIP_BADGES[normalized]) path.setAttribute("opacity", "0.35");
      else if (normalized === "Placeholder") path.setAttribute("opacity", "0.7");
    }
    svg.appendChild(path);
    wrap.appendChild(svg);
    var badgeText = FILE_CHIP_BADGES[normalized];
    if (badgeText) {
      var badge = document.createElement("span");
      badge.className = "file-chip-badge" + (badgeText.length > 2 ? " is-long" : "");
      badge.textContent = badgeText;
      wrap.appendChild(badge);
    }
    return wrap;
  }
  function createFileChip(mention) {
    var kind = String(mention && mention.kind || "file").toLowerCase();
    if (kind === "skill" || kind === "mcp") {
      return createTypedMentionChip(kind, mention);
    }
    var chip = document.createElement("span");
    chip.className = "file-chip";
    var path = mention && (mention.path || mention.fileName) || "";
    if (path) chip.title = path;
    chip.appendChild(createFileChipIcon(mention && mention.iconKind));
    var name = document.createElement("span");
    name.className = "file-chip-name";
    name.textContent = mention && mention.fileName || path;
    chip.appendChild(name);
    return chip;
  }
  function createTypedMentionChip(kind, mention) {
    var chip = document.createElement("span");
    chip.className = "file-chip file-chip-" + kind;
    var path = mention && (mention.path || mention.fileName) || "";
    var insert = kind === "skill" ? "//skill:" + path : "//mcp:" + path;
    chip.title = insert;
    var type = document.createElement("span");
    type.className = "file-chip-type";
    type.textContent = kind === "skill" ? "\u6280\u80FD" : "MCP";
    chip.appendChild(type);
    var name = document.createElement("span");
    name.className = "file-chip-name";
    name.textContent = mention && mention.fileName || path;
    chip.appendChild(name);
    return chip;
  }
  function fillUserText(el, content, mentions) {
    var text = content || "";
    var items = Array.isArray(mentions) ? mentions.slice().sort(function(a, b) {
      return (a.start || 0) - (b.start || 0);
    }) : [];
    if (!items.length) {
      el.textContent = text;
      return;
    }
    var last = 0;
    items.forEach(function(mention) {
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
    const row = document.createElement("div");
    row.className = "message-row assistant assistant-row";
    row.dataset.messageId = messageId || "";
    const stack = document.createElement("div");
    stack.className = "message-stack";
    const bubble = document.createElement("div");
    bubble.className = "bubble";
    const content = document.createElement("div");
    content.className = "message-content md-root";
    bubble.appendChild(content);
    stack.appendChild(bubble);
    stack.appendChild(createMessageActions(row));
    row.appendChild(stack);
    return row;
  }
  function createReasoningRow(messageId) {
    const row = document.createElement("div");
    row.className = "message-row assistant reasoning-row";
    row.dataset.messageId = messageId || "";
    row.innerHTML = '<details class="reasoning-block" open><summary><span class="reasoning-chevron">\u203A</span><span class="reasoning-label">' + t("thinking") + '</span></summary><div class="reasoning-content message-content"></div></details>';
    return row;
  }
  function appendMessage(role, content, append, images, startedAt, mentions) {
    if (append && role === "assistant" && state.currentAssistantEl) {
      const el = state.currentAssistantEl.querySelector(".message-content");
      el.textContent += content;
      scrollToBottom();
      return;
    }
    if (append && role === "reasoning" && state.currentReasoningEl) {
      const el = state.currentReasoningEl.querySelector(".reasoning-content");
      el.textContent += content;
      scrollToBottom();
      return;
    }
    if (role === "user") {
      getMessageRoot().appendChild(createUserRow(content, images, startedAt, mentions));
    } else if (role === "assistant") {
      const row = createAssistantRow("");
      row.querySelector(".message-content").textContent = content;
      getMessageRoot().appendChild(row);
      state.currentAssistantEl = row;
    } else if (role === "reasoning") {
      const row = createReasoningRow("");
      row.querySelector(".reasoning-content").textContent = content;
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
  function createToolCard(toolCallId, toolName) {
    state.currentAssistantEl = null;
    state.currentReasoningEl = null;
    const row = document.createElement("div");
    row.className = "message-row assistant tool-row";
    const details = document.createElement("details");
    details.className = "message tool";
    details.dataset.toolCallId = toolCallId;
    details.innerHTML = "<summary><span>" + escapeHtml(toolName || "unknown") + '</span><span class="tool-status running">running</span></summary><div class="tool-body"><div class="tool-section-label">arguments</div><pre class="tool-pre tool-args"></pre><div class="tool-result" style="display:none"><div class="tool-section-label">result</div><div class="tool-result-html md-root"></div></div></div>';
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
    const normalized = (status || "succeeded").toLowerCase();
    if (normalized === "awaiting_approval") {
      badge.textContent = t("approvalPending");
      badge.className = "tool-status running";
      return;
    }
    if (normalized === "approval_denied") {
      badge.textContent = t("deniedStatus");
      badge.className = "tool-status failed";
      return;
    }
    const cssClass = normalized === "succeeded" || normalized === "success" ? "success" : normalized === "failed" || normalized === "failure" ? "failed" : normalized === "cancelled" || normalized === "canceled" ? "cancelled" : normalized === "running" ? "running" : normalized === "preparing" ? "running" : "success";
    const label = cssClass === "success" ? "success" : cssClass === "failed" ? "failed" : cssClass === "cancelled" ? "cancelled" : cssClass === "running" ? "running" : normalized;
    badge.textContent = label;
    badge.className = "tool-status " + cssClass;
  }
  function ensureToolApprovalPanel(card, event) {
    const body = card.querySelector(".tool-body");
    if (!body) return null;
    let panel = body.querySelector(".tool-approval");
    if (panel) return panel;
    panel = document.createElement("div");
    panel.className = "tool-approval";
    body.prepend(panel);
    const title = document.createElement("div");
    title.className = "tool-approval-title";
    title.dataset.i18n = "approvalTitle";
    title.textContent = t("approvalTitle");
    panel.appendChild(title);
    const description = document.createElement("div");
    description.className = "tool-approval-description";
    description.dataset.i18n = "approvalDescription";
    description.textContent = t("approvalDescription");
    panel.appendChild(description);
    const argumentsPre = document.createElement("pre");
    argumentsPre.className = "tool-pre tool-approval-arguments";
    panel.appendChild(argumentsPre);
    const actions = document.createElement("div");
    actions.className = "tool-approval-actions";
    const deny = document.createElement("button");
    deny.type = "button";
    deny.className = "tool-approval-button deny";
    deny.dataset.i18n = "deny";
    deny.textContent = t("deny");
    const approve = document.createElement("button");
    approve.type = "button";
    approve.className = "tool-approval-button approve";
    approve.dataset.i18n = "approve";
    approve.textContent = t("approve");
    function submit(approved) {
      deny.disabled = true;
      approve.disabled = true;
      post({ type: "toolApproval", toolCallId: event.toolCallId, approved });
    }
    deny.addEventListener("click", function() {
      submit(false);
    });
    approve.addEventListener("click", function() {
      submit(true);
    });
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
    card.dataset.awaitingApproval = "true";
    const badge = card.querySelector(".tool-status");
    if (badge) {
      badge.textContent = t("approvalPending");
      badge.className = "tool-status running";
    }
    const panel = ensureToolApprovalPanel(card, event);
    const argumentsPre = panel && panel.querySelector(".tool-approval-arguments");
    if (argumentsPre) argumentsPre.textContent = event.arguments || "";
    const argsPre = card.querySelector(".tool-args");
    if (argsPre && event.arguments) argsPre.textContent = event.arguments;
    scrollToBottom(true);
  }
  function resolveToolApproval(event) {
    const card = getToolCard(event.toolCallId);
    const panel = card && card.querySelector(".tool-approval");
    if (!card || !panel) return;
    delete card.dataset.awaitingApproval;
    const badge = card.querySelector(".tool-status");
    if (badge) {
      badge.textContent = t(event.approved ? "allowedStatus" : "deniedStatus");
      badge.className = "tool-status " + (event.approved ? "success" : "failed");
    }
    const actions = panel.querySelector(".tool-approval-actions");
    if (actions) actions.remove();
    let result = panel.querySelector(".tool-approval-result");
    if (!result) {
      result = document.createElement("div");
      result.className = "tool-approval-result";
      panel.appendChild(result);
    }
    const decisionKey = event.approved ? "approved" : "denied";
    result.dataset.i18n = decisionKey;
    result.textContent = t(decisionKey);
    result.className = "tool-approval-result " + decisionKey;
  }
  function renderDiffLines(lines) {
    if (!lines || !lines.length) {
      return '<div class="diff-empty">' + escapeHtml(t("noDiffAvailable")) + "</div>";
    }
    return lines.map(function(line) {
      var kind = (line.kind || "").toLowerCase();
      if (kind === "collapsed") {
        var label = (t("unmodifiedLines") || "{0} unmodified lines").replace("{0}", String(line.count || 0));
        return '<div class="diff-line collapsed">' + escapeHtml(label) + "</div>";
      }
      var css = kind === "added" ? "add" : kind === "removed" ? "del" : kind === "hunkheader" ? "hunk" : kind === "header" ? "header" : "ctx";
      var prefix = kind === "added" ? "+" : kind === "removed" ? "-" : kind === "hunkheader" || kind === "header" ? "" : " ";
      return '<div class="diff-line ' + css + '"><span class="diff-line-prefix">' + escapeHtml(prefix) + '</span><span class="diff-line-text">' + escapeHtml(line.text || "") + "</span></div>";
    }).join("");
  }
  function filesChangedTitle(count) {
    if (count === 1) return t("filesChangedOne") || "1 File Changed";
    return (t("filesChangedMany") || "{0} Files Changed").replace("{0}", String(count));
  }
  function joinSummaryParts(parts) {
    if (!parts.length) return "";
    if (parts.length === 1) return parts[0];
    if (parts.length === 2) return parts[0] + ", " + parts[1];
    return parts.slice(0, -1).join(", ") + ", " + parts[parts.length - 1];
  }
  function turnActivitySummaryText(event) {
    var parts = [];
    var explored = event.exploredFileCount || 0;
    var searches = event.searchCount || 0;
    var commands = event.commandCount || 0;
    var thoughts = event.thoughtCount || 0;
    if (explored === 1) parts.push(t("exploredFilesOne") || "explored 1 file");
    else if (explored > 1) parts.push((t("exploredFilesMany") || "explored {0} files").replace("{0}", String(explored)));
    if (searches === 1) parts.push(t("searchesOne") || "1 search");
    else if (searches > 1) parts.push((t("searchesMany") || "{0} searches").replace("{0}", String(searches)));
    if (commands === 1) parts.push(t("commandsOne") || "ran 1 command");
    else if (commands > 1) parts.push((t("commandsMany") || "ran {0} commands").replace("{0}", String(commands)));
    var joined = joinSummaryParts(parts);
    if (joined) return joined;
    if (thoughts === 1) return t("thoughtsOne") || "Thought";
    if (thoughts > 1) return (t("thoughtsMany") || "{0} thoughts").replace("{0}", String(thoughts));
    var items = event.items || [];
    if (items.length) {
      var first = items[0] || {};
      var line = ((first.verb || "") + " " + (first.detail || first.path || "")).trim();
      if (line) {
        if (items.length === 1) return line;
        return line.replace(/…+\s*$/u, "") + "\u2026";
      }
    }
    return t("thinking") || "Working\u2026";
  }
  function resolveFilesChangedCard(event) {
    var files = event.files || [];
    var hostRow = targetHostRow();
    var row = null;
    var card = null;
    var wasLive = false;
    var isNew = false;
    if (hostRow) {
      row = hostRow;
      card = hostRow.querySelector(":scope > .files-changed-card");
      if (card) {
        wasLive = card.getAttribute("data-live") === "1";
      } else if (files.length) {
        card = document.createElement("div");
        card.className = "files-changed-card";
        hostRow.appendChild(card);
        isNew = true;
      }
    } else if (isFragmentBuild() && files.length) {
      row = document.createElement("div");
      row.className = "message-row assistant files-changed-host";
      card = document.createElement("div");
      card.className = "files-changed-card";
      row.appendChild(card);
      getMessageRoot().appendChild(row);
      isNew = true;
    }
    return row && card ? { row, card, wasLive, isNew } : null;
  }
  function sealFilesChangedCard(card) {
    if (!card) return;
    card.removeAttribute("data-live");
    card.setAttribute("data-sealed", "1");
    updateEmptyStateVisibility();
    scrollToBottom();
  }
  function appendFilesChangedCard(event) {
    state.currentAssistantEl = null;
    state.currentReasoningEl = null;
    var files = event.files || [];
    var upsert = event.upsert === true;
    if (!files.length) {
      if (!upsert) {
        var resolved = resolveFilesChangedCard(event);
        if (resolved && resolved.wasLive) sealFilesChangedCard(resolved.card);
      }
      return;
    }
    var target = resolveFilesChangedCard(event);
    if (!target) return;
    var card = target.card;
    var openPaths = {};
    if (!target.isNew) {
      card.querySelectorAll(".files-changed-item.open").forEach(function(item) {
        var path = item.getAttribute("data-path") || "";
        if (path) openPaths[path] = true;
      });
    }
    card.innerHTML = "";
    var title = document.createElement("div");
    title.className = "files-changed-title";
    title.textContent = filesChangedTitle(files.length);
    card.appendChild(title);
    var list = document.createElement("div");
    list.className = "files-changed-list";
    files.forEach(function(file) {
      var item = document.createElement("div");
      item.className = "files-changed-item";
      item.setAttribute("data-path", file.path || "");
      if (openPaths[file.path || ""]) item.classList.add("open");
      var button = document.createElement("button");
      button.type = "button";
      button.className = "files-changed-row";
      button.title = file.path || file.displayName || "";
      var name = document.createElement("span");
      name.className = "files-changed-name";
      name.textContent = file.displayName || file.path || "";
      button.appendChild(name);
      var counts = document.createElement("span");
      counts.className = "files-changed-counts";
      if ((file.added || 0) > 0) {
        var a = document.createElement("span");
        a.className = "turn-activity-add";
        a.textContent = "+" + file.added;
        counts.appendChild(a);
      }
      if ((file.removed || 0) > 0) {
        var d = document.createElement("span");
        d.className = "turn-activity-del";
        d.textContent = "-" + file.removed;
        counts.appendChild(d);
      }
      button.appendChild(counts);
      item.appendChild(button);
      var diff = document.createElement("div");
      diff.className = "files-changed-diff";
      diff.innerHTML = renderDiffLines(file.lines || []);
      button.addEventListener("click", function(e) {
        e.preventDefault();
        e.stopPropagation();
        item.classList.toggle("open");
        scrollToBottom();
      });
      item.appendChild(diff);
      list.appendChild(item);
    });
    card.appendChild(list);
    if (upsert) {
      card.setAttribute("data-live", "1");
      card.removeAttribute("data-sealed");
    } else {
      card.removeAttribute("data-live");
      if (target.wasLive) card.setAttribute("data-sealed", "1");
    }
    updateEmptyStateVisibility();
    scrollToBottom();
  }
  function formatWorkedFor(durationMs) {
    if (!durationMs || durationMs <= 0) return "";
    var secondsLabel = formatReasoningSeconds(durationMs);
    return (t("workedFor") || "Worked for {0}").replace("{0}", secondsLabel);
  }
  function syncTurnActivityChevron(details) {
    if (!details) return;
    var chevron = details.querySelector(".turn-activity-chevron");
    if (!chevron) return;
    chevron.textContent = details.open ? "\u2228" : "\u203A";
  }
  function createTurnActivityDetails() {
    const details = document.createElement("details");
    details.className = "turn-activity";
    details.addEventListener("toggle", function() {
      syncTurnActivityChevron(details);
      if (details.open) {
        details.classList.add("is-expanded");
        scrollTurnActivityThoughts(details);
        scrollToBottom();
      } else {
        details.classList.remove("is-expanded");
      }
    });
    return details;
  }
  function scrollTurnActivityThoughts(details) {
    if (!details || !details.open) return;
    details.querySelectorAll(".turn-activity-thought").forEach(function(el) {
      el.scrollTop = el.scrollHeight;
    });
  }
  function appendTurnActivityCard(event) {
    state.currentAssistantEl = null;
    state.currentReasoningEl = null;
    var items = event.items || [];
    if (!items.length && !(event.exploredFileCount || event.searchCount || event.commandCount || event.thoughtCount)) return;
    var upsert = event.upsert === true;
    var hostRow = targetHostRow();
    var details = null;
    var isNew = false;
    if (hostRow) {
      details = hostRow.querySelector(":scope > .turn-activity");
      if (!details) {
        details = createTurnActivityDetails();
        hostRow.appendChild(details);
        isNew = true;
      }
    } else if (isFragmentBuild()) {
      const row = document.createElement("div");
      row.className = "message-row assistant turn-activity-host";
      details = createTurnActivityDetails();
      row.appendChild(details);
      getMessageRoot().appendChild(row);
      isNew = true;
    } else {
      return;
    }
    var keepOpen = upsert && !isNew && details.open === true;
    details.innerHTML = "";
    var summary = document.createElement("summary");
    var summaryText = document.createElement("span");
    summaryText.className = "turn-activity-summary-text";
    summaryText.textContent = turnActivitySummaryText(event);
    summary.appendChild(summaryText);
    var chevron = document.createElement("span");
    chevron.className = "turn-activity-chevron";
    chevron.textContent = "\u203A";
    summary.appendChild(chevron);
    details.appendChild(summary);
    var body = document.createElement("div");
    body.className = "turn-activity-body";
    var workedFor = formatWorkedFor(event.durationMs);
    if (workedFor) {
      var duration = document.createElement("div");
      duration.className = "turn-activity-duration";
      duration.textContent = workedFor;
      body.appendChild(duration);
    }
    items.forEach(function(item) {
      var hasDiff = item.lines && item.lines.length;
      var hasThought = item.kind === "thought" && item.body;
      var hasNarration = item.kind === "narration" && item.body;
      var entry = document.createElement("div");
      entry.className = "turn-activity-item" + (hasDiff ? " has-diff" : "") + (hasThought ? " has-thought" : "") + (hasNarration ? " has-narration" : "");
      if (hasThought || hasNarration) {
        var thoughtLabel = document.createElement("div");
        thoughtLabel.className = "turn-activity-thought-label";
        thoughtLabel.textContent = item.verb || (hasNarration ? t("said") || "Said" : t("thought") || "Thought");
        entry.appendChild(thoughtLabel);
        var thought = document.createElement("div");
        thought.className = "turn-activity-thought";
        thought.textContent = item.body || "";
        entry.appendChild(thought);
        body.appendChild(entry);
        return;
      }
      var button = document.createElement("button");
      button.type = "button";
      button.className = "turn-activity-row";
      button.title = item.path || item.detail || "";
      var line = document.createElement("span");
      line.className = "turn-activity-line";
      var verbText = item.verb || "";
      var detailText = item.detail || item.path || "";
      line.textContent = verbText && detailText ? verbText + " " + detailText : verbText || detailText;
      button.appendChild(line);
      if (item.status) {
        var status = document.createElement("span");
        status.className = "turn-activity-status tool-status";
        applyToolStatusBadge(status, item.status);
        if (item.statusLabel) status.textContent = item.statusLabel;
        button.appendChild(status);
      }
      entry.appendChild(button);
      if (hasDiff) {
        var diff = document.createElement("div");
        diff.className = "turn-activity-diff";
        diff.innerHTML = renderDiffLines(item.lines);
        button.addEventListener("click", function(e) {
          e.preventDefault();
          e.stopPropagation();
          entry.classList.toggle("open");
          scrollToBottom();
        });
        entry.appendChild(diff);
      } else if (item.body || item.messageId || item.toolCallId) {
        var detailPanel = document.createElement("pre");
        detailPanel.className = "turn-activity-tool-detail";
        if (item.body) {
          detailPanel.textContent = item.body;
          entry.dataset.hydrated = "1";
        } else {
          detailPanel.textContent = "\u2026";
          entry.dataset.hydrated = "0";
        }
        if (item.messageId) entry.dataset.messageId = item.messageId;
        if (item.toolCallId) entry.dataset.toolCallId = item.toolCallId;
        button.addEventListener("click", function(e) {
          e.preventDefault();
          e.stopPropagation();
          var opening = !entry.classList.contains("open");
          entry.classList.toggle("open");
          if (opening && entry.dataset.hydrated !== "1") {
            requestToolDetailForEntry(entry, detailPanel);
          }
          scrollToBottom();
        });
        entry.appendChild(detailPanel);
      }
      body.appendChild(entry);
    });
    details.appendChild(body);
    if (event.upsert) {
      details.setAttribute("data-live", "1");
      details.open = keepOpen;
    } else {
      details.removeAttribute("data-live");
      details.open = false;
    }
    if (details.open) {
      details.classList.add("is-expanded");
    } else {
      details.classList.remove("is-expanded");
    }
    syncTurnActivityChevron(details);
    updateEmptyStateVisibility();
    scrollTurnActivityThoughts(details);
    scrollToBottom();
  }
  function createCompactionSkeleton(id) {
    const details = document.createElement("details");
    details.className = "compaction-checkpoint";
    details.dataset.compactionId = id;
    details.innerHTML = '<summary><span class="compaction-title"></span><span class="tool-status"></span></summary><div class="compaction-body"><div class="compaction-summary"></div><details class="compaction-tech"><summary class="compaction-tech-label"></summary><pre class="compaction-detail"></pre></details></div>';
    return details;
  }
  function upsertCompactionCheckpoint(event) {
    const id = event.id || "compaction";
    var hostRow = targetHostRow();
    var details = null;
    if (hostRow) {
      details = hostRow.querySelector(":scope > .compaction-checkpoint");
      if (!details) {
        details = createCompactionSkeleton(id);
        hostRow.appendChild(details);
      }
    } else if (isFragmentBuild()) {
      state.currentAssistantEl = null;
      state.currentReasoningEl = null;
      const row = document.createElement("div");
      row.className = "message-row assistant compaction-row";
      details = createCompactionSkeleton(id);
      row.appendChild(details);
      getMessageRoot().appendChild(row);
    } else {
      return;
    }
    const title = details.querySelector(".compaction-title");
    if (title) title.textContent = event.title || "";
    applyToolStatusBadge(
      details.querySelector(".tool-status"),
      event.running ? "running" : event.status || "succeeded"
    );
    const summary = details.querySelector(".compaction-summary");
    if (summary) {
      summary.textContent = event.summary || "";
      summary.style.display = event.summary ? "block" : "none";
    }
    const tech = details.querySelector(".compaction-tech");
    const techLabel = details.querySelector(".compaction-tech-label");
    const detail = details.querySelector(".compaction-detail");
    if (techLabel) techLabel.textContent = event.detailsLabel || "";
    const techText = [event.header, event.detail].filter(Boolean).join("\n\n");
    if (detail) detail.textContent = techText;
    if (tech) tech.style.display = techText && !event.running ? "block" : "none";
    scrollToBottom();
  }
  function isPlanSpecialTool(name) {
    return name === "ask_plan_clarification" || name === "publish_plan";
  }
  function scopedRoot() {
    if (state.patchRow) return state.patchRow;
    if (state.virtualRender) return getMessageRoot();
    return document;
  }
  function getPlanClarifyCard(requestId) {
    if (!requestId) return null;
    const root = scopedRoot();
    if (!root || !root.querySelector) return null;
    return root.querySelector('.plan-clarify-card[data-request-id="' + cssEscape(requestId) + '"]');
  }
  function resolvePlanCard(event, createSkeleton) {
    var hostRow = targetHostRow();
    if (hostRow) {
      var existing = hostRow.querySelector(":scope > .plan-clarify-card, :scope > .plan-ready-card");
      if (existing) return existing;
      return createSkeleton(hostRow);
    }
    if (isFragmentBuild()) {
      var row = document.createElement("div");
      row.className = "message-row assistant plan-row";
      var created = createSkeleton(row);
      getMessageRoot().appendChild(row);
      return created;
    }
    return null;
  }
  function showPlanClarify(event) {
    if (!event || !event.requestId || !event.questions || !event.questions.length) return;
    state.currentAssistantEl = null;
    state.currentReasoningEl = null;
    var card = resolvePlanCard(event, function(host) {
      var c = document.createElement("div");
      c.className = "plan-clarify-card";
      host.appendChild(c);
      return c;
    });
    if (!card) return;
    card.dataset.requestId = event.requestId;
    card.innerHTML = "";
    var title = document.createElement("div");
    title.className = "plan-card-title";
    title.dataset.i18n = "planClarifyTitle";
    title.textContent = t("planClarifyTitle");
    card.appendChild(title);
    (event.questions || []).forEach(function(question) {
      var block = document.createElement("div");
      block.className = "plan-clarify-question";
      block.dataset.questionId = question.id || "";
      block.dataset.allowMultiple = question.allowMultiple ? "1" : "0";
      var prompt = document.createElement("div");
      prompt.className = "plan-clarify-prompt";
      prompt.textContent = question.prompt || "";
      block.appendChild(prompt);
      var options = document.createElement("div");
      options.className = "plan-clarify-options";
      (question.options || []).forEach(function(option) {
        var btn = document.createElement("button");
        btn.type = "button";
        btn.className = "plan-clarify-option";
        btn.dataset.optionId = option.id || "";
        btn.textContent = option.label || option.id || "";
        btn.addEventListener("click", function() {
          if (card.dataset.resolved === "1") return;
          if (block.dataset.allowMultiple === "1") {
            btn.classList.toggle("selected");
          } else {
            options.querySelectorAll(".plan-clarify-option").forEach(function(other) {
              other.classList.toggle("selected", other === btn);
            });
          }
        });
        options.appendChild(btn);
      });
      block.appendChild(options);
      card.appendChild(block);
    });
    if (event.allowFreeText !== false) {
      var note = document.createElement("textarea");
      note.className = "plan-clarify-notes";
      note.rows = 2;
      note.placeholder = t("planClarifyNotes");
      note.dataset.i18nPlaceholder = "planClarifyNotes";
      card.appendChild(note);
    }
    var actions = document.createElement("div");
    actions.className = "plan-card-actions";
    var submit = document.createElement("button");
    submit.type = "button";
    submit.className = "plan-card-button primary";
    submit.dataset.i18n = "planClarifySubmit";
    submit.textContent = t("planClarifySubmit");
    submit.addEventListener("click", function() {
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
    if (!card || card.dataset.resolved === "1") return;
    var selections = {};
    var hasSelection = false;
    card.querySelectorAll(".plan-clarify-question").forEach(function(block) {
      var qid = block.dataset.questionId || "";
      var ids = [];
      block.querySelectorAll(".plan-clarify-option.selected").forEach(function(btn) {
        if (btn.dataset.optionId) ids.push(btn.dataset.optionId);
      });
      if (qid && ids.length) {
        selections[qid] = ids;
        hasSelection = true;
      }
    });
    var notes = card.querySelector(".plan-clarify-notes");
    var freeText = notes && notes.value ? String(notes.value).trim() : "";
    if (!hasSelection && !freeText) return;
    post({
      type: "planClarifyAnswer",
      requestId: card.dataset.requestId,
      selections,
      freeText
    });
  }
  function applyPlanClarifyResolved(card, summary) {
    if (!card) return;
    card.dataset.resolved = "1";
    card.querySelectorAll("button, textarea").forEach(function(el) {
      el.disabled = true;
    });
    var result = card.querySelector(".plan-clarify-result");
    if (!result) {
      result = document.createElement("div");
      result.className = "plan-clarify-result";
      card.appendChild(result);
    }
    result.textContent = summary || t("planClarifyAnswered");
    result.dataset.i18n = summary ? "" : "planClarifyAnswered";
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
    return "";
  }
  function showPlanReady(event) {
    if (!event) return;
    var runId = event.runId || "plan";
    state.currentAssistantEl = null;
    state.currentReasoningEl = null;
    var card = resolvePlanCard(event, function(host) {
      var c = document.createElement("div");
      c.className = "plan-ready-card";
      host.appendChild(c);
      return c;
    });
    if (!card) return;
    card.dataset.runId = runId;
    if (event.planPath) card.dataset.planPath = event.planPath;
    card.innerHTML = "";
    var title = document.createElement("div");
    title.className = "plan-card-title";
    title.textContent = event.title || t("planReadyTitle");
    card.appendChild(title);
    var body = document.createElement("div");
    body.className = "plan-ready-body md-root";
    applyMarkdownHtml(body, resolveRenderedHtml(event, resolvePlanMarkdown(event)));
    card.appendChild(body);
    var actions = document.createElement("div");
    actions.className = "plan-card-actions";
    if (event.planPath) {
      var openBtn = document.createElement("button");
      openBtn.type = "button";
      openBtn.className = "plan-card-button";
      openBtn.dataset.i18n = "planOpenEditor";
      openBtn.textContent = t("planOpenEditor");
      openBtn.addEventListener("click", function() {
        post({ type: "planOpenEditor", path: card.dataset.planPath });
      });
      actions.appendChild(openBtn);
    }
    var buildBtn = document.createElement("button");
    buildBtn.type = "button";
    buildBtn.className = "plan-card-button primary";
    buildBtn.dataset.i18n = "planBuild";
    buildBtn.textContent = t("planBuild");
    buildBtn.addEventListener("click", function() {
      post({ type: "planBuild" });
    });
    actions.appendChild(buildBtn);
    card.appendChild(actions);
    updateEmptyStateVisibility();
    scrollToBottom(true);
  }
  function appendOverflowSkipped(event) {
    state.currentAssistantEl = null;
    state.currentReasoningEl = null;
    const row = document.createElement("div");
    row.className = "message-row assistant status-row";
    const el = document.createElement("div");
    el.className = "overflow-skipped";
    el.textContent = event.message || "";
    row.appendChild(el);
    getMessageRoot().appendChild(row);
    updateEmptyStateVisibility();
    scrollToBottom();
  }
  function handleEvent(event) {
    if (!event || !event.type) return;
    switch (event.type) {
      case "RESET_TIMELINE":
        resetTimeline();
        updateEmptyStateVisibility();
        break;
      case "USER_MESSAGE":
        appendMessage("user", event.content || "", false, event.images || [], event.startedAt || "", event.mentions || []);
        break;
      case "FILES_CHANGED":
        appendFilesChangedCard(event);
        break;
      case "TURN_ACTIVITY":
        appendTurnActivityCard(event);
        break;
      case "COMPACTION_CHECKPOINT":
        upsertCompactionCheckpoint(event);
        break;
      case "OVERFLOW_RETRY_SKIPPED":
        appendOverflowSkipped(event);
        break;
      case "RUN_STARTED":
        state.currentAssistantEl = null;
        state.currentReasoningEl = null;
        break;
      case "REASONING_MESSAGE_START":
      case "REASONING_MESSAGE_CONTENT":
      case "REASONING_MESSAGE_END":
        break;
      case "TEXT_MESSAGE_START":
        state.currentAssistantEl = null;
        state.assistantStarted[event.messageId] = false;
        break;
      case "TEXT_MESSAGE_CONTENT":
        finalizeReasoningLabel(event.messageId);
        if (!state.assistantStarted[event.messageId]) ensureAssistantBubble(event.messageId);
        break;
      case "TEXT_MESSAGE_END":
        state.currentAssistantEl = null;
        break;
      case "STATIC_ASSISTANT_HTML":
        applyAssistantHtml(
          event.messageId,
          resolveRenderedHtml(event),
          event.createIfMissing !== false,
          event.streaming === true,
          event.responseDurationMs
        );
        updateCopyText(
          findAssistantBubbleRow(event.messageId),
          resolveEventMarkdown(event)
        );
        if (!event.streaming) state.currentAssistantEl = null;
        break;
      case "REMOVE_ASSISTANT_BUBBLES": {
        var ids = event.messageIds || [];
        ids.forEach(function(id) {
          var row = findAssistantBubbleRow(id);
          if (row && row.parentNode) row.parentNode.removeChild(row);
          delete state.assistantStarted[id];
        });
        state.currentAssistantEl = null;
        break;
      }
      case "TOOL_CALL_START":
        if (isPlanSpecialTool(event.toolCallName)) {
          state.currentAssistantEl = null;
          break;
        }
        createToolCard(event.toolCallId, event.toolCallName);
        break;
      case "TOOL_CALL_ARGS": {
        const card = getToolCard(event.toolCallId);
        const pre = card && card.querySelector(".tool-args");
        if (pre) pre.textContent = event.delta || "";
        scrollToBottom();
        break;
      }
      case "TOOL_CALL_END": {
        const card = getToolCard(event.toolCallId);
        if (!card) break;
        const normalized = (event.status || "running").toLowerCase();
        if (normalized === "awaiting_approval" || normalized === "approval_denied") {
          applyToolStatusBadge(card.querySelector(".tool-status"), normalized);
          if (normalized === "awaiting_approval") {
            card.dataset.awaitingApproval = "true";
          }
          break;
        }
        const panel = card.querySelector(".tool-approval");
        const hasPendingApproval = panel && panel.querySelector(".tool-approval-actions");
        if (card.dataset.awaitingApproval !== "true" && !hasPendingApproval) {
          applyToolStatusBadge(card.querySelector(".tool-status"), event.status || "running");
        }
        break;
      }
      case "TOOL_APPROVAL_REQUEST":
        showToolApproval(event);
        break;
      case "TOOL_APPROVAL_RESOLVED":
        resolveToolApproval(event);
        break;
      case "PLAN_CLARIFY_REQUEST":
        showPlanClarify(event);
        break;
      case "PLAN_CLARIFY_RESOLVED":
        resolvePlanClarify(event);
        break;
      case "PLAN_READY":
        showPlanReady(event);
        break;
      case "TOOL_CALL_OUTPUT": {
        const card = getToolCard(event.toolCallId);
        const result = card && card.querySelector(".tool-result");
        const html = card && card.querySelector(".tool-result-html");
        if (result && html) {
          result.style.display = "block";
          html.textContent += event.delta || "";
        }
        scrollToBottom();
        break;
      }
      case "TOOL_CALL_RESULT": {
        const card = getToolCard(event.toolCallId);
        if (!card) break;
        if (event.messageId) card.dataset.messageId = event.messageId;
        applyToolStatusBadge(card.querySelector(".tool-status"), event.status || "succeeded");
        if (event.header) {
          let header = card.querySelector(".tool-header");
          if (!header) {
            header = document.createElement("div");
            header.className = "tool-header";
            card.querySelector(".tool-body").prepend(header);
          }
          header.textContent = event.header;
        }
        if (event.summary) {
          let summary = card.querySelector(".tool-summary-text");
          if (!summary) {
            summary = document.createElement("div");
            summary.className = "tool-summary-text";
            card.querySelector(".tool-body").insertBefore(summary, card.querySelector(".tool-result"));
          }
          summary.textContent = event.summary;
        }
        const result = card.querySelector(".tool-result");
        const html = card.querySelector(".tool-result-html");
        if (result && html) {
          result.style.display = "block";
          applyMarkdownHtml(html, resolveRenderedHtml(event, event.content || ""));
        }
        var contentText = event.content || "";
        var needsHydration = !!(event.messageId || event.toolCallId) && (contentText.indexOf("[Tool result evicted") >= 0 || contentText.length < 80);
        if (needsHydration) {
          card.dataset.hydrated = "0";
          if (!card.dataset.bindHydrate) {
            card.dataset.bindHydrate = "1";
            card.addEventListener("toggle", function() {
              if (card.open && card.dataset.hydrated !== "1") {
                requestToolDetailForToolCard(card);
              }
            });
          }
        } else {
          card.dataset.hydrated = "1";
        }
        scrollToBottom();
        break;
      }
    }
  }
  function applyThemeTokensToRoot(tokensCss) {
    var root = document.documentElement;
    root.style.cssText = "";
    tokensCss.replace(/(--[\\w-]+)\\s*:\\s*([^;]+);/g, function(_, name, value) {
      root.style.setProperty(name.trim(), value.trim());
    });
  }
  function syncThemeSurfaces() {
    var rootStyle = getComputedStyle(document.documentElement);
    var chatBg = rootStyle.getPropertyValue("--chat-bg").trim();
    var assistantText = rootStyle.getPropertyValue("--assistant-text").trim();
    if (chatBg) {
      document.documentElement.style.backgroundColor = chatBg;
      document.body.style.backgroundColor = chatBg;
      var scroller = document.getElementById("chat-scroll");
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
    var tokensEl = document.getElementById("chat-theme-tokens");
    if (tokensEl) {
      tokensEl.textContent = tokensCss;
    }
    var syntaxEl = document.getElementById("chat-code-syntax");
    if (syntaxEl) {
      syntaxEl.textContent = decodeBase64Utf8(syntaxB64);
    }
    applyThemeTokensToRoot(tokensCss);
    syncThemeSurfaces();
  }
  function renderItemRow(item) {
    const fragment = document.createDocumentFragment();
    const prevTarget = state.batchTarget;
    const prevVirtual = state.virtualRender;
    state.batchTarget = fragment;
    state.virtualRender = true;
    try {
      if (item.type === "TOOL") {
        handleEvent(item.event);
        const toolState = item.toolState || {};
        [
          "TOOL_CALL_ARGS",
          "TOOL_CALL_END",
          "TOOL_CALL_OUTPUT",
          "TOOL_CALL_RESULT",
          "TOOL_APPROVAL_REQUEST",
          "TOOL_APPROVAL_RESOLVED"
        ].forEach(function(type) {
          if (toolState[type]) handleEvent(toolState[type]);
        });
      } else if (item.type === "PLAN_CLARIFY" && item.event.resolved) {
        handleEvent(Object.assign({ type: "PLAN_CLARIFY_REQUEST" }, item.event));
        handleEvent({
          type: "PLAN_CLARIFY_RESOLVED",
          requestId: item.event.requestId,
          summary: item.event.summary
        });
      } else {
        handleEvent(item.event);
      }
    } finally {
      state.batchTarget = prevTarget;
      state.virtualRender = prevVirtual;
    }
    const row = fragment.firstChild;
    if (row && row.nodeType === 1) {
      row.dataset.timelineItemId = item.id;
      row.dataset.timelineVersion = String(item.version);
    }
    return row || null;
  }
  function createDomRenderer() {
    return {
      state,
      handleEvent,
      renderItemRow,
      resetTimeline,
      beginBatch,
      endBatch,
      scrollToBottom,
      updateEmptyStateVisibility,
      applyToolDetailPayload,
      applyThemeUpdate,
      applyChatI18n,
      enhanceCodeBlocks,
      getChatScroller,
      isNearBottom,
      hasActiveSelection,
      findAssistantBubbleRow,
      t,
      post
    };
  }
  (function bindExternalLinks() {
    var root = document.getElementById("virtual-window") || document.getElementById("messages");
    if (!root) return;
    root.addEventListener("click", function(e) {
      var target = e.target;
      if (!target || typeof target.closest !== "function") return;
      var anchor = target.closest("a[href]");
      if (!anchor) return;
      var href = anchor.getAttribute("href");
      if (!href || href.charAt(0) === "#") return;
      e.preventDefault();
      e.stopPropagation();
      post({ type: "openUrl", url: anchor.href });
    });
  })();
  (function bindImageLightbox() {
    var lightbox = document.getElementById("image-lightbox");
    if (!lightbox) return;
    var backdrop = lightbox.querySelector(".image-lightbox-backdrop");
    var closeBtn = lightbox.querySelector(".image-lightbox-close");
    if (backdrop) backdrop.addEventListener("click", closeImagePreview);
    if (closeBtn) closeBtn.addEventListener("click", closeImagePreview);
    document.addEventListener("keydown", function(e) {
      if (e.key === "Escape" && !lightbox.hidden) closeImagePreview();
    });
  })();

  // node_modules/@tanstack/virtual-core/dist/esm/lazy-measurements.js
  function createLazyMeasurementsView(count, flat, getItemKey) {
    const cache = new Array(count);
    return new Proxy(cache, {
      get(target, prop, receiver) {
        if (typeof prop === "string") {
          const c = prop.charCodeAt(0);
          if (c >= 48 && c <= 57) {
            const i = +prop;
            if (Number.isInteger(i) && i >= 0 && i < count) {
              let v = target[i];
              if (!v) {
                const s = flat[i * 2];
                v = target[i] = {
                  index: i,
                  key: getItemKey(i),
                  start: s,
                  size: flat[i * 2 + 1],
                  end: s + flat[i * 2 + 1],
                  lane: 0
                };
              }
              return v;
            }
          }
          if (prop === "length") return count;
        }
        return Reflect.get(target, prop, receiver);
      }
    });
  }

  // node_modules/@tanstack/virtual-core/dist/esm/utils.js
  function memo(getDeps, fn, opts) {
    let deps = opts.initialDeps ?? [];
    let result;
    let isInitial = true;
    function memoizedFunction() {
      var _a;
      const debugEnabled = !!opts.key && !!((_a = opts.debug) == null ? void 0 : _a.call(opts));
      let depTime = 0;
      if (debugEnabled) depTime = Date.now();
      const newDeps = getDeps();
      const depsChanged = newDeps.length !== deps.length || newDeps.some((dep, index) => deps[index] !== dep);
      if (!depsChanged) {
        return result;
      }
      deps = newDeps;
      let resultTime = 0;
      if (debugEnabled) resultTime = Date.now();
      result = fn(...newDeps);
      if (debugEnabled) {
        const depEndTime = Math.round((Date.now() - depTime) * 100) / 100;
        const resultEndTime = Math.round((Date.now() - resultTime) * 100) / 100;
        const resultFpsPercentage = resultEndTime / 16;
        const pad = (str, num) => {
          str = String(str);
          while (str.length < num) {
            str = " " + str;
          }
          return str;
        };
        console.info(
          `%c\u23F1 ${pad(resultEndTime, 5)} /${pad(depEndTime, 5)} ms`,
          `
            font-size: .6rem;
            font-weight: bold;
            color: hsl(${Math.max(
            0,
            Math.min(120 - 120 * resultFpsPercentage, 120)
          )}deg 100% 31%);`,
          opts == null ? void 0 : opts.key
        );
      }
      if ((opts == null ? void 0 : opts.onChange) && !(isInitial && opts.skipInitialOnChange)) {
        opts.onChange(result);
      }
      isInitial = false;
      return result;
    }
    memoizedFunction.updateDeps = (newDeps) => {
      deps = newDeps;
    };
    return memoizedFunction;
  }
  function notUndefined(value, msg) {
    if (value === void 0) {
      throw new Error(`Unexpected undefined${msg ? `: ${msg}` : ""}`);
    } else {
      return value;
    }
  }
  var approxEqual = (a, b) => Math.abs(a - b) < 1.01;
  var debounce = (targetWindow, fn, ms) => {
    let timeoutId;
    return Object.assign(
      function(...args) {
        targetWindow.clearTimeout(timeoutId);
        timeoutId = targetWindow.setTimeout(() => fn.apply(this, args), ms);
      },
      {
        // The handle is closure-local, so a caller that has already
        // unsubscribed has no way to stop a queued call. Teardown paths use
        // this to drop the pending invocation instead of letting it land.
        cancel: () => {
          targetWindow.clearTimeout(timeoutId);
        }
      }
    );
  };

  // node_modules/@tanstack/virtual-core/dist/esm/index.js
  var _isIOSResult;
  var isIOSWebKit = () => {
    if (_isIOSResult !== void 0) return _isIOSResult;
    if (typeof navigator === "undefined") return _isIOSResult = false;
    if (/iP(hone|od|ad)/.test(navigator.userAgent)) return _isIOSResult = true;
    const mtp = navigator.maxTouchPoints;
    return _isIOSResult = navigator.platform === "MacIntel" && mtp !== void 0 && mtp > 0;
  };
  var getRect = (element) => {
    const { offsetWidth, offsetHeight } = element;
    return { width: offsetWidth, height: offsetHeight };
  };
  var defaultKeyExtractor = (index) => index;
  var defaultRangeExtractor = (range) => {
    const start = Math.max(range.startIndex - range.overscan, 0);
    const end = Math.min(range.endIndex + range.overscan, range.count - 1);
    const len = end - start + 1;
    const arr = new Array(len);
    for (let i = 0; i < len; i++) {
      arr[i] = start + i;
    }
    return arr;
  };
  var observeElementRect = (instance, cb) => {
    const element = instance.scrollElement;
    if (!element) {
      return;
    }
    const targetWindow = instance.targetWindow;
    if (!targetWindow) {
      return;
    }
    const handler = (rect) => {
      const { width, height } = rect;
      cb({ width: Math.round(width), height: Math.round(height) });
    };
    handler(getRect(element));
    if (!targetWindow.ResizeObserver) {
      return () => {
      };
    }
    const observer = new targetWindow.ResizeObserver((entries) => {
      const run = () => {
        const entry = entries[0];
        if (entry == null ? void 0 : entry.borderBoxSize) {
          const box = entry.borderBoxSize[0];
          if (box) {
            handler({ width: box.inlineSize, height: box.blockSize });
            return;
          }
        }
        handler(getRect(element));
      };
      instance.options.useAnimationFrameWithResizeObserver ? requestAnimationFrame(run) : run();
    });
    observer.observe(element, { box: "border-box" });
    return () => {
      observer.unobserve(element);
    };
  };
  var addEventListenerOptions = {
    passive: true
  };
  var supportsScrollend = typeof window == "undefined" ? true : "onscrollend" in window;
  var observeOffset = (instance, cb, readOffset) => {
    const element = instance.scrollElement;
    if (!element) {
      return;
    }
    const targetWindow = instance.targetWindow;
    if (!targetWindow) {
      return;
    }
    const registerScrollendEvent = instance.options.useScrollendEvent && supportsScrollend;
    let offset = 0;
    const fallback = registerScrollendEvent ? null : debounce(
      targetWindow,
      () => cb(offset, false),
      instance.options.isScrollingResetDelay
    );
    const createHandler = (isScrolling) => () => {
      offset = readOffset(element);
      fallback == null ? void 0 : fallback();
      cb(offset, isScrolling);
    };
    const handler = createHandler(true);
    const endHandler = createHandler(false);
    element.addEventListener("scroll", handler, addEventListenerOptions);
    if (registerScrollendEvent) {
      element.addEventListener("scrollend", endHandler, addEventListenerOptions);
    }
    return () => {
      element.removeEventListener("scroll", handler);
      if (registerScrollendEvent) {
        element.removeEventListener("scrollend", endHandler);
      }
      fallback == null ? void 0 : fallback.cancel();
    };
  };
  var observeElementOffset = (instance, cb) => observeOffset(instance, cb, (el) => {
    const { horizontal, isRtl } = instance.options;
    return horizontal ? el.scrollLeft * (isRtl && -1 || 1) : el.scrollTop;
  });
  var measureElement = (element, entry, instance) => {
    if (instance.options.useCachedMeasurements) {
      const index = instance.indexFromElement(element);
      const key = instance.options.getItemKey(index);
      return instance.itemSizeCache.get(key) ?? instance.options.estimateSize(index);
    }
    if (entry == null ? void 0 : entry.borderBoxSize) {
      const box = entry.borderBoxSize[0];
      if (box) {
        const size = Math.round(
          box[instance.options.horizontal ? "inlineSize" : "blockSize"]
        );
        return size;
      }
    }
    if (!entry) {
      const index = instance.indexFromElement(element);
      const key = instance.options.getItemKey(index);
      const cachedSize = instance.itemSizeCache.get(key);
      if (cachedSize !== void 0) {
        return cachedSize;
      }
    }
    return element[instance.options.horizontal ? "offsetWidth" : "offsetHeight"];
  };
  var scrollWithAdjustments = (offset, {
    adjustments = 0,
    behavior
  }, instance) => {
    var _a, _b;
    (_b = (_a = instance.scrollElement) == null ? void 0 : _a.scrollTo) == null ? void 0 : _b.call(_a, {
      [instance.options.horizontal ? "left" : "top"]: offset + adjustments,
      behavior
    });
  };
  var elementScroll = scrollWithAdjustments;
  var Virtualizer = class {
    constructor(opts) {
      this.unsubs = [];
      this.scrollElement = null;
      this.targetWindow = null;
      this.isScrolling = false;
      this.scrollState = null;
      this.measurementsCache = [];
      this._flatMeasurements = null;
      this.itemSizeCache = /* @__PURE__ */ new Map();
      this.itemSizeCacheVersion = 0;
      this.laneAssignments = /* @__PURE__ */ new Map();
      this.pendingMin = null;
      this.prevLanes = void 0;
      this.lanesChangedFlag = false;
      this.lanesSettling = false;
      this.pendingScrollAnchor = null;
      this.scrollRect = null;
      this.scrollOffset = null;
      this.scrollDirection = null;
      this.scrollAdjustments = 0;
      this._iosDeferredAdjustment = 0;
      this._iosTouching = false;
      this._iosJustTouchEnded = false;
      this._iosTouchEndTimerId = null;
      this._intendedScrollOffset = null;
      this.elementsCache = /* @__PURE__ */ new Map();
      this.now = () => {
        var _a, _b, _c;
        return ((_c = (_b = (_a = this.targetWindow) == null ? void 0 : _a.performance) == null ? void 0 : _b.now) == null ? void 0 : _c.call(_b)) ?? Date.now();
      };
      this.observer = /* @__PURE__ */ (() => {
        let _ro = null;
        const get = () => {
          if (_ro) {
            return _ro;
          }
          if (!this.targetWindow || !this.targetWindow.ResizeObserver) {
            return null;
          }
          return _ro = new this.targetWindow.ResizeObserver((entries) => {
            entries.forEach((entry) => {
              const run = () => {
                const node = entry.target;
                const index = this.indexFromElement(node);
                if (!node.isConnected) {
                  this.observer.unobserve(node);
                  for (const [cacheKey, cachedNode] of this.elementsCache) {
                    if (cachedNode === node) {
                      this.elementsCache.delete(cacheKey);
                      break;
                    }
                  }
                  return;
                }
                if (!this.isIndexInRange(index)) return;
                if (this.shouldMeasureDuringScroll(index)) {
                  this.resizeItem(
                    index,
                    this.options.measureElement(node, entry, this)
                  );
                }
              };
              this.options.useAnimationFrameWithResizeObserver ? requestAnimationFrame(run) : run();
            });
          });
        };
        return {
          disconnect: () => {
            var _a;
            (_a = get()) == null ? void 0 : _a.disconnect();
            _ro = null;
          },
          observe: (target) => {
            var _a;
            return (_a = get()) == null ? void 0 : _a.observe(target, { box: "border-box" });
          },
          unobserve: (target) => {
            var _a;
            return (_a = get()) == null ? void 0 : _a.unobserve(target);
          }
        };
      })();
      this.range = null;
      this.setOptions = (opts2) => {
        var _a, _b;
        const merged = {
          debug: false,
          initialOffset: 0,
          overscan: 1,
          paddingStart: 0,
          paddingEnd: 0,
          scrollPaddingStart: 0,
          scrollPaddingEnd: 0,
          horizontal: false,
          getItemKey: defaultKeyExtractor,
          rangeExtractor: defaultRangeExtractor,
          onChange: () => {
          },
          measureElement,
          initialRect: { width: 0, height: 0 },
          scrollMargin: 0,
          gap: 0,
          indexAttribute: "data-index",
          initialMeasurementsCache: [],
          lanes: 1,
          anchorTo: "start",
          followOnAppend: false,
          scrollEndThreshold: 1,
          isScrollingResetDelay: 150,
          enabled: true,
          isRtl: false,
          useScrollendEvent: false,
          useAnimationFrameWithResizeObserver: false,
          laneAssignmentMode: "estimate",
          useCachedMeasurements: false
        };
        for (const key in opts2) {
          const v = opts2[key];
          if (v !== void 0) merged[key] = v;
        }
        const prevOptions = this.options;
        let anchor = null;
        let followOnAppend = null;
        let edgeKeysChanged = false;
        if (prevOptions !== void 0 && prevOptions.enabled && merged.enabled && merged.anchorTo === "end" && this.scrollElement !== null) {
          const prevCount = prevOptions.count;
          const nextCount = merged.count;
          const measurements = this.getMeasurements();
          const prevFirstKey = prevCount > 0 ? ((_a = measurements[0]) == null ? void 0 : _a.key) ?? prevOptions.getItemKey(0) : null;
          const prevLastKey = prevCount > 0 ? ((_b = measurements[prevCount - 1]) == null ? void 0 : _b.key) ?? prevOptions.getItemKey(prevCount - 1) : null;
          const didCountChange = nextCount !== prevCount;
          const didEdgeKeysChange = didCountChange || prevCount > 0 && nextCount > 0 && (merged.getItemKey(0) !== prevFirstKey || merged.getItemKey(nextCount - 1) !== prevLastKey);
          if (didEdgeKeysChange) {
            edgeKeysChanged = true;
            const item = prevCount > 0 ? this.getVirtualItemForOffset(this.getScrollOffset()) ?? measurements[0] : null;
            if (item) {
              anchor = [item.key, this.getScrollOffset() - item.start];
            }
            const behavior = merged.followOnAppend === true ? "auto" : merged.followOnAppend || null;
            if (behavior && nextCount > prevCount && this.isAtEnd(prevOptions.scrollEndThreshold) && (prevCount === 0 || merged.getItemKey(nextCount - 1) !== prevLastKey)) {
              followOnAppend = behavior;
            }
          }
        }
        this.options = merged;
        if (edgeKeysChanged) {
          this.pendingMin = 0;
          this.itemSizeCacheVersion++;
        }
        let anchorResolved = false;
        let anchorDelta = 0;
        if (anchor && this.scrollOffset !== null) {
          const [anchorKey, anchorOffset] = anchor;
          const newMeasurements = this.getMeasurements();
          const { count, getItemKey } = this.options;
          let idx = 0;
          while (idx < count && getItemKey(idx) !== anchorKey) {
            idx++;
          }
          if (idx < count) {
            const anchorItem = newMeasurements[idx];
            if (anchorItem) {
              const newOffset = Math.max(0, anchorItem.start + anchorOffset);
              if (newOffset !== this.scrollOffset) {
                anchorDelta = newOffset - this.scrollOffset;
                this.scrollOffset = newOffset;
                anchorResolved = true;
              }
            }
          }
        }
        if (anchorResolved || followOnAppend) {
          this.pendingScrollAnchor = [
            anchorResolved ? anchor[0] : null,
            anchorResolved ? anchor[1] : 0,
            followOnAppend,
            anchorDelta
          ];
        }
      };
      this.notify = (sync) => {
        var _a, _b;
        (_b = (_a = this.options).onChange) == null ? void 0 : _b.call(_a, this, sync);
      };
      this.maybeNotify = memo(
        () => {
          this.calculateRange();
          return [
            this.isScrolling,
            this.range ? this.range.startIndex : null,
            this.range ? this.range.endIndex : null
          ];
        },
        (isScrolling) => {
          this.notify(isScrolling);
        },
        {
          key: "maybeNotify",
          debug: () => this.options.debug,
          initialDeps: [
            this.isScrolling,
            this.range ? this.range.startIndex : null,
            this.range ? this.range.endIndex : null
          ]
        }
      );
      this.cleanup = () => {
        this.unsubs.filter(Boolean).forEach((d) => d());
        this.unsubs = [];
        this.observer.disconnect();
        if (this.rafId != null && this.targetWindow) {
          this.targetWindow.cancelAnimationFrame(this.rafId);
          this.rafId = null;
        }
        this.scrollState = null;
        this.isScrolling = false;
        this.scrollDirection = null;
        this._iosDeferredAdjustment = 0;
        this._iosTouching = false;
        this._iosJustTouchEnded = false;
        this.scrollElement = null;
        this.targetWindow = null;
      };
      this._didMount = () => {
        return () => {
          this.cleanup();
        };
      };
      this._willUpdate = () => {
        var _a;
        const scrollElement = this.options.enabled ? this.options.getScrollElement() : null;
        if (this.scrollElement !== scrollElement) {
          this.cleanup();
          if (!scrollElement) {
            this.maybeNotify();
            return;
          }
          this.scrollElement = scrollElement;
          if (this.scrollElement && "ownerDocument" in this.scrollElement) {
            this.targetWindow = this.scrollElement.ownerDocument.defaultView;
          } else {
            this.targetWindow = ((_a = this.scrollElement) == null ? void 0 : _a.window) ?? null;
          }
          this.elementsCache.forEach((cached) => {
            this.observer.observe(cached);
          });
          this.unsubs.push(
            this.options.observeElementRect(this, (rect) => {
              this.scrollRect = rect;
              this.maybeNotify();
            })
          );
          this.unsubs.push(
            this.options.observeElementOffset(this, (offset, isScrolling) => {
              if (isScrolling && this._intendedScrollOffset === null && offset === this.scrollOffset) {
                return;
              }
              if (this._intendedScrollOffset !== null && Math.abs(offset - this._intendedScrollOffset) < 1.5) {
                offset = this._intendedScrollOffset;
              }
              this._intendedScrollOffset = null;
              this.scrollAdjustments = 0;
              const prevOffset = this.getScrollOffset();
              this.scrollDirection = isScrolling ? prevOffset === offset ? this.scrollDirection : prevOffset < offset ? "forward" : "backward" : null;
              this.scrollOffset = offset;
              this.isScrolling = isScrolling;
              this._flushIosDeferredIfReady();
              if (this.scrollState) {
                this.scheduleScrollReconcile();
              }
              this.maybeNotify();
            })
          );
          if ("addEventListener" in this.scrollElement) {
            const scrollEl = this.scrollElement;
            const onTouchStart = () => {
              this._iosTouching = true;
              this._iosJustTouchEnded = false;
              if (this._iosTouchEndTimerId !== null && this.targetWindow != null) {
                this.targetWindow.clearTimeout(this._iosTouchEndTimerId);
                this._iosTouchEndTimerId = null;
              }
            };
            const onTouchEnd = () => {
              this._iosTouching = false;
              if (!isIOSWebKit() || this.targetWindow == null) {
                return;
              }
              this._iosJustTouchEnded = true;
              this._iosTouchEndTimerId = this.targetWindow.setTimeout(() => {
                this._iosJustTouchEnded = false;
                this._iosTouchEndTimerId = null;
                this._flushIosDeferredIfReady();
              }, 150);
            };
            scrollEl.addEventListener(
              "touchstart",
              onTouchStart,
              addEventListenerOptions
            );
            scrollEl.addEventListener(
              "touchend",
              onTouchEnd,
              addEventListenerOptions
            );
            this.unsubs.push(() => {
              scrollEl.removeEventListener("touchstart", onTouchStart);
              scrollEl.removeEventListener("touchend", onTouchEnd);
              if (this._iosTouchEndTimerId !== null && this.targetWindow != null) {
                this.targetWindow.clearTimeout(this._iosTouchEndTimerId);
                this._iosTouchEndTimerId = null;
              }
            });
          }
          this._scrollToOffset(this.getScrollOffset(), {
            adjustments: void 0,
            behavior: void 0
          });
        }
        const anchor = this.pendingScrollAnchor;
        this.pendingScrollAnchor = null;
        if (anchor && this.scrollElement && this.options.enabled) {
          const [key, _offset, followOnAppend, anchorDelta] = anchor;
          if (key !== null && !followOnAppend) {
            if (isIOSWebKit() && (this.isScrolling || this._iosTouching || this._iosJustTouchEnded)) {
              if (anchorDelta !== 0) {
                this._iosDeferredAdjustment += anchorDelta;
              }
            } else {
              this._scrollToOffset(this.getScrollOffset(), {
                adjustments: void 0,
                behavior: void 0
              });
            }
          }
          if (followOnAppend) {
            this.scrollToEnd({ behavior: followOnAppend });
          }
        }
      };
      this._flushIosDeferredIfReady = () => {
        if (this._iosDeferredAdjustment === 0) return;
        if (this.isScrolling) return;
        if (this._iosTouching) return;
        if (this._iosJustTouchEnded) return;
        const cur = this.getScrollOffset();
        const max = this.getMaxScrollOffset();
        if (cur < 0 || cur > max) return;
        if (this._iosDeferredAdjustment < 0 && cur >= max - 1) {
          this._iosDeferredAdjustment = 0;
          return;
        }
        const delta = this._iosDeferredAdjustment;
        this._iosDeferredAdjustment = 0;
        this._scrollToOffset(cur, {
          adjustments: this.scrollAdjustments += delta,
          behavior: void 0
        });
      };
      this.rafId = null;
      this.getSize = () => {
        if (!this.options.enabled) {
          this.scrollRect = null;
          return 0;
        }
        this.scrollRect = this.scrollRect ?? this.options.initialRect;
        return this.scrollRect[this.options.horizontal ? "width" : "height"];
      };
      this.getScrollOffset = () => {
        if (!this.options.enabled) {
          this.scrollOffset = null;
          return 0;
        }
        this.scrollOffset = this.scrollOffset ?? (typeof this.options.initialOffset === "function" ? this.options.initialOffset() : this.options.initialOffset);
        return this.scrollOffset;
      };
      this.getMeasurementOptions = memo(
        () => [
          this.options.count,
          this.options.paddingStart,
          this.options.scrollMargin,
          this.options.getItemKey,
          this.options.enabled,
          this.options.lanes,
          this.options.laneAssignmentMode,
          this.options.gap
        ],
        (count, paddingStart, scrollMargin, getItemKey, enabled, lanes, laneAssignmentMode, gap) => {
          const lanesChanged = this.prevLanes !== void 0 && this.prevLanes !== lanes;
          if (lanesChanged) {
            this.lanesChangedFlag = true;
          }
          this.prevLanes = lanes;
          this.pendingMin = null;
          return {
            count,
            paddingStart,
            scrollMargin,
            getItemKey,
            enabled,
            lanes,
            laneAssignmentMode,
            gap
          };
        },
        {
          key: false
        }
      );
      this.isIndexInRange = (index) => index >= 0 && index < this.options.count;
      this.getMeasurements = memo(
        () => [this.getMeasurementOptions(), this.itemSizeCacheVersion],
        ({
          count,
          paddingStart,
          scrollMargin,
          getItemKey,
          enabled,
          lanes,
          laneAssignmentMode,
          gap
        }, _itemSizeCacheVersion) => {
          const itemSizeCache = this.itemSizeCache;
          if (!enabled) {
            this.measurementsCache = [];
            this.itemSizeCache.clear();
            this.laneAssignments.clear();
            return [];
          }
          if (this.laneAssignments.size > count) {
            for (const index of this.laneAssignments.keys()) {
              if (index >= count) {
                this.laneAssignments.delete(index);
              }
            }
          }
          if (this.lanesChangedFlag) {
            this.lanesChangedFlag = false;
            this.lanesSettling = true;
            this.measurementsCache = [];
            this.itemSizeCache.clear();
            this.laneAssignments.clear();
            this.pendingMin = null;
          }
          if (this.measurementsCache.length === 0 && !this.lanesSettling) {
            this.measurementsCache = this.options.initialMeasurementsCache;
            this.measurementsCache.forEach((item) => {
              this.itemSizeCache.set(item.key, item.size);
            });
          }
          const min = this.lanesSettling ? 0 : this.pendingMin ?? 0;
          this.pendingMin = null;
          if (this.lanesSettling && this.measurementsCache.length === count) {
            this.lanesSettling = false;
          }
          if (lanes === 1) {
            const need = count * 2;
            let flat = this._flatMeasurements;
            if (!flat || flat.length < need) {
              const next = new Float64Array(need);
              if (flat && min > 0) next.set(flat.subarray(0, min * 2));
              flat = next;
              this._flatMeasurements = flat;
            }
            let runningStart;
            if (min === 0) {
              runningStart = paddingStart + scrollMargin;
            } else {
              const prevIdx = min - 1;
              runningStart = flat[prevIdx * 2] + flat[prevIdx * 2 + 1] + gap;
            }
            for (let i = min; i < count; i++) {
              const key = getItemKey(i);
              const measuredSize = itemSizeCache.get(key);
              const size = typeof measuredSize === "number" ? measuredSize : this.options.estimateSize(i);
              flat[i * 2] = runningStart;
              flat[i * 2 + 1] = size;
              runningStart += size + gap;
            }
            const view = createLazyMeasurementsView(count, flat, getItemKey);
            this.measurementsCache = view;
            return view;
          }
          const measurements = this.measurementsCache.slice(0, min);
          const laneLastIndex = new Array(lanes).fill(
            void 0
          );
          const laneEnds = new Float64Array(lanes);
          let filledLanes = 0;
          for (let m = 0; m < min; m++) {
            const item = measurements[m];
            if (item) {
              if (laneLastIndex[item.lane] === void 0) filledLanes++;
              laneLastIndex[item.lane] = m;
              laneEnds[item.lane] = item.end;
            }
          }
          for (let i = min; i < count; i++) {
            const key = getItemKey(i);
            const cachedLane = this.laneAssignments.get(i);
            let lane;
            let start;
            const shouldCacheLane = laneAssignmentMode === "estimate" || itemSizeCache.has(key);
            if (cachedLane !== void 0 && this.options.lanes > 1) {
              lane = cachedLane;
              const prevIndex = laneLastIndex[lane];
              const prevInLane = prevIndex !== void 0 ? measurements[prevIndex] : void 0;
              start = prevInLane ? prevInLane.end + gap : paddingStart + scrollMargin;
            } else if (filledLanes === lanes) {
              let bestLane = 0;
              let bestEnd = laneEnds[0];
              let bestIdx = laneLastIndex[0];
              for (let l = 1; l < lanes; l++) {
                const e = laneEnds[l];
                if (e < bestEnd || e === bestEnd && laneLastIndex[l] < bestIdx) {
                  bestLane = l;
                  bestEnd = e;
                  bestIdx = laneLastIndex[l];
                }
              }
              lane = bestLane;
              start = bestEnd + gap;
              if (shouldCacheLane) {
                this.laneAssignments.set(i, lane);
              }
            } else {
              lane = i % this.options.lanes;
              start = paddingStart + scrollMargin;
              if (shouldCacheLane) {
                this.laneAssignments.set(i, lane);
              }
            }
            const measuredSize = itemSizeCache.get(key);
            const size = typeof measuredSize === "number" ? measuredSize : this.options.estimateSize(i);
            const end = start + size;
            measurements[i] = {
              index: i,
              start,
              size,
              end,
              key,
              lane
            };
            if (laneLastIndex[lane] === void 0) filledLanes++;
            laneLastIndex[lane] = i;
            laneEnds[lane] = end;
          }
          this.measurementsCache = measurements;
          return measurements;
        },
        {
          key: "getMeasurements",
          debug: () => this.options.debug
        }
      );
      this.calculateRange = memo(
        () => [
          this.getMeasurements(),
          this.getSize(),
          this.getScrollOffset(),
          this.options.lanes
        ],
        (measurements, outerSize, scrollOffset, lanes) => {
          if (measurements.length === 0 || outerSize === 0) {
            this.range = null;
            return null;
          }
          this.range = calculateRangeImpl(
            measurements,
            outerSize,
            scrollOffset,
            lanes,
            // Pass the typed array so binary search + forward-walk can read
            // start/end directly from Float64Array, skipping the Proxy traps.
            lanes === 1 && this._flatMeasurements != null ? this._flatMeasurements : null
          );
          return this.range;
        },
        {
          key: "calculateRange",
          debug: () => this.options.debug
        }
      );
      this.getVirtualIndexes = memo(
        () => {
          let startIndex = null;
          let endIndex = null;
          const range = this.calculateRange();
          if (range) {
            startIndex = range.startIndex;
            endIndex = range.endIndex;
          }
          this.maybeNotify.updateDeps([this.isScrolling, startIndex, endIndex]);
          return [
            this.options.rangeExtractor,
            this.options.overscan,
            this.options.count,
            startIndex,
            endIndex
          ];
        },
        (rangeExtractor, overscan, count, startIndex, endIndex) => {
          return startIndex === null || endIndex === null ? [] : rangeExtractor({
            startIndex,
            endIndex,
            overscan,
            count
          });
        },
        {
          key: "getVirtualIndexes",
          debug: () => this.options.debug
        }
      );
      this.indexFromElement = (node) => {
        const attributeName = this.options.indexAttribute;
        const indexStr = node.getAttribute(attributeName);
        if (!indexStr) {
          console.warn(
            `Missing attribute name '${attributeName}={index}' on measured element.`
          );
          return -1;
        }
        return parseInt(indexStr, 10);
      };
      this.shouldMeasureDuringScroll = (index) => {
        var _a;
        if (!this.scrollState || this.scrollState.behavior !== "smooth") {
          return true;
        }
        const scrollIndex = this.scrollState.index ?? ((_a = this.getVirtualItemForOffset(this.scrollState.lastTargetOffset)) == null ? void 0 : _a.index);
        if (scrollIndex !== void 0 && this.range) {
          const bufferSize = Math.max(
            this.options.overscan,
            Math.ceil((this.range.endIndex - this.range.startIndex) / 2)
          );
          const minIndex = Math.max(0, scrollIndex - bufferSize);
          const maxIndex = Math.min(
            this.options.count - 1,
            scrollIndex + bufferSize
          );
          return index >= minIndex && index <= maxIndex;
        }
        return true;
      };
      this.measureElement = (node) => {
        if (!node) {
          this.elementsCache.forEach((cached, key2) => {
            if (!cached.isConnected) {
              this.observer.unobserve(cached);
              this.elementsCache.delete(key2);
            }
          });
          return;
        }
        const index = this.indexFromElement(node);
        if (!this.isIndexInRange(index)) return;
        const key = this.options.getItemKey(index);
        const prevNode = this.elementsCache.get(key);
        if (prevNode !== node) {
          if (prevNode) {
            this.observer.unobserve(prevNode);
          }
          this.observer.observe(node);
          this.elementsCache.set(key, node);
        }
        if ((!this.isScrolling || this.scrollState) && this.shouldMeasureDuringScroll(index)) {
          this.resizeItem(index, this.options.measureElement(node, void 0, this));
        }
      };
      this.resizeItem = (index, size) => {
        var _a, _b;
        if (!this.isIndexInRange(index)) return;
        let cachedSize;
        let itemStart;
        let key;
        const flat = this._flatMeasurements;
        if (this.options.lanes === 1 && flat !== null) {
          key = this.options.getItemKey(index);
          itemStart = flat[index * 2];
          cachedSize = flat[index * 2 + 1];
        } else {
          const item = this.measurementsCache[index];
          if (!item) return;
          key = item.key;
          itemStart = item.start;
          cachedSize = item.size;
        }
        const itemSize = this.itemSizeCache.get(key) ?? cachedSize;
        const delta = size - itemSize;
        if (delta !== 0) {
          const wasAtEnd = this.options.anchorTo === "end" && ((_a = this.scrollState) == null ? void 0 : _a.behavior) !== "smooth" && this.getVirtualDistanceFromEnd() <= this.options.scrollEndThreshold;
          const prevTotalSize = wasAtEnd ? this.getTotalSize() : 0;
          const scrollOffsetWithAdj = this.getScrollOffset() + this.scrollAdjustments;
          const isFirstMeasure = !this.itemSizeCache.has(key);
          const defaultShouldAdjust = isFirstMeasure ? (
            // First measurement: compensate any item whose top sits above the
            // fold — the estimate→actual delta must be corrected regardless of
            // scroll direction, since the whole estimated block was above it.
            itemStart < scrollOffsetWithAdj
          ) : (
            // Re-measurement: only compensate an item that is ENTIRELY above the
            // fold. An item that merely *spans* the fold (top above, bottom
            // below — e.g. a streaming chat message growing at its bottom)
            // changes size *below* the anchor point, so shifting scrollTop by the
            // delta would drag the viewport downward on every growth (#1218).
            // Also skip during backward scroll to avoid the "items jump while
            // scrolling up" cascade.
            itemStart + itemSize <= scrollOffsetWithAdj && this.scrollDirection !== "backward"
          );
          const shouldAdjustScroll = ((_b = this.scrollState) == null ? void 0 : _b.behavior) !== "smooth" && (this.shouldAdjustScrollPositionOnItemSizeChange !== void 0 ? this.shouldAdjustScrollPositionOnItemSizeChange(
            // The callback expects a VirtualItem; build one lazily only
            // when the consumer actually supplied a custom predicate.
            this.measurementsCache[index] ?? {
              index,
              key,
              start: itemStart,
              size: cachedSize,
              end: itemStart + cachedSize,
              lane: 0
            },
            delta,
            this
          ) : defaultShouldAdjust);
          if (this.pendingMin === null || index < this.pendingMin) {
            this.pendingMin = index;
          }
          this.itemSizeCache.set(key, size);
          this.itemSizeCacheVersion++;
          let adjustedSync = false;
          if (wasAtEnd) {
            adjustedSync = this.applyScrollAdjustment(
              this.getTotalSize() - prevTotalSize
            );
          } else if (shouldAdjustScroll) {
            adjustedSync = this.applyScrollAdjustment(delta);
          }
          this.notify(adjustedSync);
        }
      };
      this.getVirtualItems = memo(
        () => [this.getVirtualIndexes(), this.getMeasurements()],
        (indexes, measurements) => {
          const virtualItems = [];
          for (let k = 0, len = indexes.length; k < len; k++) {
            const i = indexes[k];
            const measurement = measurements[i];
            virtualItems.push(measurement);
          }
          return virtualItems;
        },
        {
          key: "getVirtualItems",
          debug: () => this.options.debug
        }
      );
      this.getVirtualItemForOffset = (offset) => {
        const measurements = this.getMeasurements();
        if (measurements.length === 0) {
          return void 0;
        }
        const flat = this._flatMeasurements;
        const useFlat = this.options.lanes === 1 && flat != null;
        const idx = findNearestBinarySearch(
          0,
          measurements.length - 1,
          useFlat ? (i) => flat[i * 2] : (i) => notUndefined(measurements[i]).start,
          offset
        );
        return notUndefined(measurements[idx]);
      };
      this.getMaxScrollOffset = () => {
        if (!this.scrollElement) return 0;
        if ("scrollHeight" in this.scrollElement) {
          return this.options.horizontal ? this.scrollElement.scrollWidth - this.scrollElement.clientWidth : this.scrollElement.scrollHeight - this.scrollElement.clientHeight;
        } else {
          const doc = this.scrollElement.document.documentElement;
          return this.options.horizontal ? doc.scrollWidth - this.scrollElement.innerWidth : doc.scrollHeight - this.scrollElement.innerHeight;
        }
      };
      this.getVirtualDistanceFromEnd = () => {
        return Math.max(
          this.getTotalSize() - this.getSize() - this.getScrollOffset(),
          0
        );
      };
      this.getDistanceFromEnd = () => {
        return Math.max(this.getMaxScrollOffset() - this.getScrollOffset(), 0);
      };
      this.isAtEnd = (threshold = this.options.scrollEndThreshold) => {
        return this.getDistanceFromEnd() <= threshold;
      };
      this.getOffsetForAlignment = (toOffset, align, itemSize = 0) => {
        if (!this.scrollElement) return 0;
        const size = this.getSize();
        const scrollOffset = this.getScrollOffset();
        if (align === "auto") {
          align = toOffset >= scrollOffset + size ? "end" : "start";
        }
        if (align === "center") {
          toOffset += (itemSize - size) / 2;
        } else if (align === "end") {
          toOffset -= size;
        }
        const maxOffset = this.getMaxScrollOffset();
        return Math.max(Math.min(maxOffset, toOffset), 0);
      };
      this.getOffsetForIndex = (index, align = "auto") => {
        index = Math.max(0, Math.min(index, this.options.count - 1));
        const size = this.getSize();
        const scrollOffset = this.getScrollOffset();
        const item = this.measurementsCache[index];
        if (!item) return;
        if (align === "auto") {
          if (item.end >= scrollOffset + size - this.options.scrollPaddingEnd) {
            align = "end";
          } else if (item.start <= scrollOffset + this.options.scrollPaddingStart) {
            align = "start";
          } else {
            return [scrollOffset, align];
          }
        }
        if (align === "end" && index === this.options.count - 1) {
          return [this.getMaxScrollOffset(), align];
        }
        const toOffset = align === "end" ? item.end + this.options.scrollPaddingEnd : item.start - this.options.scrollPaddingStart;
        return [
          this.getOffsetForAlignment(toOffset, align, item.size),
          align
        ];
      };
      this.scrollToOffset = (toOffset, { align = "start", behavior = "auto" } = {}) => {
        this._iosDeferredAdjustment = 0;
        const offset = this.getOffsetForAlignment(toOffset, align);
        const now = this.now();
        this.scrollState = {
          index: null,
          align,
          behavior,
          startedAt: now,
          lastTargetOffset: offset,
          stableFrames: 0
        };
        this._scrollToOffset(offset, { adjustments: void 0, behavior });
        this.scheduleScrollReconcile();
      };
      this.scrollToIndex = (index, {
        align: initialAlign = "auto",
        behavior = "auto"
      } = {}) => {
        this._iosDeferredAdjustment = 0;
        index = Math.max(0, Math.min(index, this.options.count - 1));
        const offsetInfo = this.getOffsetForIndex(index, initialAlign);
        if (!offsetInfo) {
          return;
        }
        const [offset, align] = offsetInfo;
        const now = this.now();
        this.scrollState = {
          index,
          align,
          behavior,
          startedAt: now,
          lastTargetOffset: offset,
          stableFrames: 0
        };
        this._scrollToOffset(offset, { adjustments: void 0, behavior });
        this.scheduleScrollReconcile();
      };
      this.scrollBy = (delta, { behavior = "auto" } = {}) => {
        const offset = this.getScrollOffset() + delta;
        const now = this.now();
        this.scrollState = {
          index: null,
          align: "start",
          behavior,
          startedAt: now,
          lastTargetOffset: offset,
          stableFrames: 0
        };
        this._scrollToOffset(offset, { adjustments: void 0, behavior });
        this.scheduleScrollReconcile();
      };
      this.scrollToEnd = ({ behavior = "auto" } = {}) => {
        if (this.options.count > 0) {
          this.scrollToIndex(this.options.count - 1, {
            align: "end",
            behavior
          });
          return;
        }
        this.scrollToOffset(Math.max(this.getTotalSize() - this.getSize(), 0), {
          behavior
        });
      };
      this.getTotalSize = () => {
        var _a;
        const measurements = this.getMeasurements();
        let end;
        if (measurements.length === 0) {
          end = this.options.paddingStart;
        } else if (this.options.lanes === 1) {
          const lastIdx = measurements.length - 1;
          const flat = this._flatMeasurements;
          if (flat != null) {
            end = flat[lastIdx * 2] + flat[lastIdx * 2 + 1];
          } else {
            end = ((_a = measurements[lastIdx]) == null ? void 0 : _a.end) ?? 0;
          }
        } else {
          const endByLane = Array(this.options.lanes).fill(null);
          let endIndex = measurements.length - 1;
          while (endIndex >= 0 && endByLane.some((val) => val === null)) {
            const item = measurements[endIndex];
            if (endByLane[item.lane] === null) {
              endByLane[item.lane] = item.end;
            }
            endIndex--;
          }
          end = Math.max(...endByLane.filter((val) => val !== null));
        }
        return Math.max(
          end - this.options.scrollMargin + this.options.paddingEnd,
          0
        );
      };
      this.takeSnapshot = () => {
        const snapshot = [];
        if (this.itemSizeCache.size === 0) return snapshot;
        const m = this.getMeasurements();
        for (const item of m) {
          if (item && this.itemSizeCache.has(item.key)) {
            snapshot.push({
              index: item.index,
              key: item.key,
              start: item.start,
              size: item.size,
              end: item.end,
              lane: item.lane
            });
          }
        }
        return snapshot;
      };
      this._scrollToOffset = (offset, {
        adjustments,
        behavior
      }) => {
        this._intendedScrollOffset = offset + (adjustments ?? 0);
        this.options.scrollToFn(offset, { behavior, adjustments }, this);
      };
      this.measure = () => {
        this.pendingMin = null;
        this.itemSizeCache.clear();
        this.laneAssignments.clear();
        this.itemSizeCacheVersion++;
        this.notify(false);
      };
      this.setOptions(opts);
    }
    // Returns `true` when it performed a synchronous `scrollTop` write this
    // tick, `false` when the delta was zero or the write was deferred (iOS).
    // `resizeItem` uses that to decide whether the follow-up `notify` must be
    // synchronous so the grown transforms commit in the same paint (#1227).
    applyScrollAdjustment(delta, behavior) {
      if (delta === 0) return false;
      if (this.options.debug) {
        console.info("correction", delta);
      }
      if (isIOSWebKit() && (this.isScrolling || this._iosTouching || this._iosJustTouchEnded)) {
        this._iosDeferredAdjustment += delta;
        return false;
      } else {
        this._scrollToOffset(this.getScrollOffset(), {
          adjustments: this.scrollAdjustments += delta,
          behavior
        });
        if (this.scrollOffset !== null) {
          this.scrollOffset += this.scrollAdjustments;
          if (this.scrollOffset < 0) this.scrollOffset = 0;
          this.scrollAdjustments = 0;
        }
        return true;
      }
    }
    scheduleScrollReconcile() {
      if (!this.targetWindow) {
        this.scrollState = null;
        return;
      }
      if (this.rafId != null) return;
      this.rafId = this.targetWindow.requestAnimationFrame(() => {
        this.rafId = null;
        this.reconcileScroll();
      });
    }
    reconcileScroll() {
      if (!this.scrollState) return;
      const el = this.scrollElement;
      if (!el) return;
      const MAX_RECONCILE_MS = 5e3;
      if (this.now() - this.scrollState.startedAt > MAX_RECONCILE_MS) {
        this.scrollState = null;
        return;
      }
      const offsetInfo = this.scrollState.index != null ? this.getOffsetForIndex(this.scrollState.index, this.scrollState.align) : void 0;
      const targetOffset = offsetInfo ? offsetInfo[0] : this.scrollState.lastTargetOffset;
      const STABLE_FRAMES = 1;
      const targetChanged = targetOffset !== this.scrollState.lastTargetOffset;
      if (!targetChanged && approxEqual(targetOffset, this.getScrollOffset())) {
        this.scrollState.stableFrames++;
        if (this.scrollState.stableFrames >= STABLE_FRAMES) {
          if (this.getScrollOffset() !== targetOffset) {
            this._scrollToOffset(targetOffset, {
              adjustments: void 0,
              behavior: "auto"
            });
          }
          this.scrollState = null;
          return;
        }
      } else {
        this.scrollState.stableFrames = 0;
        if (targetChanged) {
          const viewport = this.getSize() || 600;
          const distance = Math.abs(targetOffset - this.getScrollOffset());
          const keepSmooth = this.scrollState.behavior === "smooth" && distance > viewport;
          this.scrollState.lastTargetOffset = targetOffset;
          if (!keepSmooth) {
            this.scrollState.behavior = "auto";
          }
          this._scrollToOffset(targetOffset, {
            adjustments: void 0,
            behavior: keepSmooth ? "smooth" : "auto"
          });
        }
      }
      this.scheduleScrollReconcile();
    }
  };
  var findNearestBinarySearch = (low, high, getCurrentValue, value) => {
    while (low <= high) {
      const middle = (low + high) / 2 | 0;
      const currentValue = getCurrentValue(middle);
      if (currentValue < value) {
        low = middle + 1;
      } else if (currentValue > value) {
        high = middle - 1;
      } else {
        return middle;
      }
    }
    if (low > 0) {
      return low - 1;
    } else {
      return 0;
    }
  };
  function findNearestBinarySearchFlat(flat, high, value) {
    let low = 0;
    while (low <= high) {
      const middle = (low + high) / 2 | 0;
      const currentValue = flat[middle * 2];
      if (currentValue < value) {
        low = middle + 1;
      } else if (currentValue > value) {
        high = middle - 1;
      } else {
        return middle;
      }
    }
    return low > 0 ? low - 1 : 0;
  }
  function calculateRangeImpl(measurements, outerSize, scrollOffset, lanes, flat) {
    const lastIndex = measurements.length - 1;
    if (measurements.length <= lanes) {
      return { startIndex: 0, endIndex: lastIndex };
    }
    if (lanes === 1 && flat !== null) {
      const startIndex2 = findNearestBinarySearchFlat(
        flat,
        lastIndex,
        scrollOffset
      );
      let endIndex2 = startIndex2;
      const limit = scrollOffset + outerSize;
      while (endIndex2 < lastIndex && flat[endIndex2 * 2] + flat[endIndex2 * 2 + 1] < limit) {
        endIndex2++;
      }
      return { startIndex: startIndex2, endIndex: endIndex2 };
    }
    const getStart = (index) => measurements[index].start;
    let startIndex = findNearestBinarySearch(0, lastIndex, getStart, scrollOffset);
    let endIndex = startIndex;
    if (lanes === 1) {
      while (endIndex < lastIndex && measurements[endIndex].end < scrollOffset + outerSize) {
        endIndex++;
      }
    } else if (lanes > 1) {
      const endPerLane = Array(lanes).fill(0);
      while (endIndex < lastIndex && endPerLane.some((pos) => pos < scrollOffset + outerSize)) {
        const item = measurements[endIndex];
        endPerLane[item.lane] = item.end;
        endIndex++;
      }
      const startPerLane = Array(lanes).fill(scrollOffset + outerSize);
      while (startIndex >= 0 && startPerLane.some((pos) => pos >= scrollOffset)) {
        const item = measurements[startIndex];
        startPerLane[item.lane] = item.start;
        startIndex--;
      }
      startIndex = Math.max(0, startIndex - startIndex % lanes);
      endIndex = Math.min(lastIndex, endIndex + (lanes - 1 - endIndex % lanes));
    }
    return { startIndex, endIndex };
  }

  // timeline-virtual.js
  var VirtualTimeline = class {
    /**
     * @param {{
     *   store: import('./timeline-store.js').TimelineItemStore,
     *   dom: ReturnType<import('./timeline-dom.js').createDomRenderer>,
     *   onLoadOlder?: () => void
     * }} options
     */
    constructor(options) {
      this.store = options.store;
      this.dom = options.dom;
      this.onLoadOlder = options.onLoadOlder || null;
      this.virtualizer = null;
      this._unmount = null;
      this.mountedById = /* @__PURE__ */ new Map();
      this.renderFrame = 0;
      this.hasOlderMessages = false;
      this.loadOlderPending = false;
      this._lastCount = -1;
      this._scrollListener = this._onScroll.bind(this);
      this._sentinelObserver = null;
    }
    init() {
      const scroller = this.dom.getChatScroller();
      const windowEl = document.getElementById("virtual-window");
      if (!scroller || !windowEl) return;
      this._buildVirtualizerOptions = (count) => ({
        count,
        getScrollElement: () => this.dom.getChatScroller(),
        estimateSize: (index) => {
          const item = this.store.items[index];
          return (item ? item.estimatedHeight : 80) + 20;
        },
        overscan: 8,
        scrollToFn: elementScroll,
        observeElementRect,
        observeElementOffset,
        onChange: () => {
          this._schedulePaint();
        }
      });
      this.virtualizer = new Virtualizer(this._buildVirtualizerOptions(this.store.count));
      this._lastCount = this.store.count;
      if (typeof this.virtualizer._didMount === "function") {
        this._unmount = this.virtualizer._didMount();
      }
      this._willUpdate();
      scroller.addEventListener("scroll", this._scrollListener, { passive: true });
      scroller.addEventListener("wheel", (e) => {
        if (e.deltaY < 0) this.dom.state.autoScrollEnabled = false;
      }, { passive: true });
      scroller.addEventListener("touchmove", () => {
        if (!this.dom.isNearBottom()) this.dom.state.autoScrollEnabled = false;
      }, { passive: true });
      this._bindSentinel();
      this._paint();
    }
    _willUpdate() {
      if (this.virtualizer && typeof this.virtualizer._willUpdate === "function") {
        this.virtualizer._willUpdate();
      }
    }
    /** @param {number} count */
    _setCount(count) {
      if (!this.virtualizer || !this._buildVirtualizerOptions) return;
      if (count === this._lastCount) {
        this._willUpdate();
        return;
      }
      this._lastCount = count;
      this.virtualizer.setOptions(this._buildVirtualizerOptions(count));
      this._willUpdate();
    }
    _bindSentinel() {
      const sentinel = document.getElementById("load-older-sentinel");
      if (!sentinel || typeof IntersectionObserver !== "function") return;
      if (this._sentinelObserver) this._sentinelObserver.disconnect();
      this._sentinelObserver = new IntersectionObserver((entries) => {
        entries.forEach((entry) => {
          if (!entry.isIntersecting || !this.hasOlderMessages || this.loadOlderPending) return;
          this.loadOlderPending = true;
          sentinel.dataset.loading = "1";
          if (this.onLoadOlder) this.onLoadOlder();
          this.dom.post({ type: "loadOlder" });
        });
      }, { root: this.dom.getChatScroller(), rootMargin: "120px" });
      this._sentinelObserver.observe(sentinel);
    }
    setOlderMessagesAvailable(available) {
      this.hasOlderMessages = !!available;
      this.loadOlderPending = false;
      const sentinel = document.getElementById("load-older-sentinel");
      if (sentinel) {
        sentinel.hidden = !available;
        delete sentinel.dataset.loading;
      }
    }
    reset() {
      this.mountedById.clear();
      const windowEl = document.getElementById("virtual-window");
      if (windowEl) windowEl.innerHTML = "";
      if (this.virtualizer) {
        this._setCount(0);
        this.virtualizer.measure();
      }
      this.dom.updateEmptyStateVisibility();
    }
    /** @param {boolean} [force] */
    scrollToBottom(force) {
      const scroller = this.dom.getChatScroller();
      if (!scroller) return;
      if (!force && (!this.dom.state.autoScrollEnabled || this.dom.hasActiveSelection())) return;
      this._willUpdate();
      scroller.scrollTop = scroller.scrollHeight;
      this._schedulePaint();
    }
    _onScroll() {
      this.dom.state.autoScrollEnabled = this.dom.isNearBottom();
      this._schedulePaint();
    }
    _schedulePaint() {
      if (this.renderFrame) return;
      this.renderFrame = requestAnimationFrame(() => {
        this.renderFrame = 0;
        this._paint();
      });
    }
    refresh() {
      if (!this.virtualizer) {
        this.init();
        return;
      }
      this._setCount(this.store.count);
      this._schedulePaint();
    }
    /**
     * When getVirtualItems is empty (e.g. viewport not measured yet), estimate a
     * window around the current scroll offset — not always the list tail.
     * @returns {Array<{ index: number, start: number, size: number, end: number, key: number, lane: number }>}
     */
    _fallbackVirtualItems() {
      const count = this.store.count;
      if (count <= 0) return [];
      const scroller = this.dom.getChatScroller();
      const scrollTop = scroller ? scroller.scrollTop : 0;
      const viewport = scroller && scroller.clientHeight > 0 ? scroller.clientHeight : 600;
      const windowSize = 40;
      let startIndex = 0;
      let offset = 0;
      for (let i = 0; i < count; i++) {
        const size = (this.store.items[i]?.estimatedHeight || 80) + 20;
        if (offset + size > scrollTop) {
          startIndex = i;
          break;
        }
        offset += size;
        startIndex = i;
      }
      startIndex = Math.max(0, startIndex - 4);
      const endIndex = Math.min(count - 1, startIndex + windowSize - 1);
      const items = [];
      for (let index = startIndex; index <= endIndex; index++) {
        const start = this._estimateOffset(index);
        const size = (this.store.items[index]?.estimatedHeight || 80) + 20;
        items.push({ index, start, size, end: start + size, key: index, lane: 0 });
      }
      void viewport;
      return items;
    }
    _paint() {
      if (!this.virtualizer) return;
      const windowEl = document.getElementById("virtual-window");
      if (!windowEl) return;
      this._willUpdate();
      if (this._lastCount !== this.store.count) {
        this._setCount(this.store.count);
      }
      let virtualItems = this.virtualizer.getVirtualItems();
      if (virtualItems.length === 0 && this.store.count > 0) {
        virtualItems = this._fallbackVirtualItems();
      }
      const liveIndices = /* @__PURE__ */ new Set();
      this.store.items.forEach((item, index) => {
        if (item.live) liveIndices.add(index);
      });
      const renderIndices = new Set(virtualItems.map((v) => v.index));
      liveIndices.forEach((i) => renderIndices.add(i));
      const nextMounted = /* @__PURE__ */ new Map();
      const usedIds = /* @__PURE__ */ new Set();
      renderIndices.forEach((index) => {
        const item = this.store.items[index];
        if (!item) return;
        const virtualRow = virtualItems.find((v) => v.index === index);
        const start = virtualRow ? virtualRow.start : this._estimateOffset(index);
        let row = this.mountedById.get(item.id);
        const version = String(item.version);
        if (row && row.dataset.timelineVersion === version) {
          row.setAttribute("data-index", String(index));
          row.style.transform = "translateY(" + start + "px)";
          nextMounted.set(item.id, row);
          usedIds.add(item.id);
          return;
        }
        if (row && row.parentNode) {
          this._unbindRemeasure(row);
          row.parentNode.removeChild(row);
        }
        row = this.dom.renderItemRow(item);
        if (!row) return;
        row.classList.add("virtual-row");
        row.setAttribute("data-index", String(index));
        row.style.position = "absolute";
        row.style.left = "0";
        row.style.right = "0";
        row.style.top = "0";
        row.style.transform = "translateY(" + start + "px)";
        row.style.width = "100%";
        windowEl.appendChild(row);
        this.dom.enhanceCodeBlocks(row);
        this._remeasureRow(row);
        this._bindRemeasure(row);
        nextMounted.set(item.id, row);
        usedIds.add(item.id);
      });
      this.mountedById.forEach((row, id) => {
        if (!usedIds.has(id) && row.parentNode) {
          this._unbindRemeasure(row);
          row.parentNode.removeChild(row);
        }
      });
      this.mountedById = nextMounted;
      const totalSize = this.virtualizer.getTotalSize() || this.store.estimateTotalSize();
      windowEl.style.height = totalSize + "px";
      this.dom.updateEmptyStateVisibility();
    }
    /** @param {number} index */
    _estimateOffset(index) {
      let offset = 0;
      for (let i = 0; i < index; i++) {
        offset += (this.store.items[i]?.estimatedHeight || 80) + 20;
      }
      return offset;
    }
    /** @param {HTMLElement} row */
    _bindRemeasure(row) {
      if (!row || row.__remeasureBound) return;
      row.__remeasureBound = true;
      let lastHeight = -1;
      const onChange = (height) => {
        if (height === lastHeight) return;
        lastHeight = height;
        this._remeasureRow(row);
      };
      if (typeof ResizeObserver === "function") {
        const ro = new ResizeObserver((entries) => {
          const entry = entries && entries[entries.length - 1];
          const box = entry && (entry.contentRect || entry.borderBoxSize);
          onChange(box ? box.height : row.getBoundingClientRect().height);
        });
        ro.observe(row);
        row.__remeasureRO = ro;
      } else {
        const details = row.querySelector("details");
        if (details) details.addEventListener("toggle", () => onChange(row.getBoundingClientRect().height));
      }
    }
    /** @param {HTMLElement} row */
    _unbindRemeasure(row) {
      if (row && row.__remeasureRO) {
        try {
          row.__remeasureRO.disconnect();
        } catch (_e) {
        }
        row.__remeasureRO = null;
      }
    }
    /**
     * Re-measure one mounted row and sync both the virtualizer cache and the
     * store's estimatedHeight, then repaint so following rows shift correctly.
     * @param {HTMLElement} row
     */
    _remeasureRow(row) {
      if (!row || !row.isConnected || !this.virtualizer) return;
      this.virtualizer.measureElement(row);
      const measured = row.getBoundingClientRect().height;
      if (measured > 0) {
        const itemId = row.dataset.timelineItemId;
        if (itemId) {
          const index = this.store.indexById.get(itemId);
          const item = index != null ? this.store.items[index] : null;
          if (item) item.estimatedHeight = Math.max(1, Math.round(measured) - 20);
        }
      }
      this._schedulePaint();
    }
    /**
     * After an in-place DOM patch, sync the row's version stamp with the store so
     * the next paint updates position instead of rebuilding the whole row.
     * @param {HTMLElement} row
     * @param {string} itemId
     */
    _syncMountedVersion(row, itemId) {
      if (!row || !itemId) return;
      const index = this.store.indexById.get(itemId);
      if (index == null) return;
      row.dataset.timelineVersion = String(this.store.items[index].version);
    }
    /**
     * @param {Array<string | object>} events
     * @param {{
     *   prepend?: boolean,
     *   append?: boolean,
     *   replace?: boolean,
     *   hasOlderMessages?: boolean,
     *   forceScroll?: boolean
     * }} [opts]
     */
    ingestEvents(events, opts) {
      const prepend = !!(opts && opts.prepend);
      const append = !!(opts && opts.append);
      const replace = !!(opts && opts.replace) || !prepend && !append;
      const previousSize = this.virtualizer ? this.virtualizer.getTotalSize() : this.store.estimateTotalSize();
      const scroller = this.dom.getChatScroller();
      const previousTop = scroller ? scroller.scrollTop : 0;
      this.dom.beginBatch();
      this.dom.state.trackReasoningDuration = false;
      this.store.ingestEvents(events, { prepend, append: append && !replace });
      this.dom.state.trackReasoningDuration = true;
      this.dom.endBatch(false);
      if (!this.virtualizer) {
        this.init();
      } else {
        this._setCount(this.store.count);
      }
      if (prepend && scroller && this.virtualizer) {
        this._paint();
        const delta = this.virtualizer.getTotalSize() - previousSize;
        scroller.scrollTop = previousTop + Math.max(0, delta);
        this._paint();
        const afterSize = this.virtualizer.getTotalSize();
        const measureDelta = afterSize - (previousSize + Math.max(0, delta));
        if (Math.abs(measureDelta) > 1) {
          scroller.scrollTop = previousTop + Math.max(0, delta) + measureDelta;
        }
      } else {
        this.refresh();
        if (opts && opts.forceScroll) {
          this.scrollToBottom(true);
        } else if (!append) {
          this.scrollToBottom(false);
        }
      }
      if (opts && "hasOlderMessages" in opts) {
        this.setOlderMessagesAvailable(!!opts.hasOlderMessages);
      }
    }
    /**
     * Apply incremental event: update store and refresh visible rows.
     * @param {object} event
     */
    applyIncrementalEvent(event) {
      if (event.type === "RESET_TIMELINE") {
        this.store.clear();
        this.reset();
        this.dom.resetTimeline();
        return;
      }
      const patchTypes = /* @__PURE__ */ new Set([
        "TOOL_CALL_ARGS",
        "TOOL_CALL_END",
        "TOOL_CALL_OUTPUT",
        "TOOL_CALL_RESULT",
        "TOOL_APPROVAL_REQUEST",
        "TOOL_APPROVAL_RESOLVED",
        "PLAN_CLARIFY_RESOLVED"
      ]);
      if (event.type === "TEXT_MESSAGE_START") {
        this.dom.state.currentAssistantEl = null;
        this.dom.state.assistantStarted[event.messageId] = false;
        return;
      }
      if (event.type === "TEXT_MESSAGE_END") {
        this.dom.state.currentAssistantEl = null;
        return;
      }
      if (event.type === "RUN_STARTED") {
        this.dom.state.currentAssistantEl = null;
        this.dom.state.currentReasoningEl = null;
        return;
      }
      if (event.type === "REASONING_MESSAGE_START" || event.type === "REASONING_MESSAGE_CONTENT" || event.type === "REASONING_MESSAGE_END") {
        return;
      }
      if (event.type === "TEXT_MESSAGE_CONTENT") {
        if (!this.dom.state.assistantStarted[event.messageId]) {
          this.store.applyEvent({ type: "STATIC_ASSISTANT_HTML", messageId: event.messageId, html: "", streaming: true });
          this.refresh();
        }
        return;
      }
      const result = this.store.applyEvent(event);
      const isTurnActivity = event.type === "TURN_ACTIVITY";
      const isFilesChanged = event.type === "FILES_CHANGED";
      if (isTurnActivity || isFilesChanged) {
        const turnId = this.store.currentTurnId || "orphan";
        const itemId = isTurnActivity ? this.store.activityId(turnId) : this.store.filesId(turnId);
        const mounted = this.mountedById.get(itemId);
        if (mounted) {
          this._patchMounted(mounted, itemId, event);
          if (result.scrollBottom) this.scrollToBottom(false);
          return;
        }
      }
      if (event.type === "STATIC_ASSISTANT_HTML") {
        const itemId = this.store.assistantId(event.messageId || "");
        const mounted = this.mountedById.get(itemId);
        if (mounted) {
          this._patchMounted(mounted, itemId, event);
          if (result.scrollBottom) this.scrollToBottom(false);
          return;
        }
      }
      if (result.reset) {
        this.reset();
        this.dom.resetTimeline();
        return;
      }
      if (patchTypes.has(event.type)) {
        const toolCallId = event.toolCallId || "";
        const itemId = this.store.toolId(toolCallId);
        const mounted = this.mountedById.get(itemId);
        if (mounted) {
          this._patchMounted(mounted, itemId, event);
          return;
        }
      }
      this.refresh();
      if (result.scrollBottom) this.scrollToBottom(false);
    }
    /**
     * Apply an incremental event to a row that is already mounted. DOM builders
     * update this exact row in place (state.patchRow) — they never create new rows
     * or scan the document — then the row's store version is synced so the next
     * paint repositions instead of rebuilding the whole row.
     * @param {HTMLElement} mounted
     * @param {string} itemId
     * @param {object} event
     */
    _patchMounted(mounted, itemId, event) {
      this.dom.state.patchRow = mounted;
      this.dom.state.batchTarget = null;
      try {
        this.dom.handleEvent(event);
      } finally {
        this.dom.state.patchRow = null;
      }
      this._syncMountedVersion(mounted, itemId);
      this._remeasureRow(mounted);
    }
    /** @param {string} itemId */
    getMountedRow(itemId) {
      return this.mountedById.get(itemId) || null;
    }
  };

  // chat-timeline.entry.js
  var store = new TimelineItemStore();
  var dom = createDomRenderer();
  var virtual = null;
  function ensureVirtual() {
    if (!virtual) {
      virtual = new VirtualTimeline({
        store,
        dom,
        onLoadOlder: function() {
          dom.state.autoScrollEnabled = false;
        }
      });
      virtual.init();
    }
    return virtual;
  }
  function handleEvent2(event) {
    ensureVirtual().applyIncrementalEvent(event);
  }
  function replayEvents(events) {
    const v = ensureVirtual();
    dom.beginBatch();
    dom.state.trackReasoningDuration = false;
    v.ingestEvents(events, { replace: true, forceScroll: true });
    dom.state.trackReasoningDuration = true;
    dom.endBatch(true);
  }
  function appendEvents(events) {
    ensureVirtual().ingestEvents(events, { append: true });
  }
  function prependEvents(events, hasOlderMessages) {
    ensureVirtual().ingestEvents(events, { prepend: true, hasOlderMessages });
  }
  function setOlderMessagesAvailable(available) {
    ensureVirtual().setOlderMessagesAvailable(available);
  }
  function handleWebMessage(message) {
    const command = typeof message === "string" ? JSON.parse(message) : message;
    if (!command || !command.command) return;
    if (command.command === "replay" || command.command === "replaceSurface") {
      replayEvents(Array.isArray(command.events) ? command.events : []);
    } else if (command.command === "append" || command.command === "appendEvents") {
      appendEvents(Array.isArray(command.events) ? command.events : []);
    } else if (command.command === "prepend") {
      prependEvents(
        Array.isArray(command.events) ? command.events : [],
        !!command.hasOlderMessages
      );
    } else if (command.command === "historyAvailability") {
      setOlderMessagesAvailable(!!command.hasOlderMessages);
    } else if (command.command === "toolDetail") {
      dom.applyToolDetailPayload(command);
    } else if (command.command === "reset") {
      dom.beginBatch();
      store.clear();
      ensureVirtual().reset();
      dom.resetTimeline();
      dom.endBatch(false);
    }
    if (command.replayComplete && Number.isInteger(command.renderGeneration)) {
      ensureVirtual().scrollToBottom(true);
      dom.post({ type: "replayComplete", renderGeneration: command.renderGeneration });
    }
  }
  function boot() {
    dom.applyChatI18n();
    ensureVirtual();
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.addEventListener("message", function(event) {
        try {
          handleWebMessage(event.data);
        } catch (e) {
          console.warn("chat web message failed", e);
        }
      });
    }
    document.addEventListener("selectionchange", function() {
      if (dom.hasActiveSelection()) dom.state.autoScrollEnabled = false;
      else if (dom.isNearBottom()) dom.state.autoScrollEnabled = true;
    });
  }
  window.handleEvent = handleEvent2;
  window.replayEvents = replayEvents;
  window.appendEvents = appendEvents;
  window.prependEvents = prependEvents;
  window.handleWebMessage = handleWebMessage;
  window.applyThemeUpdate = dom.applyThemeUpdate.bind(dom);
  window.applyChatI18n = dom.applyChatI18n.bind(dom);
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", boot);
  } else {
    boot();
  }
})();
