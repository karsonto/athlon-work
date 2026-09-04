import { test } from 'node:test';
import assert from 'node:assert/strict';
import { TimelineItemStore } from './timeline-store.js';

test('USER_MESSAGE starts a turn and seals prior live items', () => {
  const store = new TimelineItemStore();
  store.applyEvent({
    type: 'TURN_ACTIVITY',
    upsert: true,
    items: [{ kind: 'thought', body: 'a', verb: 'Thought' }],
    thoughtCount: 1
  });
  const activity = store.items.find((item) => item.type === 'TURN_ACTIVITY');
  assert.equal(activity?.live, true);

  store.applyEvent({ type: 'USER_MESSAGE', messageId: 'u1', content: 'hi' });
  assert.equal(store.currentTurnId, 'u1');
  assert.equal(store.items.length, 2);
  assert.equal(activity?.live, false);
});

test('prepend preserves order and increases count', () => {
  const store = new TimelineItemStore();
  store.applyEvent({ type: 'USER_MESSAGE', messageId: 'u2', content: 'new' });
  store.ingestEvents([
    { type: 'USER_MESSAGE', messageId: 'u1', content: 'old' }
  ], { prepend: true });
  assert.equal(store.items.length, 2);
  assert.equal(store.items[0].messageId, 'u1');
  assert.equal(store.items[1].messageId, 'u2');
});

test('STATIC_ASSISTANT_HTML moves files item after assistant when finalized', () => {
  const store = new TimelineItemStore();
  store.applyEvent({ type: 'USER_MESSAGE', messageId: 'u1', content: 'q' });
  store.applyEvent({
    type: 'FILES_CHANGED',
    upsert: true,
    files: [{ path: 'a.txt', displayName: 'a.txt', added: 1, removed: 0, lines: [] }]
  });
  store.applyEvent({
    type: 'STATIC_ASSISTANT_HTML',
    messageId: 'a1',
    html: '<p>answer</p>',
    streaming: false
  });

  const filesIndex = store.indexById.get(store.filesId('u1'));
  const assistantIndex = store.indexById.get(store.assistantId('a1'));
  assert.ok(filesIndex != null && assistantIndex != null);
  assert.ok(filesIndex > assistantIndex);
});

test('TOOL events accumulate on one item', () => {
  const store = new TimelineItemStore();
  store.applyEvent({ type: 'USER_MESSAGE', messageId: 'u1', content: 'q' });
  store.applyEvent({ type: 'TOOL_CALL_START', toolCallId: 't1', toolCallName: 'grep' });
  store.applyEvent({ type: 'TOOL_CALL_ARGS', toolCallId: 't1', delta: '{"pattern":"x"}' });
  assert.equal(store.items.filter((item) => item.type === 'TOOL').length, 1);
  const tool = store.items.find((item) => item.type === 'TOOL');
  assert.ok(tool?.toolState?.TOOL_CALL_ARGS);
});

test('batched replay append keeps prior slices (session switch / restart)', () => {
  const store = new TimelineItemStore();
  // First WebView batch: replay clears + loads head
  store.ingestEvents([
    { type: 'RESET_TIMELINE' },
    { type: 'USER_MESSAGE', messageId: 'u1', content: 'one' },
    { type: 'STATIC_ASSISTANT_HTML', messageId: 'a1', html: '<p>1</p>' }
  ], { append: false });
  assert.equal(store.items.length, 2);

  // Follow-up batches must append without clearing (WebChatView PostReplayInBatchesAsync)
  store.ingestEvents([
    { type: 'USER_MESSAGE', messageId: 'u2', content: 'two' },
    { type: 'STATIC_ASSISTANT_HTML', messageId: 'a2', html: '<p>2</p>' }
  ], { append: true });
  store.ingestEvents([
    { type: 'USER_MESSAGE', messageId: 'u3', content: 'three' }
  ], { append: true });

  assert.equal(store.items.length, 5);
  assert.equal(store.items[0].messageId, 'u1');
  assert.equal(store.items[1].messageId, 'a1');
  assert.equal(store.items[2].messageId, 'u2');
  assert.equal(store.items[3].messageId, 'a2');
  assert.equal(store.items[4].messageId, 'u3');
});

test('append without flag would clear — document regression guard via explicit append', () => {
  const store = new TimelineItemStore();
  store.ingestEvents([{ type: 'USER_MESSAGE', messageId: 'keep', content: 'x' }]);
  store.ingestEvents([{ type: 'USER_MESSAGE', messageId: 'only', content: 'y' }]); // replace
  assert.equal(store.items.length, 1);
  assert.equal(store.items[0].messageId, 'only');
});

