using System.Text.Json;
using Athlon.Agent.Core.Knowledge;

namespace Athlon.Agent.App.Windows;

internal static class KnowledgeGraphHtml
{
    public static string Build(KnowledgeModuleGraph graph)
    {
        var payload = JsonSerializer.Serialize(new
        {
            nodes = graph.Nodes.Select(node => new
            {
                id = node.Id,
                name = node.Name,
                type = node.EntityType,
                description = node.Description,
                evidence = node.Evidence.Select(item => new
                {
                    fileName = item.FileName,
                    title = item.TitlePath,
                    page = item.PageNumber,
                    content = item.Content
                })
            }),
            edges = graph.Edges.Select(edge => new
            {
                id = edge.Id,
                source = edge.SourceId,
                target = edge.TargetId,
                sourceName = edge.SourceName,
                targetName = edge.TargetName,
                description = edge.Description,
                weight = edge.Weight,
                evidence = edge.Evidence
            })
        });
        payload = payload.Replace("<", "\\u003c", StringComparison.Ordinal);
        return Template.Replace("/*__GRAPH__*/", payload, StringComparison.Ordinal);
    }

    private const string Template = """
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8" />
        <style>
          html, body { margin: 0; height: 100%; background: #f6f5f3; color: #1c1c1c; font: 13px/1.4 "Segoe UI", sans-serif; }
          #app { display: flex; height: 100%; }
          #stage { flex: 1; position: relative; overflow: hidden; }
          #toolbar { position: absolute; top: 12px; left: 12px; right: 12px; display: flex; gap: 8px; z-index: 2; }
          #search { flex: 1; max-width: 280px; border: 1px solid #d0cdc6; border-radius: 6px; padding: 6px 8px; background: #fff; }
          #legend { display: flex; gap: 10px; align-items: center; color: #5c5852; }
          .swatch { width: 8px; height: 8px; border-radius: 99px; display: inline-block; margin-right: 4px; }
          #world { position: absolute; inset: 0; transform-origin: 0 0; }
          svg { position: absolute; inset: 0; overflow: visible; }
          .node { position: absolute; width: 18px; height: 18px; margin: -9px 0 0 -9px; border-radius: 99px; cursor: grab; }
          .node span { position: absolute; left: 22px; top: -1px; white-space: nowrap; font-size: 12px; }
          .node.match { outline: 2px solid #1c1c1c; outline-offset: 2px; }
          .node.dim { opacity: 0.28; }
          #side { width: 320px; border-left: 1px solid #e4e0d8; background: #fff; padding: 16px; overflow: auto; }
          #side h2 { margin: 0 0 6px; font-size: 16px; }
          #side .type { color: #6b6560; margin-bottom: 10px; }
          #side .rel { margin: 0 0 8px; }
          #side .evidence { margin-top: 12px; padding-top: 10px; border-top: 1px solid #eee; }
          #side pre { white-space: pre-wrap; font: 12px/1.45 Consolas, monospace; margin: 4px 0 0; }
          .empty { padding: 24px; color: #6b6560; }
        </style>
        </head>
        <body>
        <div id="app">
          <div id="stage">
            <div id="toolbar">
              <input id="search" placeholder="搜索节点" />
              <div id="legend"></div>
            </div>
            <div id="world"></div>
          </div>
          <aside id="side"><div class="empty">点击节点查看关系和证据切片。</div></aside>
        </div>
        <script id="graph-data" type="application/json">/*__GRAPH__*/</script>
        <script>
        const data = JSON.parse(document.getElementById("graph-data").textContent);
        const colors = { organization: "#3b6ea5", person: "#2f7d4a", geo: "#b86e00", event: "#7a4ea3" };
        const labels = { organization: "组织", person: "人物", geo: "地点", event: "事件" };
        const legend = document.getElementById("legend");
        Object.keys(colors).forEach(type => {
          const item = document.createElement("span");
          item.innerHTML = `<i class="swatch" style="background:${colors[type]}"></i>${labels[type]}`;
          legend.appendChild(item);
        });
        const world = document.getElementById("world");
        const side = document.getElementById("side");
        const stage = document.getElementById("stage");
        if (!data.nodes.length) {
          world.outerHTML = '<div class="empty">这个知识空间还没有图谱。上传文档或使用「重建图谱」。</div>';
        } else {
          const nodes = data.nodes.map((node, index) => {
            const angle = (Math.PI * 2 * index) / data.nodes.length;
            const radius = 80 + data.nodes.length * 8;
            return { ...node, x: Math.cos(angle) * radius + 480, y: Math.sin(angle) * radius + 360, vx: 0, vy: 0 };
          });
          const byId = Object.fromEntries(nodes.map(node => [node.id, node]));
          const edges = data.edges.filter(edge => byId[edge.source] && byId[edge.target]);
          for (let step = 0; step < 80; step++) {
            for (let i = 0; i < nodes.length; i++) {
              for (let j = i + 1; j < nodes.length; j++) {
                let dx = nodes[j].x - nodes[i].x;
                let dy = nodes[j].y - nodes[i].y;
                const dist = Math.max(12, Math.hypot(dx, dy));
                const force = 900 / (dist * dist);
                dx /= dist; dy /= dist;
                nodes[i].vx -= dx * force; nodes[i].vy -= dy * force;
                nodes[j].vx += dx * force; nodes[j].vy += dy * force;
              }
            }
            edges.forEach(edge => {
              const a = byId[edge.source], b = byId[edge.target];
              let dx = b.x - a.x, dy = b.y - a.y;
              const dist = Math.max(1, Math.hypot(dx, dy));
              const force = (dist - 140) * 0.02;
              dx /= dist; dy /= dist;
              a.vx += dx * force; a.vy += dy * force;
              b.vx -= dx * force; b.vy -= dy * force;
            });
            nodes.forEach(node => {
              node.x += Math.max(-12, Math.min(12, node.vx));
              node.y += Math.max(-12, Math.min(12, node.vy));
              node.vx *= 0.6; node.vy *= 0.6;
            });
          }
          const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
          world.appendChild(svg);
          const lines = edges.map(edge => {
            const line = document.createElementNS("http://www.w3.org/2000/svg", "line");
            line.setAttribute("stroke", "#c8c2b8");
            line.setAttribute("stroke-width", "1.2");
            svg.appendChild(line);
            return line;
          });
          const elements = nodes.map(node => {
            const el = document.createElement("div");
            el.className = "node";
            el.style.background = colors[node.type] || "#666";
            el.title = node.name;
            const label = document.createElement("span");
            label.textContent = node.name;
            el.appendChild(label);
            el.addEventListener("pointerdown", event => {
              event.stopPropagation();
              dragging = node;
              event.currentTarget.setPointerCapture(event.pointerId);
            });
            el.addEventListener("click", event => {
              event.stopPropagation();
              select(node);
            });
            world.appendChild(el);
            return el;
          });
          let scale = 1, panX = 0, panY = 0, dragging = null, panning = null;
          function render() {
            world.style.transform = `translate(${panX}px, ${panY}px) scale(${scale})`;
            nodes.forEach((node, index) => {
              elements[index].style.left = node.x + "px";
              elements[index].style.top = node.y + "px";
            });
            edges.forEach((edge, index) => {
              const a = byId[edge.source], b = byId[edge.target];
              lines[index].setAttribute("x1", a.x);
              lines[index].setAttribute("y1", a.y);
              lines[index].setAttribute("x2", b.x);
              lines[index].setAttribute("y2", b.y);
            });
          }
          function select(node) {
            const related = edges.filter(edge => edge.source === node.id || edge.target === node.id);
            const relations = related.map(edge => {
              const other = edge.source === node.id ? edge.targetName : edge.sourceName;
              const arrow = edge.source === node.id ? `${node.name} —${edge.description || "相关"}→ ${other}` : `${other} —${edge.description || "相关"}→ ${node.name}`;
              return `<p class="rel">${escapeHtml(arrow)}</p>`;
            }).join("") || "<p class='type'>没有相连关系。</p>";
            const evidence = (node.evidence || []).map(item => {
              const page = item.page == null ? "" : ` · 第 ${item.page} 页`;
              return `<div class="evidence"><strong>${escapeHtml(item.fileName || "")}</strong><span class="type">${escapeHtml(item.title || "")}${page}</span><pre>${escapeHtml(item.content || "")}</pre></div>`;
            }).join("");
            side.innerHTML = `<h2>${escapeHtml(node.name)}</h2><div class="type">${escapeHtml(labels[node.type] || node.type)}</div><p>${escapeHtml(node.description || "")}</p>${relations}${evidence}`;
          }
          function escapeHtml(value) {
            return String(value).replace(/[&<>"']/g, ch => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[ch]));
          }
          stage.addEventListener("pointerdown", event => {
            if (event.target !== stage && event.target !== world) return;
            panning = { x: event.clientX - panX, y: event.clientY - panY };
          });
          window.addEventListener("pointermove", event => {
            if (dragging) {
              const rect = world.getBoundingClientRect();
              dragging.x = (event.clientX - rect.left) / scale;
              dragging.y = (event.clientY - rect.top) / scale;
              render();
            } else if (panning) {
              panX = event.clientX - panning.x;
              panY = event.clientY - panning.y;
              render();
            }
          });
          window.addEventListener("pointerup", () => { dragging = null; panning = null; });
          stage.addEventListener("wheel", event => {
            event.preventDefault();
            const next = Math.min(2.5, Math.max(0.35, scale * (event.deltaY < 0 ? 1.08 : 0.92)));
            const rect = stage.getBoundingClientRect();
            const px = event.clientX - rect.left;
            const py = event.clientY - rect.top;
            panX = px - (px - panX) * (next / scale);
            panY = py - (py - panY) * (next / scale);
            scale = next;
            render();
          }, { passive: false });
          document.getElementById("search").addEventListener("input", event => {
            const query = event.target.value.trim().toLowerCase();
            let first = null;
            nodes.forEach((node, index) => {
              const match = !query || node.name.toLowerCase().includes(query);
              elements[index].classList.toggle("match", Boolean(query) && match);
              elements[index].classList.toggle("dim", Boolean(query) && !match);
              if (match && !first) first = node;
            });
            if (first && query) {
              panX = stage.clientWidth / 2 - first.x * scale;
              panY = stage.clientHeight / 2 - first.y * scale;
              select(first);
              render();
            }
          });
          render();
        }
        </script>
        </body>
        </html>
        """;
}
