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
    /** @type {Map<string, HTMLElement>} */
    this.mountedById = new Map();
    /** @type {number} */
    this.renderFrame = 0;
    /** @type {boolean} */
    this.hasOlderMessages = false;
    /** @type {boolean} */
    this.loadOlderPending = false;
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

  /** @param {number} count */
  _setCount(count) {
    if (!this.virtualizer || !this._buildVirtualizerOptions) return;
    this.virtualizer.setOptions(this._buildVirtualizerOptions(count));
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
    scroller.scrollTop = scroller.scrollHeight;
  }

  _onScroll() {
    this.dom.state.autoScrollEnabled = this.dom.isNearBottom();
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

  _paint() {
    if (!this.virtualizer) return;
    const windowEl = document.getElementById('virtual-window');
    if (!windowEl) return;

    this._setCount(this.store.count);
    let virtualItems = this.virtualizer.getVirtualItems();
    // Fallback when the scroll element has not been measured yet (height 0):
    // still render a trailing window so history / new turns are not blank.
    if (virtualItems.length === 0 && this.store.count > 0) {
      const start = Math.max(0, this.store.count - 40);
      virtualItems = [];
      for (let index = start; index < this.store.count; index++) {
        virtualItems.push({
          index,
          start: this._estimateOffset(index),
          size: (this.store.items[index]?.estimatedHeight || 80) + 20,
          end: 0,
          key: index,
          lane: 0
        });
      }
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

      if (row && row.parentNode) row.parentNode.removeChild(row);
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
      this._bindRemeasure(row);
      this.virtualizer.measureElement(row);
      const measured = row.getBoundingClientRect().height;
      if (measured > 0) {
        item.estimatedHeight = Math.max(1, Math.round(measured) - 20);
      }
      nextMounted.set(item.id, row);
      usedIds.add(item.id);
      this.dom.enhanceCodeBlocks(row);
    });

    this.mountedById.forEach((row, id) => {
      if (!usedIds.has(id) && row.parentNode) row.parentNode.removeChild(row);
    });
    this.mountedById = nextMounted;

    windowEl.style.height = this.virtualizer.getTotalSize() + 'px';
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
    if (row.dataset.remeasureBound) return;
    row.dataset.remeasureBound = '1';
    const details = row.querySelector('details');
    if (!details) return;
    details.addEventListener('toggle', () => {
      if (!this.virtualizer) return;
      this.virtualizer.measureElement(row);
      const itemId = row.dataset.timelineItemId;
      if (itemId) {
        const item = this.store.items.find((candidate) => candidate.id === itemId);
        const measured = row.getBoundingClientRect().height;
        if (item && measured > 0) {
          item.estimatedHeight = Math.max(1, Math.round(measured) - 20);
        }
      }
      this._schedulePaint();
    });
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
      // Sync paint so estimated→measured delta can be compensated in the same turn.
      this._paint();
      const delta = this.virtualizer.getTotalSize() - previousSize;
      scroller.scrollTop = previousTop + Math.max(0, delta);
      // One more paint after scroll so the window matches the anchored offset.
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
      const mounted = this.mountedById.get(this.store.activityId(turnId));
      if (mounted) {
        this.dom.state.batchTarget = null;
        this.dom.handleEvent(event);
        if (this.virtualizer) this.virtualizer.measureElement(mounted);
        if (result.scrollBottom) this.scrollToBottom(false);
        return;
      }
    }

    if (event.type === 'FILES_CHANGED') {
      const turnId = this.store.currentTurnId || 'orphan';
      const mounted = this.mountedById.get(this.store.filesId(turnId));
      if (mounted) {
        this.dom.state.batchTarget = null;
        this.dom.handleEvent(event);
        if (this.virtualizer) this.virtualizer.measureElement(mounted);
        if (result.scrollBottom) this.scrollToBottom(false);
        return;
      }
    }

    if (event.type === 'STATIC_ASSISTANT_HTML') {
      const mounted = this.mountedById.get(this.store.assistantId(event.messageId || ''));
      if (mounted) {
        this.dom.state.batchTarget = null;
        this.dom.handleEvent(event);
        if (this.virtualizer) this.virtualizer.measureElement(mounted);
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
      const mounted = this.mountedById.get(this.store.toolId(toolCallId));
      if (mounted) {
        this.dom.state.batchTarget = null;
        this.dom.handleEvent(event);
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