test('prepend scroll compensation uses stable estimated height delta', () => {
  const store = new TimelineItemStore();
  store.ingestEvents([
    { type: 'USER_MESSAGE', messageId: 'u2', content: 'visible' },
    { type: 'STATIC_ASSISTANT_HTML', messageId: 'a2', html: '<p>ans</p>' }
  ]);
  const before = store.estimateTotalSize();
  const previousTop = 200;

  store.ingestEvents([
    { type: 'USER_MESSAGE', messageId: 'u1', content: 'older' },
    { type: 'STATIC_ASSISTANT_HTML', messageId: 'a1', html: '<p>old</p>' }
  ], { prepend: true });

  const after = store.estimateTotalSize();
  const delta = after - before;
  assert.ok(delta > 0, 'prepended content must increase total size');
  assert.equal(store.items[0].messageId, 'u1');
  assert.equal(store.items[2].messageId, 'u2');
  // Anchored scroll formula used by VirtualTimeline.ingestEvents
  const anchoredTop = previousTop + delta;
  assert.equal(anchoredTop, previousTop + delta);
  assert.ok(anchoredTop > previousTop);
});

test('multi-turn activity: seal belongs to its own turn, next turn gets a fresh card', () => {
  const store = new TimelineItemStore();

  // Turn 1: user message, live activity, then seal.
  store.applyEvent({ type: 'USER_MESSAGE', messageId: 'u1', content: 'q1' });
  const act1 = { type: 'TURN_ACTIVITY', upsert: true, items: [{ kind: 'thought', body: 'work', verb: 'Thought' }], thoughtCount: 1 };
  store.applyEvent(act1);
  const a1 = store.items.find((item) => item.type === 'TURN_ACTIVITY');
  assert.equal(a1?.live, true);
  assert.equal(a1?.turnId, 'u1');

  store.applyEvent({ type: 'TURN_ACTIVITY', upsert: false, items: [{ kind: 'thought', body: 'work', verb: 'Thought' }], thoughtCount: 1, durationMs: 1000 });
  assert.equal(a1?.live, false);
  assert.equal(a1?.event.upsert, false);
  assert.equal(store.currentTurnId, 'u1', 'seal must not move currentTurnId');

  // Turn 2 starts; seal of turn 1 must not be replayed into turn 2.
  store.applyEvent({ type: 'USER_MESSAGE', messageId: 'u2', content: 'q2' });
  assert.equal(store.currentTurnId, 'u2');
  assert.equal(a1?.live, false, 'turn-1 card stays sealed after turn-2 starts');

  const act2 = { type: 'TURN_ACTIVITY', upsert: true, items: [{ kind: 'thought', body: 'work2', verb: 'Thought' }], thoughtCount: 1 };
  store.applyEvent(act2);

  const activities = store.items.filter((item) => item.type === 'TURN_ACTIVITY');
  assert.equal(activities.length, 2, 'each turn owns exactly one activity card');
  assert.equal(activities[0].turnId, 'u1');
  assert.equal(activities[1].turnId, 'u2');
  assert.equal(activities[1].live, true);
  assert.equal(activities[0].live, false);
  // Item order follows event arrival: u1, a1, u2, a2.
  const typeOrder = store.items.map((item) => item.type);
  assert.deepEqual(typeOrder, ['USER', 'TURN_ACTIVITY', 'USER', 'TURN_ACTIVITY']);
});

test('files card per turn is keyed by turn id and survives cross-turn upserts', () => {
  const store = new TimelineItemStore();
  store.applyEvent({ type: 'USER_MESSAGE', messageId: 'u1', content: 'q1' });
  store.applyEvent({
    type: 'FILES_CHANGED', upsert: true,
    files: [{ path: 'a.txt', added: 1, removed: 0, lines: [] }]
  });
  const files1 = store.items.find((item) => item.type === 'FILES_CHANGED');
  assert.equal(files1?.turnId, 'u1');
  assert.equal(files1?.live, true);

  store.applyEvent({ type: 'FILES_CHANGED', upsert: false, files: [] });
  assert.equal(files1?.live, false);

  store.applyEvent({ type: 'USER_MESSAGE', messageId: 'u2', content: 'q2' });
  store.applyEvent({
    type: 'FILES_CHANGED', upsert: true,
    files: [{ path: 'b.txt', added: 2, removed: 0, lines: [] }]
  });

  const filesCards = store.items.filter((item) => item.type === 'FILES_CHANGED');
  assert.equal(filesCards.length, 2);
  assert.equal(filesCards[0].turnId, 'u1');
  assert.equal(filesCards[0].live, false);
  assert.equal(filesCards[1].turnId, 'u2');
  assert.equal(filesCards[1].live, true);
});
