/**
 * Flat timeline item store for virtual list rendering.
 * @typedef {{ type: string, [key: string]: unknown }} AgUiEvent
 * @typedef {{
 *   id: string,
 *   type: string,
 *   event: AgUiEvent,
 *   turnId: string | null,
 *   live: boolean,
 *   estimatedHeight: number,
 *   version: number,
 *   toolCallId?: string,
 *   messageId?: string
 * }} TimelineItem
 */

const HEIGHT = {
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
    case 'USER':
      return HEIGHT.USER + ((event.images && event.images.length) ? 80 : 0);
    case 'STATIC_ASSISTANT_HTML':
    case 'ASSISTANT':
      return HEIGHT.ASSISTANT;
    case 'TURN_ACTIVITY':
      return event && event.upsert ? 52 : HEIGHT.TURN_ACTIVITY;
    case 'TOOL':
      return HEIGHT.TOOL;
    case 'FILES_CHANGED':
      return HEIGHT.FILES_CHANGED + ((event.files && event.files.length) || 0) * 28;
    case 'COMPACTION_CHECKPOINT':
      return HEIGHT.COMPACTION;
    case 'PLAN_CLARIFY':
    case 'PLAN_READY':
      return HEIGHT.PLAN;
    case 'OVERFLOW_RETRY_SKIPPED':
      return HEIGHT.OVERFLOW;
    default:
      return 80;
  }
}

function itemTypeFromEvent(event) {
  return event.type === 'STATIC_ASSISTANT_HTML' ? 'ASSISTANT' : event.type;
}

function cloneEvent(event) {
  return JSON.parse(JSON.stringify(event));
}

