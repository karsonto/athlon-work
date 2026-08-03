(function () {
  "use strict";
  if (window.__athlonAria && window.__athlonAria.__version === "2") return;

  var REF_MAX_AGE_MS = 10 * 60 * 1000;
  var ariaElementToRef = new WeakMap();
  var ariaRefStore = new Map();
  var ariaRefSeq = 0;

  function now() { return Date.now(); }
  function normalizeSpace(v) { return (v || "").replace(/\s+/g, " ").trim(); }
  function truncateText(v, n) {
    v = normalizeSpace(v);
    if (!v) return "";
    return v.length <= n ? v : v.slice(0, n - 1) + "…";
  }

  function pruneAriaRefs() {
    var cutoff = now() - REF_MAX_AGE_MS;
    ariaRefStore.forEach(function (entry, ref) {
      if (entry.createdAt < cutoff || !entry.element || !entry.element.isConnected) {
        ariaRefStore.delete(ref);
      }
    });
  }

  function getOrCreateAriaRef(element, path, frameRef) {
    pruneAriaRefs();
    var existing = ariaElementToRef.get(element);
    if (existing) {
      ariaRefStore.set(existing, { element: element, createdAt: now(), path: path, frameRef: frameRef });
      return existing;
    }
    var ref = "aria_" + (++ariaRefSeq).toString(36);
    ariaElementToRef.set(element, ref);
    ariaRefStore.set(ref, { element: element, createdAt: now(), path: path, frameRef: frameRef });
    return ref;
  }

  function normalizeAriaRef(ref) {
    if (typeof ref !== "string") return null;
    var trimmed = ref.trim();
    if (!trimmed) return null;
    var m = trimmed.match(/^\[ref=(aria_[a-z0-9]+)\]$/i) || trimmed.match(/^ref=(aria_[a-z0-9]+)$/i);
    if (m) return m[1];
    if (/^aria_[a-z0-9]+$/i.test(trimmed)) return trimmed;
    return null;
  }

  function getStoredAriaElement(ref) {
    var normalized = normalizeAriaRef(ref);
    if (!normalized) return null;
    var entry = ariaRefStore.get(normalized);
    if (!entry || !entry.element || !entry.element.isConnected) {
      ariaRefStore.delete(normalized);
      return null;
    }
    entry.createdAt = now();
    return entry.element;
  }

  function isActuallyVisible(el) {
    var style = window.getComputedStyle(el);
    if (style.display === "none" || style.visibility === "hidden" || style.visibility === "collapse") return false;
    var rect = el.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0;
  }

  function isObscuredButMeaningfulFormControl(el) {
    if (!(el instanceof HTMLSelectElement || el instanceof HTMLTextAreaElement)) return false;
    var style = window.getComputedStyle(el);
    if (style.display === "none" || style.visibility === "hidden" || style.visibility === "collapse") return false;
    var rect = el.getBoundingClientRect();
    if (rect.width > 0 && rect.height > 0) return false;
    if (el.tabIndex >= 0) return true;
    if (normalizeSpace(el.getAttribute("aria-label"))) return true;
    if (resolveAriaLabelledBy(el)) return true;
    var id = el.getAttribute("id");
    if (id && el.ownerDocument.querySelector('label[for="' + CSS.escape(id) + '"]')) return true;
    return Boolean(el.closest("label"));
  }

  function isIncludedInAriaTree(el) {
    return isActuallyVisible(el) || isObscuredButMeaningfulFormControl(el);
  }

  function buildSelectorHint(el) {
    var id = el.getAttribute("id");
    if (id) return "#" + CSS.escape(id);
    var testId = el.getAttribute("data-testid") || el.getAttribute("data-test-id") || el.getAttribute("data-test");
    if (testId) return '[data-testid="' + CSS.escape(testId) + '"]';
    var tag = el.tagName.toLowerCase();
    var role = el.getAttribute("role");
    return role ? tag + '[role="' + role + '"]' : tag;
  }

  function getElementRect(el) {
    var r = el.getBoundingClientRect();
    return { x: r.x, y: r.y, width: r.width, height: r.height };
  }

  function resolveAriaLabelledBy(el) {
    var raw = normalizeSpace(el.getAttribute("aria-labelledby"));
    if (!raw) return undefined;
    var ids = raw.split(/\s+/).filter(Boolean);
    var text = ids.map(function (id) {
      return normalizeSpace(el.ownerDocument.getElementById(id) && el.ownerDocument.getElementById(id).textContent);
    }).filter(Boolean).join(" ");
    return text || undefined;
  }

  function getLabelText(el) {
    var ariaLabel = normalizeSpace(el.getAttribute("aria-label"));
    if (ariaLabel) return ariaLabel;
    var labelledBy = resolveAriaLabelledBy(el);
    if (labelledBy) return labelledBy;
    if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement || el instanceof HTMLSelectElement) {
      var id = el.getAttribute("id");
      if (id) {
        var label = el.ownerDocument.querySelector('label[for="' + CSS.escape(id) + '"]');
        var labelText = normalizeSpace(label && label.textContent);
        if (labelText) return labelText;
      }
      var wrap = el.closest("label");
      var wrapText = normalizeSpace(wrap && wrap.textContent);
      if (wrapText) return wrapText;
    }
    if (el instanceof HTMLImageElement && normalizeSpace(el.alt)) return normalizeSpace(el.alt);
    var title = normalizeSpace(el.getAttribute("title"));
    if (title) return title;
    if ((el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) && normalizeSpace(el.placeholder)) {
      return normalizeSpace(el.placeholder);
    }
    if ((el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement || el instanceof HTMLSelectElement) &&
        normalizeSpace(el.getAttribute("name"))) {
      return normalizeSpace(el.getAttribute("name"));
    }
    return normalizeSpace(el.innerText || el.textContent) || undefined;
  }

  function inferRole(el) {
    var explicit = normalizeSpace(el.getAttribute("role"));
    if (explicit && explicit !== "presentation" && explicit !== "none") return explicit;
    if (el instanceof HTMLButtonElement) return "button";
    if (el instanceof HTMLAnchorElement && el.href) return "link";
    if (el instanceof HTMLTextAreaElement) return "textbox";
    if (el instanceof HTMLSelectElement) return el.multiple ? "listbox" : "combobox";
    if (el instanceof HTMLInputElement) {
      var type = (el.type || "text").toLowerCase();
      if (type === "checkbox") return "checkbox";
      if (type === "radio") return "radio";
      if (["button", "submit", "reset"].indexOf(type) >= 0) return "button";
      if (["email", "password", "search", "tel", "text", "url", "number"].indexOf(type) >= 0) return "textbox";
    }
    var tag = el.tagName.toLowerCase();
    if (tag === "img") return "img";
    if (tag === "summary") return "button";
    if (tag === "dialog") return "dialog";
    if (tag === "main") return "main";
    if (tag === "nav") return "navigation";
    if (tag === "aside") return "complementary";
    if (tag === "header") return "banner";
    if (tag === "footer") return "contentinfo";
    if (tag === "section") return "region";
    if (tag === "form") return "form";
    if (tag === "ul" || tag === "ol") return "list";
    if (tag === "li") return "listitem";
    if (/^h[1-6]$/.test(tag)) return "heading";
    return undefined;
  }

  function inferLevel(el) {
    var ariaLevel = Number(el.getAttribute("aria-level"));
    if (Number.isFinite(ariaLevel) && ariaLevel > 0) return ariaLevel;
    var tag = el.tagName.toLowerCase();
    if (/^h[1-6]$/.test(tag)) return Number(tag.slice(1));
    return undefined;
  }

  function getAriaChecked(el) {
    if (el instanceof HTMLInputElement && (el.type === "checkbox" || el.type === "radio")) {
      return el.indeterminate ? "mixed" : el.checked;
    }
    var raw = normalizeSpace(el.getAttribute("aria-checked"));
    if (!raw) return undefined;
    if (raw === "mixed") return "mixed";
    return raw === "true";
  }

  function getAriaPressed(el) {
    var raw = normalizeSpace(el.getAttribute("aria-pressed"));
    if (!raw) return undefined;
    if (raw === "mixed") return "mixed";
    return raw === "true";
  }

  function getNodeStates(el, role) {
    var states = {};
    var checked = getAriaChecked(el);
    if (checked !== undefined) states.checked = checked;
    var disabled = (el instanceof HTMLButtonElement || el instanceof HTMLInputElement ||
      el instanceof HTMLSelectElement || el instanceof HTMLTextAreaElement)
      ? Boolean(el.disabled)
      : el.getAttribute("aria-disabled") === "true";
    if (disabled) states.disabled = true;
    var expandedAttr = el.getAttribute("aria-expanded");
    if (expandedAttr === "true") states.expanded = true;
    if (expandedAttr === "false") states.expanded = false;
    var selectedAttr = el.getAttribute("aria-selected");
    if (selectedAttr === "true") states.selected = true;
    if (selectedAttr === "false") states.selected = false;
    var pressed = getAriaPressed(el);
    if (pressed !== undefined) states.pressed = pressed;
    var level = inferLevel(el);
    if (level !== undefined) states.level = level;
    if ((el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) && el.readOnly) states.readonly = true;
    if ((el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) && el.required) states.required = true;
    return states;
  }

  function getNodeProps(el, role) {
    var props = {};
    if (role === "link" && el instanceof HTMLAnchorElement) props.url = el.href || undefined;
    if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement || el instanceof HTMLSelectElement) {
      props.value = truncateText(el.value, 200) || undefined;
    }
    if ((el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) && el.placeholder) {
      props.placeholder = el.placeholder;
    }
    return props;
  }

  function textContribution(el) {
    var text = normalizeSpace(el.innerText || el.textContent);
    return text ? truncateText(text, 200) : undefined;
  }

  function isInteractiveRole(role) {
    return Boolean(role && ["button", "link", "checkbox", "radio", "textbox", "combobox", "listbox", "option", "switch", "tab", "menuitem"].indexOf(role) >= 0);
  }

  function isElementInteractive(el, role) {
    if (isInteractiveRole(role)) return true;
    if (el instanceof HTMLButtonElement || el instanceof HTMLAnchorElement) return true;
    if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement || el instanceof HTMLSelectElement) return true;
    if (el.isContentEditable) return true;
    return el.hasAttribute("tabindex");
  }

  function shouldIncludeNode(el, role, filter) {
    if (!isIncludedInAriaTree(el)) return false;
    if (filter === "interactive" && !isElementInteractive(el, role)) return false;
    return Boolean(role || textContribution(el));
  }

  function formatStates(summary) {
    var parts = [];
    var s = summary.states || {};
    if (s.checked === true) parts.push("[checked]");
    if (s.checked === "mixed") parts.push("[checked=mixed]");
    if (s.disabled) parts.push("[disabled]");
    if (s.expanded === true) parts.push("[expanded]");
    if (s.selected === true) parts.push("[selected]");
    if (s.pressed === true) parts.push("[pressed]");
    if (s.level) parts.push("[level=" + s.level + "]");
    return parts.length ? " " + parts.join(" ") : "";
  }

  function formatNodeLine(summary) {
    var tag = summary.tag || "";
    var tagHint = (tag === "textarea" || tag === "select" || tag === "input") ? " <" + tag + ">" : "";
    var parts = ["- " + summary.role + tagHint];
    if (summary.name) parts.push('"' + String(summary.name).replace(/"/g, '\\"') + '"');
    if (summary.ref) parts.push("[ref=" + summary.ref + "]");
    var line = parts.join(" ") + formatStates(summary);
    if (summary.props && summary.props.url) return line + " -> " + summary.props.url;
    return line;
  }

  function buildPath(parentPath, role, index) {
    return parentPath ? parentPath + "/" + role + "[" + index + "]" : role + "[" + index + "]";
  }

  function summarizeNode(el, ref, path, frameRef) {
    var role = inferRole(el);
    var text = textContribution(el);
    var name = getLabelText(el);
    if (!role && !text) return null;
    return {
      ref: ref,
      role: role || "text",
      name: name || undefined,
      tag: el.tagName.toLowerCase(),
      text: text,
      path: path,
      states: getNodeStates(el, role),
      props: getNodeProps(el, role),
      rect: getElementRect(el),
      selectorHint: buildSelectorHint(el),
      frameRef: frameRef
    };
  }

  function getTraversableChildren(el) {
    var children = Array.prototype.slice.call(el.children || []);
    if (el.shadowRoot) children = children.concat(Array.prototype.slice.call(el.shadowRoot.children || []));
    if (el instanceof HTMLSlotElement) {
      try { children = children.concat(el.assignedElements({ flatten: true })); } catch (e) {}
    }
    return children;
  }

  function collectAriaTree(root, options) {
    var result = [];
    var childCounters = {};
    var filter = options.filter || null;
    function visit(el, currentDepth, parentPath, frameRef) {
      if (options.depth !== undefined && currentDepth > options.depth) return null;
      var role = inferRole(el);
      // Align with edge-plugin: when filtering interactive, still walk children of non-interactive parents.
      if (filter === "interactive" && !isElementInteractive(el, role)) {
        var passthrough = [];
        if (el instanceof HTMLIFrameElement) {
          try {
            if (el.contentDocument && el.contentDocument.body) {
              passthrough = collectAriaTree(el.contentDocument.body, {
                depth: options.depth,
                rootPath: parentPath,
                frameRef: frameRef,
                frames: options.frames,
                filter: filter
              });
            }
          } catch (e) {
            var iframeRef = getOrCreateAriaRef(el, (parentPath || "iframe") + "/iframe", frameRef);
            options.frames.push({ ref: iframeRef, role: "iframe", sameOrigin: false, src: el.src || undefined });
          }
        } else {
          getTraversableChildren(el).forEach(function (child) {
            var node = visit(child, currentDepth + 1, parentPath, frameRef);
            if (node) passthrough.push(node);
          });
        }
        return passthrough.length ? { summary: null, children: passthrough, _passthrough: true } : null;
      }
      var key = parentPath || "__root__";
      childCounters[key] = (childCounters[key] || 0) + 1;
      var path = buildPath(parentPath, role || el.tagName.toLowerCase(), childCounters[key]);
      var ref = getOrCreateAriaRef(el, path, frameRef);
      var summary = summarizeNode(el, ref, path, frameRef);
      var children = [];
      if (el instanceof HTMLIFrameElement) {
        try {
          if (el.contentDocument && el.contentDocument.body) {
            children = collectAriaTree(el.contentDocument.body, {
              depth: options.depth,
              rootPath: path,
              frameRef: ref,
              frames: options.frames,
              filter: filter
            });
          }
        } catch (e) {
          options.frames.push({ ref: ref, role: "iframe", sameOrigin: false, src: el.src || undefined });
        }
      } else {
        getTraversableChildren(el).forEach(function (child) {
          var node = visit(child, currentDepth + 1, path, frameRef);
          if (node) children.push(node);
        });
      }
      if (!summary) {
        return children.length ? { summary: { ref: ref, role: "group", path: path, selectorHint: buildSelectorHint(el), rect: getElementRect(el), frameRef: frameRef }, children: children } : null;
      }
      if (!shouldIncludeNode(el, role, filter)) {
        return children.length ? { summary: summary, children: children } : null;
      }
      return { summary: summary, children: children };
    }
    getTraversableChildren(root).forEach(function (child) {
      var node = visit(child, 1, options.rootPath, options.frameRef);
      if (!node) return;
      if (node._passthrough) {
        node.children.forEach(function (c) { result.push(c); });
      } else {
        result.push(node);
      }
    });
    return result;
  }

  function flattenTree(nodes, out) {
    out = out || [];
    nodes.forEach(function (n) {
      if (n.summary) out.push(n.summary);
      if (n.children) flattenTree(n.children, out);
    });
    return out;
  }

  function renderTree(nodes, depth, lines) {
    depth = depth || 0;
    lines = lines || [];
    for (var i = 0; i < nodes.length; i++) {
      var n = nodes[i];
      if (n.summary) {
        lines.push(Array(depth + 1).join("  ") + formatNodeLine(n.summary));
      }
      if (n.children && n.children.length) {
        renderTree(n.children, n.summary ? depth + 1 : depth, lines);
      }
    }
    return lines;
  }

  function scoreFieldMatch(field, wanted, exact, starts, contains) {
    if (!field) return 0;
    if (field === wanted) return exact;
    if (field.indexOf(wanted) === 0) return starts;
    if (field.indexOf(wanted) >= 0) return contains;
    return 0;
  }

  function getSearchFields(el) {
    var htmlEl = el;
    var inner = normalizeSpace(htmlEl.innerText || el.textContent).toLowerCase();
    var aria = normalizeSpace(el.getAttribute("aria-label")).toLowerCase();
    var title = normalizeSpace(el.getAttribute("title")).toLowerCase();
    var placeholder = normalizeSpace(el.getAttribute("placeholder")).toLowerCase();
    var value = "";
    if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement || el instanceof HTMLSelectElement) {
      value = normalizeSpace(el.value).toLowerCase();
    }
    var nameAttr = normalizeSpace(el.getAttribute("name")).toLowerCase();
    var label = normalizeSpace(getLabelText(el)).toLowerCase();
    var nearby = "";
    try {
      var parent = el.parentElement;
      if (parent) nearby = normalizeSpace(parent.innerText || parent.textContent).toLowerCase().slice(0, 200);
    } catch (e) {}
    return { inner: inner, aria: aria, title: title, placeholder: placeholder, value: value, name: nameAttr, label: label, nearby: nearby };
  }

  function scoreAriaNodeMatch(node, query) {
    var wantedName = normalizeSpace(query.name).toLowerCase();
    var wantedRole = normalizeSpace(query.role).toLowerCase();
    var wantedText = normalizeSpace(query.text).toLowerCase();
    var nodeRole = normalizeSpace(node.role).toLowerCase();
    if (wantedRole) {
      var roleOk = nodeRole === wantedRole
        || (wantedRole === "field" && ["textbox", "combobox", "listbox", "searchbox", "spinbutton"].indexOf(nodeRole) >= 0)
        || (wantedRole === "button" && (nodeRole === "button" || node.tag === "button"));
      if (!roleOk) return -1;
    }
    if (query.interactiveOnly && !isInteractiveRole(node.role)) return -1;

    var el = getStoredAriaElement(node.ref);
    var fields = el ? getSearchFields(el) : {
      inner: normalizeSpace(node.text).toLowerCase(),
      aria: normalizeSpace(node.name).toLowerCase(),
      title: "",
      placeholder: "",
      value: "",
      name: "",
      label: normalizeSpace(node.name).toLowerCase(),
      nearby: ""
    };

    var score = 0;
    var needle = wantedName || wantedText;
    if (needle) {
      var hay = [fields.label, fields.placeholder, fields.aria, fields.name, fields.title, fields.nearby, fields.inner, fields.value]
        .filter(Boolean).join(" | ");
      if (hay.indexOf(needle) < 0) return -1;
      score = Math.max(score, scoreFieldMatch(fields.label, needle, 120, 100, 80));
      score = Math.max(score, scoreFieldMatch(fields.placeholder, needle, 110, 90, 75));
      score = Math.max(score, scoreFieldMatch(fields.aria, needle, 105, 90, 75));
      score = Math.max(score, scoreFieldMatch(fields.name, needle, 90, 75, 60));
      score = Math.max(score, scoreFieldMatch(fields.title, needle, 85, 70, 55));
      score = Math.max(score, scoreFieldMatch(fields.nearby, needle, 80, 68, 52));
      score = Math.max(score, scoreFieldMatch(fields.inner, needle, 75, 60, 45));
      score = Math.max(score, scoreFieldMatch(fields.value, needle, 70, 55, 40));
    }
    if (wantedRole) score += 20;
    if (wantedRole === "button" && (nodeRole === "button" || node.tag === "button")) score += 10;
    if (wantedRole === "field" && ["textbox", "combobox", "listbox"].indexOf(nodeRole) >= 0) score += 15;
    if (query.intent === "type" && node.role !== "textbox") score -= 30;
    if (query.intent === "click" && isInteractiveRole(node.role)) score += 10;
    if (query.intent === "select" && (node.role === "combobox" || node.role === "listbox" || node.role === "option")) score += 15;
    return score;
  }

  function findAriaTargets(query) {
    var scopeRef = query.scopeRef ? normalizeAriaRef(query.scopeRef) : undefined;
    var scope = scopeRef ? getStoredAriaElement(scopeRef) : document.body;
    if (!scope) return [];
    var frames = [];
    var flattened = flattenTree(collectAriaTree(scope, { rootPath: scopeRef, frames: frames }));
    var scored = [];
    flattened.forEach(function (node) {
      var score = scoreAriaNodeMatch(node, query);
      if (score >= 0) scored.push({ score: score, node: node });
    });
    scored.sort(function (a, b) { return b.score - a.score; });
    var limit = Math.min(Math.max(Number(query.limit) || 5, 1), 10);
    return scored.slice(0, limit).map(function (x) { return x.node; });
  }

  function findAriaTarget(query) {
    return findAriaTargets(query)[0];
  }

  function getAvailableActions(el) {
    var role = inferRole(el);
    var actions = [];
    if (isElementInteractive(el, role)) actions.push("click");
    if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement || el.isContentEditable) actions.push("type", "press");
    if (el instanceof HTMLSelectElement || role === "combobox" || role === "listbox") actions.push("selectOption");
    return actions;
  }

  function dispatchSyntheticMouseClick(target) {
    var view = (target.ownerDocument && target.ownerDocument.defaultView) || window;
    var rect = target.getBoundingClientRect();
    var clientX = rect.left + Math.max(rect.width, 0) / 2;
    var clientY = rect.top + Math.max(rect.height, 0) / 2;
    var init = { bubbles: true, cancelable: true, view: view, clientX: clientX, clientY: clientY, button: 0 };
    target.dispatchEvent(new MouseEvent("mousedown", Object.assign({}, init, { buttons: 1 })));
    target.dispatchEvent(new MouseEvent("mouseup", Object.assign({}, init, { buttons: 0 })));
    target.dispatchEvent(new MouseEvent("click", Object.assign({}, init, { buttons: 0 })));
  }

  function setNativeValue(el, value) {
    var proto = el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    var desc = Object.getOwnPropertyDescriptor(proto, "value");
    if (desc && desc.set) desc.set.call(el, value);
    else el.value = value;
  }

  function dispatchInputEvents(el) {
    el.dispatchEvent(new Event("input", { bubbles: true }));
    el.dispatchEvent(new Event("change", { bubbles: true }));
  }

  function dispatchKeyboardSequence(el, key) {
    var init = { key: key, bubbles: true, cancelable: true };
    el.dispatchEvent(new KeyboardEvent("keydown", init));
    el.dispatchEvent(new KeyboardEvent("keypress", init));
    el.dispatchEvent(new KeyboardEvent("keyup", init));
  }

  function snapshotFingerprint() {
    return location.href + "::" + (document.body && document.body.innerText ? document.body.innerText.length : 0) + "::" + document.querySelectorAll("*").length;
  }

  function sleep(ms) {
    return new Promise(function (resolve) { setTimeout(resolve, ms); });
  }

  function waitForNextPaint() {
    return new Promise(function (resolve) { requestAnimationFrame(function () { resolve(); }); });
  }

  function findPopupContainer(el) {
    var controls = el.getAttribute("aria-controls");
    if (controls) {
      var byId = document.getElementById(controls.split(/\s+/)[0]);
      if (byId) return byId;
    }
    var listbox = document.querySelector('[role="listbox"]');
    return listbox || null;
  }

  function findPopupOption(popup, opts) {
    var options = Array.prototype.slice.call(popup.querySelectorAll('[role="option"], option, li, [data-value]'));
    for (var i = 0; i < options.length; i++) {
      var item = options[i];
      var label = normalizeSpace(item.innerText || item.textContent);
      var value = normalizeSpace(item.getAttribute("data-value") || (item.value != null ? String(item.value) : ""));
      if (opts.value && value === opts.value) return item;
      if (opts.label && label === normalizeSpace(opts.label)) return item;
    }
    return null;
  }

  function readAriaTree(args) {
    args = args || {};
    var depth = Number.isInteger(args.depth) && args.depth >= 0 ? args.depth : undefined;
    var filter = args.filter === "interactive" ? "interactive" : null;
    var root = args.ref ? getStoredAriaElement(args.ref) : document.body;
    if (!root) {
      return {
        ok: false,
        tool: "readAriaTree",
        error: '未找到 ref "' + args.ref + '" 对应的节点。请重新 browser_read_aria_tree 或 browser_find_aria_nodes 获取有效 ref。'
      };
    }
    var frames = [];
    var tree = collectAriaTree(root, { depth: depth, rootPath: args.ref, frames: frames, filter: filter });
    var flattened = flattenTree(tree);
    var treeText = renderTree(tree).join("\n");
    var interactiveCount = flattened.filter(function (n) { return isInteractiveRole(n.role); }).length;
    var userControlled = depth !== undefined || Boolean(args.ref) || Boolean(filter);
    var sparse = !userControlled && (flattened.length < 6 || interactiveCount < 2);
    var activeEl = document.activeElement instanceof Element ? document.activeElement : null;
    var activeRef = activeEl ? getOrCreateAriaRef(activeEl, "active") : undefined;
    return {
      ok: true,
      tool: "readAriaTree",
      data: {
        tree: treeText,
        filter: filter || "all",
        nodeCount: flattened.length,
        refCount: flattened.length,
        sparse: sparse,
        fallbackSuggested: sparse,
        tips: sparse
          ? "Tree looks sparse. Retry with filter=\"interactive\", or use browser_find_aria_nodes with name/role/text."
          : undefined,
        depth: depth,
        rootRef: args.ref,
        activeRef: activeRef,
        frames: frames,
        observations: { url: location.href, title: document.title }
      }
    };
  }

  function findAriaNodes(args) {
    args = args || {};
    var scopeRef = args.scopeRef ? normalizeAriaRef(args.scopeRef) || undefined : undefined;
    if (args.scopeRef && !scopeRef) {
      return { ok: false, tool: "findAriaNodes", error: 'scopeRef "' + args.scopeRef + '" 格式无效，请使用完整 ref，例如 aria_1' };
    }
    if (!normalizeSpace(args.name) && !normalizeSpace(args.text) && !normalizeSpace(args.role)) {
      return {
        ok: false,
        tool: "findAriaNodes",
        error: "findAriaNodes 需要至少提供 name、text 或 role 之一。示例: {\"text\":\"登录\",\"role\":\"button\"} 或 {\"role\":\"textbox\",\"name\":\"邮箱\"}"
      };
    }
    var candidates = findAriaTargets({
      name: args.name, role: args.role, text: args.text, scopeRef: scopeRef,
      limit: args.limit, interactiveOnly: Boolean(args.interactiveOnly), intent: args.intent
    });
    return {
      ok: true,
      tool: "findAriaNodes",
      data: {
        query: {
          name: normalizeSpace(args.name) || undefined,
          role: normalizeSpace(args.role) || undefined,
          text: normalizeSpace(args.text) || undefined,
          scopeRef: scopeRef,
          limit: Math.min(Math.max(Number(args.limit) || 5, 1), 10),
          interactiveOnly: Boolean(args.interactiveOnly),
          intent: args.intent
        },
        candidates: candidates,
        observations: { url: location.href, title: document.title }
      }
    };
  }

  function resolveAriaRef(args) {
    var ref = typeof args === "string" ? args : (args && args.ref);
    var normalized = normalizeAriaRef(ref || "");
    if (!normalized) return { ok: false, tool: "resolveAriaRef", error: 'ref "' + (ref || "") + '" 格式无效，请使用完整 ref，例如 aria_1' };
    var el = getStoredAriaElement(normalized);
    if (!el) return { ok: false, tool: "resolveAriaRef", error: 'ref "' + normalized + '" 已失效，请重新读取语义树' };
    var path = (ariaRefStore.get(normalized) || {}).path || normalized;
    var summary = summarizeNode(el, normalized, path, (ariaRefStore.get(normalized) || {}).frameRef);
    return { ok: true, tool: "resolveAriaRef", data: { ref: normalized, node: summary } };
  }

  function ariaInspect(args) {
    var ref = typeof args === "string" ? args : (args && args.ref);
    var normalized = normalizeAriaRef(ref || "");
    if (!normalized) return { ok: false, tool: "ariaInspect", error: 'ref "' + (ref || "") + '" 格式无效，请使用完整 ref，例如 aria_1' };
    var el = getStoredAriaElement(normalized);
    if (!el) return { ok: false, tool: "ariaInspect", error: 'ref "' + normalized + '" 已失效，请重新读取语义树' };
    var path = (ariaRefStore.get(normalized) || {}).path || normalized;
    var summary = summarizeNode(el, normalized, path, (ariaRefStore.get(normalized) || {}).frameRef);
    if (!summary) return { ok: false, tool: "ariaInspect", error: 'ref "' + normalized + '" 不是可交互语义节点' };
    return {
      ok: true,
      tool: "ariaInspect",
      data: {
        node: summary,
        nearbyText: truncateText(normalizeSpace((el.closest("label, form, section, article, main, div") || {}).textContent), 240) || undefined,
        availableActions: getAvailableActions(el)
      }
    };
  }

  async function ariaInteract(args) {
    args = args || {};
    var ref = normalizeAriaRef(args.ref || "");
    var action = args.action;
    if (!ref) return { ok: false, tool: "ariaInteract", error: 'ref "' + (args.ref || "") + '" 格式无效，请使用完整 ref，例如 aria_1' };
    var el = getStoredAriaElement(ref);
    if (!el) return { ok: false, tool: "ariaInteract", error: 'ref "' + ref + '" 已失效，请重新读取语义树' };
    if (!action) return { ok: false, tool: "ariaInteract", error: "缺少 action" };
    var before = snapshotFingerprint();
    var path = (ariaRefStore.get(ref) || {}).path || ref;
    var target = summarizeNode(el, ref, path, (ariaRefStore.get(ref) || {}).frameRef);
    if (!target) return { ok: false, tool: "ariaInteract", error: 'ref "' + ref + '" 不是可交互语义节点' };
    var result = { action: action, ref: ref, target: target, success: true, beforeNode: target, changedFields: [] };
    try {
      if (action === "click") {
        el.scrollIntoView({ block: "center", inline: "nearest", behavior: "instant" });
        await waitForNextPaint();
        if (el.focus) el.focus();
        await waitForNextPaint();
        dispatchSyntheticMouseClick(el);
      } else if (action === "type") {
        var text = args.text != null ? String(args.text) : "";
        if (!text) throw new Error("缺少 text");
        if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) {
          el.focus();
          var nextValue = args.mode === "append" ? (el.value + text) : text;
          setNativeValue(el, nextValue);
          dispatchInputEvents(el);
          result.valuePreview = truncateText(nextValue, 120);
        } else if (el.isContentEditable) {
          el.focus();
          el.textContent = args.mode === "append" ? ((el.textContent || "") + text) : text;
          dispatchInputEvents(el);
          result.valuePreview = truncateText(normalizeSpace(el.textContent), 120);
        } else {
          throw new Error("目标节点不可输入");
        }
      } else if (action === "press") {
        var key = args.key != null ? String(args.key) : "";
        if (!key) throw new Error("缺少 key");
        if (el.focus) el.focus();
        dispatchKeyboardSequence(el, key);
        result.key = key;
      } else if (action === "selectOption") {
        if (el instanceof HTMLSelectElement) {
          var option = Array.prototype.slice.call(el.options).find(function (item) {
            return args.value ? item.value === args.value : (args.label ? item.label.trim() === String(args.label).trim() : false);
          });
          if (!option) throw new Error("未找到匹配的下拉选项");
          el.value = option.value;
          dispatchInputEvents(el);
          result.selectedValue = option.value;
          result.selectedLabel = option.label;
          result.valuePreview = truncateText(option.label || option.value, 120);
        } else {
          var role = inferRole(el);
          if (role !== "combobox" && role !== "listbox") throw new Error("目标节点不是可选择控件");
          var popup = findPopupContainer(el);
          if (!popup) throw new Error("未找到关联的选项面板，请先展开下拉后重试");
          var popOption = findPopupOption(popup, { value: args.value, label: args.label });
          if (!popOption) throw new Error("在当前选项面板中未找到匹配项");
          popOption.scrollIntoView({ block: "nearest", inline: "nearest", behavior: "instant" });
          await waitForNextPaint();
          dispatchSyntheticMouseClick(popOption);
          result.selectedValue = normalizeSpace(popOption.getAttribute("data-value")) || undefined;
          result.selectedLabel = normalizeSpace(popOption.innerText || popOption.textContent) || undefined;
          result.valuePreview = truncateText(result.selectedLabel || result.selectedValue || "", 120);
        }
      } else {
        throw new Error("不支持的 action: " + action);
      }
    } catch (err) {
      return { ok: false, tool: "ariaInteract", error: err && err.message ? err.message : "未知错误" };
    }
    var after = snapshotFingerprint();
    var refreshed = getStoredAriaElement(ref);
    var afterPath = (ariaRefStore.get(ref) || {}).path || ref;
    var afterNode = refreshed ? summarizeNode(refreshed, ref, afterPath, (ariaRefStore.get(ref) || {}).frameRef) || undefined : undefined;
    result.urlChanged = before.split("::")[0] !== after.split("::")[0];
    result.domChanged = before !== after;
    result.treeChanged = result.domChanged;
    result.reloadSuggested = result.domChanged;
    result.afterNode = afterNode;
    result.changedFields = [];
    if ((result.beforeNode && result.beforeNode.props && result.beforeNode.props.value) !== (afterNode && afterNode.props && afterNode.props.value)) result.changedFields.push("value");
    if ((result.beforeNode && result.beforeNode.states && result.beforeNode.states.expanded) !== (afterNode && afterNode.states && afterNode.states.expanded)) result.changedFields.push("expanded");
    if ((result.beforeNode && result.beforeNode.states && result.beforeNode.states.selected) !== (afterNode && afterNode.states && afterNode.states.selected)) result.changedFields.push("selected");
    if ((result.beforeNode && result.beforeNode.states && result.beforeNode.states.checked) !== (afterNode && afterNode.states && afterNode.states.checked)) result.changedFields.push("checked");
    return { ok: true, tool: "ariaInteract", data: result };
  }

  async function waitForAria(args) {
    args = args || {};
    var state = args.state || "appear";
    var normalizedRef = args.ref !== undefined ? (normalizeAriaRef(args.ref) || undefined) : undefined;
    if (args.ref !== undefined && !normalizedRef) {
      return { ok: false, tool: "waitForAria", error: 'ref "' + args.ref + '" 格式无效，请使用完整 ref，例如 aria_1' };
    }
    var timeoutMs = Math.min(Math.max(Number(args.timeoutMs) || 8000, 200), 30000);
    var startedAt = now();
    var stableSince = 0;
    var lastFingerprint = "";
    var initialTarget = findAriaTarget({ ref: normalizedRef, name: args.name, role: args.role });
    var initialValue = initialTarget && initialTarget.props ? initialTarget.props.value : undefined;
    var initialExpanded = initialTarget && initialTarget.states ? initialTarget.states.expanded : undefined;
    var initialSelected = initialTarget && initialTarget.states ? initialTarget.states.selected : undefined;
    while (now() - startedAt < timeoutMs) {
      var target = findAriaTarget({ ref: normalizedRef, name: args.name, role: args.role });
      var matched = Boolean(target);
      if (state === "appear" && matched) {
        return { ok: true, tool: "waitForAria", data: { matched: true, elapsedMs: now() - startedAt, matchedRef: target.ref } };
      }
      if (state === "disappear" && !matched) {
        return { ok: true, tool: "waitForAria", data: { matched: true, elapsedMs: now() - startedAt } };
      }
      if (state === "stable" && matched) {
        var fp = target.ref + ":" + (target.name || "") + ":" + (target.text || "") + ":" + ((target.states && target.states.expanded) || "");
        if (fp === lastFingerprint) {
          if (!stableSince) stableSince = now();
          if (now() - stableSince >= 800) {
            return { ok: true, tool: "waitForAria", data: { matched: true, elapsedMs: now() - startedAt, matchedRef: target.ref } };
          }
        } else {
          lastFingerprint = fp;
          stableSince = now();
        }
      }
      if (state === "valueChanged" && matched && target.props && target.props.value !== initialValue) {
        return { ok: true, tool: "waitForAria", data: { matched: true, elapsedMs: now() - startedAt, matchedRef: target.ref } };
      }
      if (state === "expandedChanged" && matched && target.states && target.states.expanded !== initialExpanded) {
        return { ok: true, tool: "waitForAria", data: { matched: true, elapsedMs: now() - startedAt, matchedRef: target.ref } };
      }
      if (state === "selectedChanged" && matched && target.states && target.states.selected !== initialSelected) {
        return { ok: true, tool: "waitForAria", data: { matched: true, elapsedMs: now() - startedAt, matchedRef: target.ref } };
      }
      await sleep(200);
    }
    return { ok: false, tool: "waitForAria", error: "等待超时 (" + state + ")" };
  }

  async function invoke(operation, args) {
    args = args || {};
    switch (operation) {
      case "readAriaTree": return readAriaTree(args);
      case "findAriaNodes": return findAriaNodes(args);
      case "resolveAriaRef": return resolveAriaRef(args);
      case "ariaInspect": return ariaInspect(args);
      case "ariaInteract": return await ariaInteract(args);
      case "waitForAria": return await waitForAria(args);
      default: return { ok: false, error: 'Unknown ARIA operation "' + operation + '"' };
    }
  }

  window.__athlonAria = {
    __version: "2",
    readAriaTree: readAriaTree,
    findAriaNodes: findAriaNodes,
    resolveAriaRef: resolveAriaRef,
    ariaInspect: ariaInspect,
    ariaInteract: ariaInteract,
    waitForAria: waitForAria,
    invoke: invoke
  };
})();
