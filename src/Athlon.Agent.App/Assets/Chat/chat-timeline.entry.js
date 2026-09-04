import { TimelineItemStore } from './timeline-store.js';
import { createDomRenderer } from './timeline-dom.js';
import { VirtualTimeline } from './timeline-virtual.js';

const store = new TimelineItemStore();
const dom = createDomRenderer();
let virtual = null;

function ensureVirtual() {
  if (!virtual) {
    virtual = new VirtualTimeline({
      store,
      dom,
      onLoadOlder: function () {
        dom.state.autoScrollEnabled = false;
      }
    });
    virtual.init();
  }
  return virtual;
}

function handleEvent(event) {
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
  // Batched replay follow-up slices must NOT clear the store.
  ensureVirtual().ingestEvents(events, { append: true });
}

function prependEvents(events, hasOlderMessages) {
  ensureVirtual().ingestEvents(events, { prepend: true, hasOlderMessages: hasOlderMessages });
}

function setOlderMessagesAvailable(available) {
  ensureVirtual().setOlderMessagesAvailable(available);
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
  } else if (command.command === 'toolDetail') {
    dom.applyToolDetailPayload(command);
  } else if (command.command === 'reset') {
    dom.beginBatch();
    store.clear();
    ensureVirtual().reset();
    dom.resetTimeline();
    dom.endBatch(false);
  }
  if (command.replayComplete && Number.isInteger(command.renderGeneration)) {
    ensureVirtual().scrollToBottom(true);
    dom.post({ type: 'replayComplete', renderGeneration: command.renderGeneration });
  }
}

function boot() {
  dom.applyChatI18n();
  ensureVirtual();

  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', function (event) {
      try { handleWebMessage(event.data); }
      catch (e) { console.warn('chat web message failed', e); }
    });
  }

  document.addEventListener('selectionchange', function () {
    if (dom.hasActiveSelection()) dom.state.autoScrollEnabled = false;
    else if (dom.isNearBottom()) dom.state.autoScrollEnabled = true;
  });
}

window.handleEvent = handleEvent;
window.replayEvents = replayEvents;
window.appendEvents = appendEvents;
window.prependEvents = prependEvents;
window.handleWebMessage = handleWebMessage;
window.applyThemeUpdate = dom.applyThemeUpdate.bind(dom);
window.applyChatI18n = dom.applyChatI18n.bind(dom);

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', boot);
} else {
  boot();
}

export {
  handleEvent,
  replayEvents,
  appendEvents,
  prependEvents,
  handleWebMessage,
  store,
  dom
};