export class TimelineItemStore {
  constructor() {
    /** @type {TimelineItem[]} */
    this.items = [];
    /** @type {string | null} */
    this.currentTurnId = null;
    /** @type {Map<string, number>} */
    this.indexById = new Map();
    /** @type {Map<string, number>} */
    this.toolIndexByCallId = new Map();
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
      if (item.type === 'TOOL' && item.event && item.event.toolCallId) {
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
    const item = /** @type {TimelineItem} */ ({
      id,
      version: 1,
      live: false,
      turnId: this.currentTurnId,
      estimatedHeight: 80,
      ...patch
    });
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
        if (item.type === 'TURN_ACTIVITY' && item.event) {
          item.event = cloneEvent(item.event);
          item.event.upsert = false;
        }
        if (item.type === 'FILES_CHANGED' && item.event) {
          item.event = cloneEvent(item.event);
          item.event.upsert = false;
        }
      }
    }
  }

  /** @param {string} turnId */
  activityId(turnId) {
    return 'activity:' + turnId;
  }

  /** @param {string} turnId */
  filesId(turnId) {
    return 'files:' + turnId;
  }

  /** @param {string} messageId */
  assistantId(messageId) {
    return 'assistant:' + messageId;
  }

  /** @param {string} toolCallId */
  toolId(toolCallId) {
    return 'tool:' + toolCallId;
  }

  /** @param {AgUiEvent} event @returns {{ removedIds?: string[], remeasure?: boolean }} */
  applyEvent(event) {
    if (!event || !event.type) return {};

    switch (event.type) {
      case 'RESET_TIMELINE':
        this.clear();
        return { reset: true };

      case 'USER_MESSAGE': {
        this.sealLiveItems();
        const messageId = event.messageId || 'user-' + this.items.length;
        this.currentTurnId = messageId;
        this.upsertItem('user:' + messageId, {
          type: 'USER',
          event: cloneEvent(event),
          turnId: messageId,
          live: false,
          messageId
        });
        return { scrollBottom: true };
      }

      case 'TURN_ACTIVITY': {
        const turnId = this.currentTurnId || 'orphan';
        const id = this.activityId(turnId);
        this.upsertItem(id, {
          type: 'TURN_ACTIVITY',
          event: cloneEvent(event),
          turnId,
          live: event.upsert === true
        });
        return { scrollBottom: true };
      }

      case 'FILES_CHANGED': {
        const turnId = this.currentTurnId || 'orphan';
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
          type: 'FILES_CHANGED',
          event: cloneEvent(event),
          turnId,
          live: event.upsert === true
        });
        return { scrollBottom: true };
      }

      case 'STATIC_ASSISTANT_HTML': {
        const messageId = event.messageId || '';
        const id = this.assistantId(messageId);
        this.upsertItem(id, {
          type: 'ASSISTANT',
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

      case 'REMOVE_ASSISTANT_BUBBLES': {
        const ids = event.messageIds || [];
        ids.forEach((mid) => this.removeItem(this.assistantId(mid)));
        return {};
      }

      case 'TOOL_CALL_START': {
        if (event.toolCallName === 'ask_plan_clarification' || event.toolCallName === 'publish_plan') {
          return {};
        }
        const toolCallId = event.toolCallId || '';
        this.upsertItem(this.toolId(toolCallId), {
          type: 'TOOL',
          event: cloneEvent(event),
          turnId: this.currentTurnId,
          live: true,
          toolCallId,
          toolState: { phase: 'start' }
        });
        return { scrollBottom: true };
      }

      case 'TOOL_CALL_ARGS':
      case 'TOOL_CALL_END':
      case 'TOOL_CALL_OUTPUT':
      case 'TOOL_CALL_RESULT':
      case 'TOOL_APPROVAL_REQUEST':
      case 'TOOL_APPROVAL_RESOLVED': {
        const toolCallId = event.toolCallId || '';
        const id = this.toolId(toolCallId);
        const index = this.indexById.get(id);
        if (index == null) return { scrollBottom: true };
        const item = this.items[index];
        if (!item.toolState) item.toolState = {};
        item.toolState[event.type] = cloneEvent(event);
        item.version += 1;
        if (event.type === 'TOOL_CALL_RESULT') item.live = false;
        return { scrollBottom: true, patchToolId: toolCallId };
      }

      case 'COMPACTION_CHECKPOINT': {
        const cid = event.id || 'compaction';
        this.upsertItem('compaction:' + cid, {
          type: 'COMPACTION',
          event: cloneEvent(event),
          turnId: null,
          live: false
        });
        return { scrollBottom: true };
      }

      case 'OVERFLOW_RETRY_SKIPPED': {
        this.upsertItem('overflow:' + this.items.length, {
          type: 'OVERFLOW',
          event: cloneEvent(event),
          turnId: this.currentTurnId,
          live: false
        });
        return { scrollBottom: true };
      }

      case 'PLAN_CLARIFY_REQUEST': {
        const requestId = event.requestId || 'plan';
        this.upsertItem('plan-clarify:' + requestId, {
          type: 'PLAN_CLARIFY',
          event: cloneEvent(event),
          turnId: this.currentTurnId,
          live: !event.resolved
        });
        return { scrollBottom: true };
      }

      case 'PLAN_CLARIFY_RESOLVED': {
        const requestId = event.requestId || '';
        const id = 'plan-clarify:' + requestId;
        const index = this.indexById.get(id);
        if (index != null) {
          const item = this.items[index];
          item.event = { ...item.event, ...cloneEvent(event), resolved: true };
          item.live = false;
          item.version += 1;
        }
        return {};
      }

      case 'PLAN_READY': {
        const runId = event.runId || 'plan';
        this.upsertItem('plan-ready:' + runId, {
          type: 'PLAN_READY',
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

    /** @type {TimelineItemStore} */
    const batchStore = prepend ? new TimelineItemStore() : this;

    for (const raw of rawEvents) {
      try {
        const event = typeof raw === 'string' ? JSON.parse(raw) : raw;
        if (!event || !event.type) continue;
        if (event.type === 'RESET_TIMELINE') {
          if (!prepend && !append) this.clear();
          continue;
        }
        batchStore.applyEvent(event);
      } catch (_e) { /* ignore parse errors */ }
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
}

export { estimateHeight, HEIGHT };
