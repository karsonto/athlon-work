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
 * The fold is keyed by the turn and the fold's index within it, so a live fold that keeps growing
 * and the fold rebuilt from the transcript are the same entry: the second render overwrites the
 * first's contents and leaves the row where it is. No "insert after the last user row" step is
 * needed — the fold and the content bubbles share one seq stream, so a fold sits exactly between
 * the replies it ran between rather than at a fixed slot ahead of the whole turn.
 */
function appendTurnActivityCard(event) {
  state.currentAssistantEl = null;
  state.currentReasoningEl = null;
  var items = event.items || [];
  if (!items.length && !(event.exploredFileCount || event.searchCount || event.commandCount || event.thoughtCount)) {
    return;
  }

  var key = turnActivityEntryKey(event);
  // A replayed fold carries its own seq. A live fold claims the next content slot the first time
  // it is created and keeps that slot across upserts, so the fold and the bubble that follows it
  // interleave exactly as they streamed.
  var seq = resolveEventSeq(event, undefined);
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
    registerEntry(key, row, seq === undefined ? nextLiveContentSeq() : seq);
  } else {
    details.innerHTML = '';
    if (seq !== undefined) {
      insertBySeq(row, seq);
    }
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
    var entry = document.createElement('div');
    entry.className = 'turn-activity-item'
      + (hasDiff ? ' has-diff' : '')
      + (hasThought ? ' has-thought' : '');

    if (hasThought) {
      var thoughtLabel = document.createElement('div');
      thoughtLabel.className = 'turn-activity-thought-label';
      thoughtLabel.textContent = item.verb || (t('thought') || 'Thought');
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

