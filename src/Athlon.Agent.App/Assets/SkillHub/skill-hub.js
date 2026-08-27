(function () {
  var state = {
    items: [],
    installed: {},
    installing: {},
    query: '',
    category: '',
    featuredExpanded: false,
    featuredLimit: 6
  };

  function post(payload) {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(payload);
    }
  }

  function escapeHtml(text) {
    return String(text || '')
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  function initial(item) {
    var name = (item.name || item.englishName || '?').trim();
    return name ? name.charAt(0).toUpperCase() : '?';
  }

  function displayName(item) {
    return (item.name || item.englishName || '').trim();
  }

  function englishNameSuffix(item) {
    var en = (item.englishName || '').trim();
    if (!en) return '';
    var primary = displayName(item);
    // Avoid duplicating when name already is / equals the english id.
    if (!primary || primary.toLowerCase() === en.toLowerCase()) return '';
    return en;
  }

  function nameHtml(item, nameClass, enClass) {
    var primary = displayName(item);
    var en = englishNameSuffix(item);
    var html = '<div class="' + nameClass + '">' + escapeHtml(primary) + '</div>';
    if (en) {
      html += '<div class="' + enClass + '">' + escapeHtml(en) + '</div>';
    }
    return html;
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

  function filteredItems() {
    var q = state.query.trim().toLowerCase();
    return state.items.filter(function (item) {
      if (state.category && (item.category || '') !== state.category) return false;
      if (!q) return true;
      var hay = [item.name, item.englishName, item.description, item.category, item.position]
        .join(' ')
        .toLowerCase();
      return hay.indexOf(q) >= 0;
    });
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

  function renderDiscover(items) {
    var section = document.getElementById('discover');
    var grid = document.getElementById('discoverGrid');
    if (!section || !grid) return;
    var discover = items.slice(0, 3);
    if (!discover.length || state.category || state.query) {
      section.hidden = true;
      grid.innerHTML = '';
      return;
    }
    section.hidden = false;
    grid.innerHTML = discover.map(function (item) {
      return '<article class="discover-card">' +
        '<div class="discover-icon">' + escapeHtml(initial(item)) + '</div>' +
        nameHtml(item, 'discover-name', 'discover-en') +
        '<div class="discover-desc">' + escapeHtml(item.description || '') + '</div>' +
        '<div class="discover-badge">' + escapeHtml(item.category || 'Skill') + '</div>' +
        '</article>';
    }).join('');
  }

  function renderRow(item) {
    var installed = isInstalled(item);
    var installing = !!state.installing[item.id];
    var actionHtml;
    if (installed) {
      // Local skill already present — never show Add.
      actionHtml = '<span class="installed-label">Installed</span>';
    } else if (installing) {
      actionHtml = '<button type="button" class="add-btn" disabled>Adding…</button>';
    } else {
      actionHtml = '<button type="button" class="add-btn" data-action="add" data-id="' +
        escapeHtml(item.id) + '">Add</button>';
    }
    return '<div class="skill-row" data-id="' + escapeHtml(item.id) + '">' +
      '<div class="skill-row-icon">' + escapeHtml(initial(item)) + '</div>' +
      '<div class="skill-row-body">' +
      nameHtml(item, 'skill-row-name', 'skill-row-en') +
      '<div class="skill-row-desc">' + escapeHtml(item.description || '') + '</div>' +
      '</div>' +
      actionHtml +
      '</div>';
  }

  function renderFeatured(items) {
    var section = document.getElementById('featured');
    var list = document.getElementById('featuredList');
    var more = document.getElementById('featuredMore');
    if (!section || !list || !more) return;

    if (state.category || state.query) {
      section.hidden = true;
      list.innerHTML = '';
      more.hidden = true;
      return;
    }

    var featured = items.slice(3);
    if (!featured.length) {
      section.hidden = true;
      list.innerHTML = '';
      more.hidden = true;
      return;
    }

    section.hidden = false;
    var visible = state.featuredExpanded ? featured : featured.slice(0, state.featuredLimit);
    list.innerHTML = visible.map(renderRow).join('');
    var hiddenCount = featured.length - state.featuredLimit;
    if (!state.featuredExpanded && hiddenCount > 0) {
      more.hidden = false;
      more.textContent = 'Show ' + hiddenCount + ' more';
    } else {
      more.hidden = true;
    }
  }

  function renderCategories(items) {
    var host = document.getElementById('categorySections');
    if (!host) return;

    var byCategory = {};
    items.forEach(function (item) {
      var key = (item.category || '').trim() || 'Other';
      if (!byCategory[key]) byCategory[key] = [];
      byCategory[key].push(item);
    });

    var keys = Object.keys(byCategory).sort(function (a, b) {
      return a.localeCompare(b, 'zh');
    });

    // When browsing All with no search, still show category sections for everything
    // after Discover+Featured would confuse; show all items under categories always
    // when filtered, otherwise show every category with its skills.
    host.innerHTML = keys.map(function (key) {
      var rows = byCategory[key].map(renderRow).join('');
      return '<section class="section">' +
        '<h2 class="section-title">' + escapeHtml(key) + '</h2>' +
        '<div class="skill-list">' + rows + '</div>' +
        '</section>';
    }).join('');
  }

  function fillCategoryFilter(items) {
    var select = document.getElementById('categoryFilter');
    if (!select) return;
    var current = state.category;
    var cats = [];
    items.forEach(function (item) {
      var c = (item.category || '').trim();
      if (c && cats.indexOf(c) < 0) cats.push(c);
    });
    cats.sort(function (a, b) { return a.localeCompare(b, 'zh'); });
    select.innerHTML = '<option value="">All</option>' +
      cats.map(function (c) {
        return '<option value="' + escapeHtml(c) + '"' +
          (c === current ? ' selected' : '') + '>' + escapeHtml(c) + '</option>';
      }).join('');
  }

  function render() {
    var items = filteredItems();
    if (!state.items.length) {
      document.getElementById('discover').hidden = true;
      document.getElementById('featured').hidden = true;
      document.getElementById('categorySections').innerHTML = '';
      return;
    }
    setStatus('', false);
    fillCategoryFilter(state.items);
    renderDiscover(items);
    renderFeatured(items);
    renderCategories(items);
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
    if (text) {
      document.body.style.color = text;
    }
  }

  function applyThemeUpdate(tokensB64) {
    var tokensCss = decodeBase64Utf8(tokensB64);
    var tokensEl = document.getElementById('skill-hub-theme-tokens');
    if (tokensEl) {
      tokensEl.textContent = tokensCss;
    }
    applyThemeTokensToRoot(tokensCss);
    syncThemeSurfaces();
  }

  function onHostMessage(data) {
    if (!data || !data.type) return;
    if (data.type === 'theme' && data.tokensB64) {
      applyThemeUpdate(data.tokensB64);
      return;
    }
    if (data.type === 'catalog') {
      state.items = data.items || [];
      state.installed = {};
      (data.installed || []).forEach(function (name) {
        var key = String(name || '').trim().toLowerCase();
        if (key) state.installed[key] = true;
      });
      // Prefer per-item installed flags from host.
      state.items.forEach(function (item) {
        if (item && item.installed) {
          [item.englishName, item.name, item.id].forEach(function (name) {
            var key = String(name || '').trim().toLowerCase();
            if (key) state.installed[key] = true;
          });
        }
      });
      if (data.error) {
        setStatus(data.error, true);
        document.getElementById('discover').hidden = true;
        document.getElementById('featured').hidden = true;
        document.getElementById('categorySections').innerHTML = '';
        fillCategoryFilter([]);
        return;
      }
      if (!state.items.length) {
        setStatus(data.emptyMessage || 'No skills available.', true);
      }
      render();
      return;
    }
    if (data.type === 'installResult') {
      delete state.installing[data.id];
      if (data.ok) {
        var keys = [data.englishName, data.name, data.id];
        keys.forEach(function (name) {
          var key = String(name || '').trim().toLowerCase();
          if (key) state.installed[key] = true;
        });
        state.items.forEach(function (item) {
          if (item && item.id === data.id) item.installed = true;
        });
      } else if (data.error) {
        setStatus(data.error, true);
      }
      render();
    }
  }

  document.getElementById('search').addEventListener('input', function (e) {
    state.query = e.target.value || '';
    render();
  });
  document.getElementById('categoryFilter').addEventListener('change', function (e) {
    state.category = e.target.value || '';
    render();
  });
  document.getElementById('manageBtn').addEventListener('click', function () {
    post({ type: 'manage' });
  });
  document.getElementById('featuredMore').addEventListener('click', function () {
    state.featuredExpanded = true;
    render();
  });
  document.getElementById('content').addEventListener('click', function (e) {
    var btn = e.target.closest('[data-action="add"]');
    if (!btn || btn.disabled) return;
    var id = btn.getAttribute('data-id');
    if (!id) return;
    state.installing[id] = true;
    render();
    post({ type: 'add', id: id });
  });

  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', function (event) {
      onHostMessage(event.data);
    });
  }

  post({ type: 'ready' });
})();
