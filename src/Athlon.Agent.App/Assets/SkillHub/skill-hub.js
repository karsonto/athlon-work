(function () {
  'use strict';

  var PAGE_SIZE = 8;

  var state = {
    items: [],
    installed: {},
    installing: {},
    query: '',
    category: '',
    position: '',
    sort: '',
    page: 1,
    detailId: null
  };

  function post(payload) {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(payload);
    }
  }

  function escapeHtml(text) {
    return String(text == null ? '' : text)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  function displayName(item) {
    return ((item && (item.name || item.englishName)) || '').trim();
  }

  function initial(item) {
    var name = displayName(item);
    return name ? name.charAt(0).toUpperCase() : '?';
  }

  function toneClass(item) {
    var name = displayName(item);
    var n = 0;
    for (var i = 0; i < name.length; i++) n = (n + name.charCodeAt(i)) % 5;
    return 'icon-tone-' + n;
  }

  function formatSize(bytes) {
    var n = Number(bytes);
    if (!n || n < 0) return '';
    if (n < 1024) return n + ' B';
    if (n < 1024 * 1024) return (n / 1024).toFixed(n < 10 * 1024 ? 1 : 0) + ' KB';
    return (n / (1024 * 1024)).toFixed(1) + ' MB';
  }

  function isInstalled(item) {
    if (item && item.installed === true) return true;
    var keys = [item.englishName, item.name, item.id];
    for (var i = 0; i < keys.length; i++) {
      var key = (keys[i] || '').trim().toLowerCase();
      if (key && state.installed[key]) return true;
    }
    return false;
  }

  function findItem(id) {
    var wanted = String(id == null ? '' : id);
    for (var i = 0; i < state.items.length; i++) {
      if (String(state.items[i].id) === wanted) return state.items[i];
    }
    return null;
  }

  function uniqueValues(items, field) {
    var seen = [];
    items.forEach(function (item) {
      var value = (item[field] || '').trim();
      if (value && seen.indexOf(value) < 0) seen.push(value);
    });
    seen.sort(function (a, b) { return a.localeCompare(b, 'zh'); });
    return seen;
  }

  function filteredItems() {
    var q = state.query.trim().toLowerCase();
    var items = state.items.filter(function (item) {
      if (state.category && (item.category || '') !== state.category) return false;
      if (state.position && (item.position || '') !== state.position) return false;
      if (!q) return true;
      var hay = [item.name, item.englishName, item.description, item.category, item.position]
        .join(' ')
        .toLowerCase();
      return hay.indexOf(q) >= 0;
    });
    if (state.sort === 'name') {
      items.sort(function (a, b) { return displayName(a).localeCompare(displayName(b), 'zh'); });
    } else if (state.sort === 'category') {
      items.sort(function (a, b) {
        var byCat = (a.category || '').localeCompare(b.category || '', 'zh');
        return byCat || displayName(a).localeCompare(displayName(b), 'zh');
      });
    }
    return items;
  }

  function tagsHtml(item) {
    var tags = [];
    if ((item.category || '').trim()) tags.push(item.category.trim());
    if ((item.position || '').trim()) tags.push(item.position.trim());
    if (!tags.length) return '';
    return '<div class="tag-row">' + tags.map(function (tag) {
      return '<span class="tag">' + escapeHtml(tag) + '</span>';
    }).join('') + '</div>';
  }

  function actionOf(item) {
    var action = String(item && item.availability || '').toLowerCase();
    if (action === 'update' || action === 'installed' || action === 'use') return action;
    return isInstalled(item) ? 'installed' : 'use';
  }

  function useButtonHtml(item, extraClass) {
    var action = actionOf(item);
    var pending = state.installing[item.id];
    var installing = !!pending;
    var cls = 'use-btn' + (action === 'installed' ? ' installed' : '') + (extraClass ? ' ' + extraClass : '');
    var label = installing
      ? (pending === 'update' ? '更新中…' : '下载中…')
      : (action === 'update' ? '更新' : action === 'installed' ? '已安装' : '使用');
    var disabled = installing || action === 'installed' ? ' disabled' : '';
    var attrs = disabled
      ? ''
      : ' data-action="add" data-id="' + escapeHtml(item.id) + '" data-mode="' + action + '"';
    return '<button type="button" class="' + cls + '"' + disabled + attrs + '>' + label + '</button>';
  }

  function renderChips() {
    var host = document.getElementById('categoryChips');
    if (!host) return;
    var cats = uniqueValues(state.items, 'category');
    var chips = [{ value: '', label: '全部' }].concat(cats.map(function (c) {
      return { value: c, label: c };
    }));
    host.innerHTML = chips.map(function (chip) {
      var active = chip.value === state.category ? ' active' : '';
      return '<button type="button" class="chip' + active + '" data-action="category" data-value="' +
        escapeHtml(chip.value) + '">' + escapeHtml(chip.label) + '</button>';
    }).join('');
  }

  function renderPositionFilter() {
    var select = document.getElementById('positionFilter');
    if (!select) return;
    var current = state.position;
    var positions = uniqueValues(state.items, 'position');
    if (current && positions.indexOf(current) < 0) current = '';
    state.position = current;
    select.innerHTML = '<option value="">全部岗位</option>' + positions.map(function (p) {
      return '<option value="' + escapeHtml(p) + '"' + (p === current ? ' selected' : '') + '>' +
        escapeHtml(p) + '</option>';
    }).join('');
  }

  function renderGrid(items) {
    var grid = document.getElementById('grid');
    var pager = document.getElementById('pager');
    var count = document.getElementById('resultCount');
    if (!grid || !pager) return;
    if (count) count.textContent = items.length ? ('共 ' + items.length + ' 个') : '';
    if (!items.length) {
      grid.innerHTML = '';
      pager.hidden = true;
      pager.innerHTML = '';
      return;
    }
    var pages = Math.max(1, Math.ceil(items.length / PAGE_SIZE));
    if (state.page > pages) state.page = pages;
    if (state.page < 1) state.page = 1;
    var start = (state.page - 1) * PAGE_SIZE;
    var pageItems = items.slice(start, start + PAGE_SIZE);
    grid.innerHTML = pageItems.map(function (item) {
      return '<article class="skill-card">' +
        '<div class="card-top">' +
        '<div class="skill-icon ' + toneClass(item) + '">' + escapeHtml(initial(item)) + '</div>' +
        '<h3 class="card-name">' + escapeHtml(displayName(item)) + '</h3>' +
        '</div>' +
        '<p class="card-desc">' + escapeHtml(item.description || '暂无说明') + '</p>' +
        tagsHtml(item) +
        '<div class="card-actions">' +
        useButtonHtml(item) +
        '<button type="button" class="detail-btn" data-action="detail" data-id="' + escapeHtml(item.id) + '">详情</button>' +
        '</div>' +
        '</article>';
    }).join('');
    renderPager(pages);
  }

  function renderPager(pages) {
    var pager = document.getElementById('pager');
    if (!pager) return;
    if (pages <= 1) {
      pager.hidden = true;
      pager.innerHTML = '';
      return;
    }
    pager.hidden = false;
    var html = '<button type="button" class="page-btn" data-action="page" data-page="' + (state.page - 1) + '"' +
      (state.page <= 1 ? ' disabled' : '') + '>‹</button>';
    var tokens = pageTokens(state.page, pages);
    tokens.forEach(function (token) {
      if (token === '…') {
        html += '<span class="page-gap">…</span>';
        return;
      }
      var active = token === state.page ? ' active' : '';
      html += '<button type="button" class="page-btn' + active + '" data-action="page" data-page="' + token + '">' +
        token + '</button>';
    });
    html += '<button type="button" class="page-btn" data-action="page" data-page="' + (state.page + 1) + '"' +
      (state.page >= pages ? ' disabled' : '') + '>›</button>';
    pager.innerHTML = html;
  }

  function pageTokens(current, pages) {
    if (pages <= 7) {
      var all = [];
      for (var i = 1; i <= pages; i++) all.push(i);
      return all;
    }
    var tokens = [1];
    var start = Math.max(2, current - 1);
    var end = Math.min(pages - 1, current + 1);
    if (start > 2) tokens.push('…');
    for (var n = start; n <= end; n++) tokens.push(n);
    if (end < pages - 1) tokens.push('…');
    tokens.push(pages);
    return tokens;
  }

  function setStatus(message, show) {
    var el = document.getElementById('status');
    if (!el) return;
    if (!show || !message) {
      el.hidden = true;
      el.textContent = '';
      return;
    }
    el.hidden = false;
    el.textContent = message;
  }

  function metaRow(label, value) {
    if (!value) return '';
    return '<tr><th>' + escapeHtml(label) + '</th><td>' + value + '</td></tr>';
  }

  function renderDrawer() {
    var drawer = document.getElementById('drawer');
    var backdrop = document.getElementById('drawerBackdrop');
    var body = document.getElementById('drawerBody');
    if (!drawer || !backdrop || !body) return;
    var item = state.detailId ? findItem(state.detailId) : null;
    if (!item) {
      drawer.hidden = true;
      drawer.setAttribute('aria-hidden', 'true');
      backdrop.hidden = true;
      body.innerHTML = '';
      return;
    }
    drawer.hidden = false;
    drawer.setAttribute('aria-hidden', 'false');
    backdrop.hidden = false;
    var size = formatSize(item.packageSize);
    var version = item.commitSeq ? String(item.commitSeq) : '';
    var english = (item.englishName || '').trim();
    var name = displayName(item);
    if (english && english.toLowerCase() === name.toLowerCase()) english = '';
    var rows = metaRow('标识', english ? '<span class="mono">' + escapeHtml(english) + '</span>' : '') +
      metaRow('分类', escapeHtml((item.category || '').trim())) +
      metaRow('岗位', escapeHtml((item.position || '').trim())) +
      metaRow('体积', escapeHtml(size)) +
      metaRow('版本', version ? escapeHtml(version) : '');
    body.innerHTML =
      '<div class="detail-head">' +
      '<div class="skill-icon ' + toneClass(item) + '">' + escapeHtml(initial(item)) + '</div>' +
      '<div><h3 class="detail-name">' + escapeHtml(name) + '</h3>' + tagsHtml(item) + '</div>' +
      '</div>' +
      '<p class="detail-note">安装后写入本地 skills 目录，并在下次对话的技能目录中可用。</p>' +
      (rows ? '<table class="meta-table"><tbody>' + rows + '</tbody></table>' : '') +
      '<section class="detail-block"><h3>说明</h3><p class="detail-copy">' +
      escapeHtml(item.description || '暂无说明') + '</p></section>' +
      '<div class="drawer-actions">' + useButtonHtml(item) + '</div>';
  }

  function openDetail(id) {
    state.detailId = id;
    renderDrawer();
  }

  function closeDetail() {
    state.detailId = null;
    renderDrawer();
  }

  function render() {
    var items = filteredItems();
    if (!state.items.length) {
      document.getElementById('grid').innerHTML = '';
      document.getElementById('pager').hidden = true;
      document.getElementById('resultCount').textContent = '';
      renderChips();
      renderPositionFilter();
      renderDrawer();
      return;
    }
    setStatus('', false);
    renderChips();
    renderPositionFilter();
    renderGrid(items);
    renderDrawer();
  }

  function decodeBase64Utf8(b64) {
    var binary = atob(b64 || '');
    var bytes = new Uint8Array(binary.length);
    for (var i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return new TextDecoder('utf-8').decode(bytes);
  }

  function applyThemeTokensToRoot(tokensCss) {
    var root = document.documentElement;
    tokensCss.replace(/(--[\w-]+)\s*:\s*([^;]+);/g, function (_, name, value) {
      root.style.setProperty(name.trim(), value.trim());
    });
  }

  function syncThemeSurfaces() {
    var bg = getComputedStyle(document.documentElement).getPropertyValue('--hub-bg').trim()
      || getComputedStyle(document.documentElement).getPropertyValue('--bg').trim();
    var text = getComputedStyle(document.documentElement).getPropertyValue('--hub-text').trim()
      || getComputedStyle(document.documentElement).getPropertyValue('--text').trim();
    if (bg) {
      document.documentElement.style.backgroundColor = bg;
      document.body.style.backgroundColor = bg;
    }
    if (text) document.body.style.color = text;
  }

  function themeScheme() {
    var root = document.documentElement;
    var scheme = getComputedStyle(root).getPropertyValue('--hub-scheme').trim();
    if (scheme === 'dark' || scheme === 'light') return scheme;
    var bg = getComputedStyle(root).getPropertyValue('--hub-bg').trim();
    var hex = bg.replace('#', '');
    if (hex.length === 3) hex = hex[0] + hex[0] + hex[1] + hex[1] + hex[2] + hex[2];
    if (!/^[0-9a-fA-F]{6}/.test(hex)) return 'light';
    var r = parseInt(hex.slice(0, 2), 16);
    var g = parseInt(hex.slice(2, 4), 16);
    var b = parseInt(hex.slice(4, 6), 16);
    return (r * 299 + g * 587 + b * 114) / 1000 < 140 ? 'dark' : 'light';
  }

  function applyThemeUpdate(tokensB64) {
    var tokensCss = decodeBase64Utf8(tokensB64);
    var tokensEl = document.getElementById('skill-hub-theme-tokens');
    if (tokensEl) tokensEl.textContent = tokensCss;
    applyThemeTokensToRoot(tokensCss);
    var scheme = themeScheme();
    document.documentElement.dataset.theme = scheme;
    document.documentElement.style.colorScheme = scheme;
    syncThemeSurfaces();
  }

  function markInstalled(names, id) {
    (names || []).forEach(function (name) {
      var key = String(name || '').trim().toLowerCase();
      if (key) state.installed[key] = true;
    });
    if (id) {
      state.items.forEach(function (item) {
        if (item && String(item.id) === String(id)) {
          item.installed = true;
          item.availability = 'installed';
        }
      });
    }
  }

  function onHostMessage(data) {
    if (typeof data === 'string') {
      try { data = JSON.parse(data); } catch (e) { return; }
    }
    if (!data || !data.type) return;
    if (data.type === 'theme' && data.tokensB64) {
      applyThemeUpdate(data.tokensB64);
      return;
    }
    if (data.type === 'catalog') {
      state.items = data.items || [];
      state.installed = {};
      state.installing = {};
      state.page = 1;
      (data.installed || []).forEach(function (name) {
        var key = String(name || '').trim().toLowerCase();
        if (key) state.installed[key] = true;
      });
      state.items.forEach(function (item) {
        if (item && item.installed) {
          markInstalled([item.englishName, item.name, item.id], item.id);
        }
      });
      if (data.error) {
        setStatus(data.error, true);
        state.items = [];
        closeDetail();
        render();
        return;
      }
      if (!state.items.length) setStatus(data.emptyMessage || '暂无可用技能。', true);
      if (state.detailId && !findItem(state.detailId)) closeDetail();
      render();
      return;
    }
    if (data.type === 'installResult') {
      var resultId = data.id == null ? '' : String(data.id);
      if (resultId) delete state.installing[resultId];
      else state.installing = {};
      if (data.ok) {
        markInstalled([data.englishName, data.name, data.id], resultId);
      } else if (data.error) {
        setStatus(data.error, true);
      }
      render();
    }
  }

  document.getElementById('search').addEventListener('input', function (e) {
    state.query = e.target.value || '';
    state.page = 1;
    render();
  });
  document.getElementById('positionFilter').addEventListener('change', function (e) {
    state.position = e.target.value || '';
    state.page = 1;
    render();
  });
  document.getElementById('sortFilter').addEventListener('change', function (e) {
    state.sort = e.target.value || '';
    state.page = 1;
    render();
  });
  document.getElementById('manageBtn').addEventListener('click', function () {
    post({ type: 'manage' });
  });
  document.getElementById('drawerClose').addEventListener('click', closeDetail);
  document.getElementById('drawerBackdrop').addEventListener('click', closeDetail);
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') closeDetail();
  });

  document.getElementById('app').addEventListener('click', function (e) {
    var btn = e.target.closest('[data-action]');
    if (!btn || btn.disabled) return;
    var action = btn.getAttribute('data-action');
    if (action === 'category') {
      state.category = btn.getAttribute('data-value') || '';
      state.page = 1;
      render();
      return;
    }
    if (action === 'page') {
      var page = Number(btn.getAttribute('data-page'));
      if (!page || page < 1) return;
      state.page = page;
      render();
      return;
    }
    if (action === 'detail') {
      openDetail(btn.getAttribute('data-id'));
      return;
    }
    if (action === 'add') {
      beginInstall(btn.getAttribute('data-id'), btn.getAttribute('data-mode'));
    }
  });

  function beginInstall(id, mode) {
    if (!id) return;
    state.installing[id] = mode === 'update' ? 'update' : 'use';
    render();
    post({ type: 'add', id: id, mode: state.installing[id] });
  }

  document.getElementById('drawer').addEventListener('click', function (e) {
    var btn = e.target.closest('[data-action="add"]');
    if (!btn || btn.disabled) return;
    beginInstall(btn.getAttribute('data-id'), btn.getAttribute('data-mode'));
  });

  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', function (event) {
      onHostMessage(event.data);
    });
  }

  window.applyThemeUpdate = applyThemeUpdate;
  post({ type: 'ready' });
})();
