import { Virtualizer, elementScroll, observeElementOffset, observeElementRect } from '@tanstack/virtual-core';

/**
 * Virtual list renderer for chat timeline items.
 */
export class VirtualTimeline {
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
    /** @type {Virtualizer<HTMLElement, HTMLElement> | null} */
    this.virtualizer = null;
    /** @type {(() => void) | null} */
    this._unmount = null;
    /** @type {Map<string, HTMLElement>} */
    this.mountedById = new Map();
    /** @type {number} */
    this.renderFrame = 0;
    /** @type {boolean} */
    this.hasOlderMessages = false;
    /** @type {boolean} */
    this.loadOlderPending = false;
    /** @type {number} */
    this._lastCount = -1;
    this._scrollListener = this._onScroll.bind(this);
    this._sentinelObserver = null;
  }

  init() {
    const scroller = this.dom.getChatScroller();
    const windowEl = document.getElementById('virtual-window');
    if (!scroller || !windowEl) return;

    // Keep a stable options factory — virtual-core setOptions replaces the whole
    // options object (it does not merge with previous options).
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
    // Vanilla virtual-core requires these lifecycle hooks to attach scroll/rect observers.
    if (typeof this.virtualizer._didMount === 'function') {
      this._unmount = this.virtualizer._didMount();
    }
    this._willUpdate();

    scroller.addEventListener('scroll', this._scrollListener, { passive: true });
    scroller.addEventListener('wheel', (e) => {
      if (e.deltaY < 0) this.dom.state.autoScrollEnabled = false;
    }, { passive: true });
    scroller.addEventListener('touchmove', () => {
      if (!this.dom.isNearBottom()) this.dom.state.autoScrollEnabled = false;
    }, { passive: true });

    this._bindSentinel();
    this._paint();
  }

  _willUpdate() {
    if (this.virtualizer && typeof this.virtualizer._willUpdate === 'function') {
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
    const sentinel = document.getElementById('load-older-sentinel');
    if (!sentinel || typeof IntersectionObserver !== 'function') return;
    if (this._sentinelObserver) this._sentinelObserver.disconnect();
    this._sentinelObserver = new IntersectionObserver((entries) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting || !this.hasOlderMessages || this.loadOlderPending) return;
        this.loadOlderPending = true;
        sentinel.dataset.loading = '1';
        if (this.onLoadOlder) this.onLoadOlder();
        this.dom.post({ type: 'loadOlder' });
      });
    }, { root: this.dom.getChatScroller(), rootMargin: '120px' });
    this._sentinelObserver.observe(sentinel);
  }

  setOlderMessagesAvailable(available) {
    this.hasOlderMessages = !!available;
    this.loadOlderPending = false;
    const sentinel = document.getElementById('load-older-sentinel');
    if (sentinel) {
      sentinel.hidden = !available;
      delete sentinel.dataset.loading;
    }
  }

  reset() {
    this.mountedById.clear();
    const windowEl = document.getElementById('virtual-window');
    if (windowEl) windowEl.innerHTML = '';
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
    // Backup paint path if virtualizer observers are momentarily stale.
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
    /** @type {Array<{ index: number, start: number, size: number, end: number, key: number, lane: number }>} */
    const items = [];
    for (let index = startIndex; index <= endIndex; index++) {
      const start = this._estimateOffset(index);
      const size = (this.store.items[index]?.estimatedHeight || 80) + 20;
      items.push({ index, start, size, end: start + size, key: index, lane: 0 });
    }
    // Keep viewport in the dependency list for lint-free intentional use.
    void viewport;
    return items;
  }

  _paint() {
    if (!this.virtualizer) return;
    const windowEl = document.getElementById('virtual-window');
    if (!windowEl) return;

    this._willUpdate();
    if (this._lastCount !== this.store.count) {
      this._setCount(this.store.count);
    }

    let virtualItems = this.virtualizer.getVirtualItems();
    if (virtualItems.length === 0 && this.store.count > 0) {
      virtualItems = this._fallbackVirtualItems();
    }

    const liveIndices = new Set();
    this.store.items.forEach((item, index) => {
      if (item.live) liveIndices.add(index);
    });

    /** @type {Set<number>} */
    const renderIndices = new Set(virtualItems.map((v) => v.index));
    liveIndices.forEach((i) => renderIndices.add(i));

    const nextMounted = new Map();
    const usedIds = new Set();

    renderIndices.forEach((index) => {
      const item = this.store.items[index];
      if (!item) return;
      const virtualRow = virtualItems.find((v) => v.index === index);
      const start = virtualRow ? virtualRow.start : this._estimateOffset(index);

      let row = this.mountedById.get(item.id);
      const version = String(item.version);
      if (row && row.dataset.timelineVersion === version) {
        row.setAttribute('data-index', String(index));
        row.style.transform = 'translateY(' + start + 'px)';
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
      row.classList.add('virtual-row');
      row.setAttribute('data-index', String(index));
      row.style.position = 'absolute';
      row.style.left = '0';
      row.style.right = '0';
      row.style.top = '0';
      row.style.transform = 'translateY(' + start + 'px)';
      row.style.width = '100%';
      windowEl.appendChild(row);
      // Enhance first, then measure — code-block chrome participates in the slot height.
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
    windowEl.style.height = totalSize + 'px';
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
    if (typeof ResizeObserver === 'function') {
      const ro = new ResizeObserver((entries) => {
        const entry = entries && entries[entries.length - 1];
        const box = entry && (entry.contentRect || entry.borderBoxSize);
        onChange(box ? box.height : row.getBoundingClientRect().height);
      });
      ro.observe(row);
      row.__remeasureRO = ro;
    } else {
      const details = row.querySelector('details');
      if (details) details.addEventListener('toggle', () => onChange(row.getBoundingClientRect().height));
    }
  }

  /** @param {HTMLElement} row */
  _unbindRemeasure(row) {
    if (row && row.__remeasureRO) {
      try { row.__remeasureRO.disconnect(); } catch (_e) { /* noop */ }
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
    const replace = !!(opts && opts.replace) || (!prepend && !append);
    const previousSize = this.virtualizer
      ? this.virtualizer.getTotalSize()
      : this.store.estimateTotalSize();
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

    if (opts && 'hasOlderMessages' in opts) {
      this.setOlderMessagesAvailable(!!opts.hasOlderMessages);
    }
  }

  /**
   * Apply incremental event: update store and refresh visible rows.
   * @param {object} event
   */
  applyIncrementalEvent(event) {
    if (event.type === 'RESET_TIMELINE') {
      this.store.clear();
      this.reset();
      this.dom.resetTimeline();
      return;
    }

    const patchTypes = new Set([
      'TOOL_CALL_ARGS', 'TOOL_CALL_END', 'TOOL_CALL_OUTPUT', 'TOOL_CALL_RESULT',
      'TOOL_APPROVAL_REQUEST', 'TOOL_APPROVAL_RESOLVED', 'PLAN_CLARIFY_RESOLVED'
    ]);

    if (event.type === 'TEXT_MESSAGE_START') {
      this.dom.state.currentAssistantEl = null;
      this.dom.state.assistantStarted[event.messageId] = false;
      return;
    }
    if (event.type === 'TEXT_MESSAGE_END') {
      this.dom.state.currentAssistantEl = null;
      return;
    }
    if (event.type === 'RUN_STARTED') {
      this.dom.state.currentAssistantEl = null;
      this.dom.state.currentReasoningEl = null;
      return;
    }
    if (event.type === 'REASONING_MESSAGE_START'
        || event.type === 'REASONING_MESSAGE_CONTENT'
        || event.type === 'REASONING_MESSAGE_END') {
      return;
    }
    if (event.type === 'TEXT_MESSAGE_CONTENT') {
      if (!this.dom.state.assistantStarted[event.messageId]) {
        this.store.applyEvent({ type: 'STATIC_ASSISTANT_HTML', messageId: event.messageId, html: '', streaming: true });
        this.refresh();
      }
      return;
    }

    const result = this.store.applyEvent(event);

    if (event.type === 'TURN_ACTIVITY' && event.upsert === true) {
      const turnId = this.store.currentTurnId || 'orphan';
      const itemId = this.store.activityId(turnId);
      const mounted = this.mountedById.get(itemId);
      if (mounted) {
        this.dom.state.batchTarget = null;
        this.dom.handleEvent(event);
        this._syncMountedVersion(mounted, itemId);
        this._remeasureRow(mounted);
        if (result.scrollBottom) this.scrollToBottom(false);
        return;
      }
    }

    if (event.type === 'FILES_CHANGED') {
      const turnId = this.store.currentTurnId || 'orphan';
      const itemId = this.store.filesId(turnId);
      const mounted = this.mountedById.get(itemId);
      if (mounted) {
        this.dom.state.batchTarget = null;
        this.dom.handleEvent(event);
        this._syncMountedVersion(mounted, itemId);
        this._remeasureRow(mounted);
        if (result.scrollBottom) this.scrollToBottom(false);
        return;
      }
    }

    if (event.type === 'STATIC_ASSISTANT_HTML') {
      const itemId = this.store.assistantId(event.messageId || '');
      const mounted = this.mountedById.get(itemId);
      if (mounted) {
        this.dom.state.batchTarget = null;
        this.dom.handleEvent(event);
        this._syncMountedVersion(mounted, itemId);
        this._remeasureRow(mounted);
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
      const toolCallId = event.toolCallId || '';
      const itemId = this.store.toolId(toolCallId);
      const mounted = this.mountedById.get(itemId);
      if (mounted) {
        this.dom.state.batchTarget = null;
        this.dom.handleEvent(event);
        this._syncMountedVersion(mounted, itemId);
        this._remeasureRow(mounted);
        return;
      }
    }

    this.refresh();
    if (result.scrollBottom) this.scrollToBottom(false);
  }

  /** @param {string} itemId */
  getMountedRow(itemId) {
    return this.mountedById.get(itemId) || null;
  }
}
