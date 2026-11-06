using System.Net;
using System.Text;
using System.Text.Json;
using Athlon.Agent.App.Themes;
using Athlon.Agent.Core.Teams;

namespace Athlon.Agent.App.Services;

public static class TeamTraceHtml
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string BuildPayloadJson(
        TeamTask task,
        IReadOnlyList<TeamTraceEvent> events,
        string? teamName = null,
        IReadOnlyDictionary<string, IReadOnlyList<object>>? transcripts = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            taskId = task.TaskId,
            teamId = task.TeamId,
            teamName = string.IsNullOrWhiteSpace(teamName) ? task.TeamId : teamName,
            goal = task.Goal,
            status = task.Status.ToString(),
            createdAt = task.CreatedAt,
            updatedAt = task.UpdatedAt,
            completedAt = task.CompletedAt,
            finalResult = task.FinalResult,
            error = task.Error,
            leaderProfileId = task.LeaderProfileId,
            leaderSessionId = task.LeaderSessionId,
            layout = task.TraceLayout.ToDictionary(
                kv => kv.Key,
                kv => new { x = kv.Value.X, y = kv.Value.Y },
                StringComparer.OrdinalIgnoreCase),
            subtasks = task.SubTasks.Select(s => new
            {
                id = s.Id,
                title = s.Title,
                status = s.Status.ToString(),
                profileId = s.AssigneeProfileId,
                memberSessionId = s.MemberSessionId,
                iteration = s.Iteration,
                maxIterations = s.MaxIterations,
                result = s.Result,
                handoff = s.Handoff,
                reason = s.RedispatchReason,
                error = s.Error,
                prevResultSummary = s.PrevResultSummary,
                dependsOn = s.DependsOn,
                startedAt = s.StartedAt,
                completedAt = s.CompletedAt
            }),
            events = events.Select(e => new
            {
                id = e.Id,
                type = e.Type,
                at = e.At,
                nodeId = e.NodeId,
                fromNodeId = e.FromNodeId,
                toNodeId = e.ToNodeId,
                profileId = e.ProfileId,
                subTaskId = e.SubTaskId,
                iteration = e.Iteration,
                message = e.Message
            }),
            transcripts = transcripts ?? new Dictionary<string, IReadOnlyList<object>>()
        }, JsonOptions);

        return payload.Replace("</", "<\\/", StringComparison.Ordinal);
    }

    public static string CurrentThemeAttribute() =>
        AppThemeManager.CurrentKind == AppThemeKind.Dark ? "dark" : "light";

    public static string Build(
        TeamTask task,
        IReadOnlyList<TeamTraceEvent> events,
        string? teamName = null,
        IReadOnlyDictionary<string, IReadOnlyList<object>>? transcripts = null)
    {
        var safe = BuildPayloadJson(task, events, teamName, transcripts);
        var title = WebUtility.HtmlEncode($"Team Trace · {(string.IsNullOrWhiteSpace(teamName) ? task.TeamId : teamName)}");
        var goal = WebUtility.HtmlEncode(task.Goal ?? "");
        var theme = CurrentThemeAttribute();

        var sb = new StringBuilder(48_000);
        sb.Append("""
<!doctype html>
<html lang="zh-CN" data-theme="@@THEME@@">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1"/>
<title>@@TITLE@@</title>
<style>
:root, [data-theme="light"] {
  --bg:#F1F5F9; --canvas:#EEF2FF; --shell:#FFFFFF; --panel:#FFFFFF; --fg:#0F172A; --muted:#64748B;
  --border:#E2E8F0; --border-strong:#CBD5E1; --accent:#6366F1; --accent-hover:#4F46E5;
  --accent-soft:rgba(99,102,241,.14); --accent-ring:rgba(99,102,241,.35);
  --success:#059669; --success-soft:#ECFDF5; --success-border:#6EE7B7;
  --running:#2563EB; --running-soft:#EFF6FF; --running-border:#93C5FD;
  --waiting:#94A3B8; --waiting-soft:#F8FAFC;
  --error:#DC2626; --error-soft:#FEF2F2; --error-border:#FCA5A5;
  --loop:#D97706; --loop-soft:#FFFBEB; --loop-border:#FCD34D; --leader:#7C3AED;
  --dot:rgba(99,102,241,.18);
  --shadow:0 1px 2px rgba(15,23,42,.05), 0 10px 28px rgba(15,23,42,.08);
  --font:"Segoe UI Variable Text","Segoe UI","Microsoft YaHei UI","PingFang SC",system-ui,sans-serif;
  --mono:ui-monospace,"Cascadia Mono","SF Mono",Consolas,monospace;
}
[data-theme="dark"] {
  --bg:#0B1220; --canvas:#111827; --shell:#0F172A; --panel:#1E293B; --fg:#F1F5F9; --muted:#94A3B8;
  --border:#334155; --border-strong:#475569; --accent:#818CF8; --accent-hover:#A5B4FC;
  --accent-soft:rgba(129,140,248,.16); --accent-ring:rgba(129,140,248,.4);
  --success:#34D399; --success-soft:rgba(52,211,153,.12); --success-border:rgba(52,211,153,.45);
  --running:#60A5FA; --running-soft:rgba(96,165,250,.12); --running-border:rgba(96,165,250,.45);
  --waiting:#64748B; --waiting-soft:rgba(100,116,139,.12);
  --error:#F87171; --error-soft:rgba(248,113,113,.12); --error-border:rgba(248,113,113,.45);
  --loop:#FBBF24; --loop-soft:rgba(251,191,36,.12); --loop-border:rgba(251,191,36,.45); --leader:#A78BFA;
  --dot:rgba(129,140,248,.12);
  --shadow:0 1px 2px rgba(0,0,0,.4), 0 14px 36px rgba(0,0,0,.35);
}
*{box-sizing:border-box} html,body{height:100%;margin:0}
body{font-family:var(--font);font-size:13px;color:var(--fg);background:var(--bg);line-height:1.45;overflow:hidden}
button{font:inherit;color:inherit;cursor:pointer}
.app{display:grid;grid-template-rows:48px 36px 1fr;height:100%;background:var(--shell)}
.topbar{background:var(--shell);border-bottom:1px solid var(--border);display:flex;align-items:center;justify-content:space-between;padding:0 12px 0 14px;gap:12px}
.title{font-weight:700;font-size:13.5px}.subtitle{color:var(--muted);font-size:11.5px;max-width:640px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.actions{display:flex;gap:8px;align-items:center}
.btn{border:1px solid var(--border);background:var(--panel);border-radius:8px;padding:5px 10px;font-weight:550}
.btn.primary{background:var(--accent);border-color:var(--accent);color:#fff}
.btn.primary:hover{background:var(--accent-hover)}
.statusbar{background:var(--shell);border-bottom:1px solid var(--border);display:flex;align-items:center;gap:14px;padding:0 16px;color:var(--muted);font-size:12px;font-family:var(--mono)}
.pill{display:inline-flex;align-items:center;gap:6px;padding:2px 8px;border-radius:999px;font-family:var(--font);font-weight:650;font-size:11px}
.pill.success{background:var(--success-soft);color:var(--success)}
.pill.running{background:var(--running-soft);color:var(--running)}
.pill.waiting{background:var(--waiting-soft);color:var(--waiting)}
.pill.error{background:var(--error-soft);color:var(--error)}
.pill.loop{background:var(--loop-soft);color:var(--loop)}
.dot{width:7px;height:7px;border-radius:50%;background:currentColor}
.pill.running .dot{animation:pulse 1.1s ease-in-out infinite}
@keyframes pulse{0%,100%{opacity:1}50%{opacity:.4}}
.workspace{display:grid;grid-template-columns:1fr 380px;min-height:0}
.workspace.drawer-closed{grid-template-columns:1fr 0fr}
.workspace.drawer-closed .drawer{display:none}
.canvas-wrap{position:relative;overflow:hidden;background:radial-gradient(circle at 1px 1px,var(--dot) 1px,transparent 0) 0 0/22px 22px,linear-gradient(180deg,color-mix(in srgb,var(--canvas) 70%,transparent),var(--bg))}
.canvas-toolbar{position:absolute;top:12px;left:12px;z-index:5;display:flex;gap:6px;flex-wrap:wrap;background:var(--shell);border:1px solid var(--border);border-radius:10px;padding:6px;box-shadow:var(--shadow);max-width:calc(100% - 180px)}
.canvas-toolbar .btn{padding:5px 9px}
.hint-chip{align-self:center;font-size:11px;color:var(--muted);padding:0 6px}
.legend{position:absolute;right:12px;top:12px;z-index:5;background:var(--shell);border:1px solid var(--border);border-radius:10px;padding:10px 12px;box-shadow:var(--shadow);font-size:11px;color:var(--muted);display:grid;gap:6px}
.legend span{display:flex;align-items:center;gap:8px}
.swatch{width:12px;height:12px;border-radius:3px;border:2px solid}
.swatch.success{border-color:var(--success);background:var(--success-soft)}
.swatch.running{border-color:var(--running);background:var(--running-soft)}
.swatch.waiting{border-color:var(--waiting);background:var(--waiting-soft)}
.swatch.error{border-color:var(--error);background:var(--error-soft)}
.swatch.loop{border-color:var(--loop);background:var(--loop-soft)}
.viewport{position:absolute;inset:0;overflow:auto;cursor:grab}
.world{position:relative;width:1600px;height:1100px;transform-origin:0 0}
svg.wires{position:absolute;inset:0;width:100%;height:100%;overflow:visible;pointer-events:none}
.wire{fill:none;stroke:var(--border-strong);stroke-width:2;marker-end:url(#arrow)}
.wire.done,.wire.success,.wire.ok{stroke:var(--success);marker-end:url(#arrowDone)}
.wire.active,.wire.running,.wire.run{stroke:var(--accent);stroke-dasharray:6 6;animation:dash .8s linear infinite;marker-end:url(#arrowActive)}
.wire.loop{stroke:var(--loop);stroke-width:2.5;stroke-dasharray:2 6;marker-end:url(#arrowLoop)}
.wire.loop.active{animation:dash .7s linear infinite}
@keyframes dash{to{stroke-dashoffset:-12}}
.edge-label{position:absolute;transform:translate(-50%,-50%);font-size:10px;font-weight:700;padding:2px 7px;border-radius:999px;background:var(--shell);border:1px solid var(--border);color:var(--muted);pointer-events:none;white-space:nowrap;z-index:2}
.edge-label.loop{color:var(--loop);border-color:var(--loop-border);background:var(--loop-soft)}
.node{position:absolute;width:220px;background:var(--panel);border:2px solid var(--border);border-radius:12px;box-shadow:var(--shadow);cursor:grab;user-select:none;z-index:3;touch-action:none}
.node.dragging{cursor:grabbing;z-index:10;opacity:.96}
.node.selected{box-shadow:0 0 0 3px var(--accent-ring),var(--shadow);border-color:var(--accent)}
.node.success{border-color:var(--success-border);background:color-mix(in srgb,var(--success-soft) 70%,var(--panel))}
.node.running{border-color:var(--running-border);background:color-mix(in srgb,var(--running-soft) 70%,var(--panel))}
.node.waiting{border-color:var(--border)}
.node.error{border-color:var(--error-border);background:color-mix(in srgb,var(--error-soft) 70%,var(--panel))}
.node.looping{border-color:var(--loop-border);background:color-mix(in srgb,var(--loop-soft) 65%,var(--panel))}
.node-h{display:flex;align-items:center;justify-content:space-between;padding:10px 12px 6px;gap:8px}
.node-kind{font-size:10px;font-weight:700;letter-spacing:.04em;text-transform:uppercase;color:var(--muted)}
.node-kind.leader{color:var(--leader)}
.node-b{padding:0 12px 12px}
.node-name{font-weight:700;font-size:13px;margin-bottom:4px}
.node-meta{color:var(--muted);font-size:11px;font-family:var(--mono)}
.node-foot{margin-top:8px;display:flex;justify-content:space-between;align-items:center;font-size:11px;color:var(--muted)}
.progress{height:3px;border-radius:999px;background:var(--border);margin-top:8px;overflow:hidden}
.progress>i{display:block;height:100%;width:0;background:var(--running)}
.node.running .progress>i,.node.looping .progress>i{width:62%;animation:grow 2s ease-in-out infinite alternate}
.node.looping .progress>i{background:var(--loop)}
.node.success .progress>i{width:100%;background:var(--success);animation:none}
@keyframes grow{from{width:35%}to{width:78%}}
.note{position:absolute;left:12px;bottom:12px;z-index:5;background:var(--shell);border:1px solid var(--border);border-radius:10px;padding:10px 12px;box-shadow:var(--shadow);max-width:460px;font-size:12px;color:var(--muted)}
.drawer{background:var(--shell);border-left:1px solid var(--border);display:flex;flex-direction:column;min-width:0;min-height:0}
.drawer-h{padding:12px 14px;border-bottom:1px solid var(--border);display:flex;align-items:flex-start;justify-content:space-between;gap:8px}
.drawer-h h2{margin:0;font-size:14px}.drawer-h .meta{color:var(--muted);font-size:12px;margin-top:4px}
.drawer-tabs{display:flex;gap:2px;padding:8px 10px;border-bottom:1px solid var(--border);background:var(--bg)}
.drawer-tab{border:0;background:transparent;border-radius:7px;padding:7px 10px;color:var(--muted);font-weight:600}
.drawer-tab.active{background:var(--panel);color:var(--fg);box-shadow:var(--shadow)}
.drawer-b{flex:1;overflow:auto;padding:12px 14px}
.kv{display:grid;grid-template-columns:96px 1fr;gap:8px 10px;margin-bottom:14px;font-size:12px}
.kv dt{color:var(--muted)}.kv dd{margin:0;font-family:var(--mono);word-break:break-word}
.block{border:1px solid var(--border);border-radius:10px;margin-bottom:10px;overflow:hidden;background:var(--panel)}
.block summary{list-style:none;cursor:pointer;padding:9px 11px;display:flex;justify-content:space-between;align-items:center;font-weight:650;font-size:12px;background:var(--bg)}
.block summary::-webkit-details-marker{display:none}
.block pre{margin:0;padding:10px 12px;font-family:var(--mono);font-size:11.5px;white-space:pre-wrap;word-break:break-word;max-height:220px;overflow:auto;border-top:1px solid var(--border);background:color-mix(in srgb,var(--bg) 70%,var(--panel))}
.timeline{display:flex;flex-direction:column}
.step{display:grid;grid-template-columns:16px 1fr;gap:10px;padding:8px 0;position:relative}
.step::before{content:"";position:absolute;left:7px;top:22px;bottom:-2px;width:2px;background:var(--border)}
.step:last-child::before{display:none}
.step-dot{width:14px;height:14px;border-radius:50%;border:2px solid var(--accent);background:var(--panel);margin-top:2px}
.step.ok .step-dot{border-color:var(--success);background:var(--success)}
.step.run .step-dot{border-color:var(--running);background:var(--running)}
.step.loop .step-dot{border-color:var(--loop);background:var(--loop)}
.step-body .when{font-family:var(--mono);font-size:11px;color:var(--muted)}
.step-body .what{font-weight:650;margin-top:2px}
.step-body .detail{color:var(--muted);font-size:12px;margin-top:2px}
</style>
</head>
<body>
<div class="app" role="dialog" aria-label="Team Trace">
  <header class="topbar">
    <div>
      <div class="title" id="title">@@TITLE@@</div>
      <div class="subtitle" id="subtitle">Goal: @@GOAL@@</div>
    </div>
    <div class="actions">
      <button class="btn" id="resetLayout" type="button">重置布局</button>
      <button class="btn" id="toggleDrawer" type="button">收起详情</button>
      <button class="btn primary" id="closeBtn" type="button">关闭</button>
    </div>
  </header>
  <div class="statusbar" id="statusbar"></div>
  <div class="workspace" id="workspace">
    <section class="canvas-wrap">
      <div class="canvas-toolbar">
        <button class="btn" id="zoomOut" type="button">−</button>
        <button class="btn" id="zoomLabel" type="button">100%</button>
        <button class="btn" id="zoomIn" type="button">＋</button>
        <button class="btn" id="fitView" type="button">适应</button>
        <span class="hint-chip">拖节点改布局 · 橙虚线 = 循环重派</span>
      </div>
      <div class="legend">
        <span><i class="swatch success"></i> Success</span>
        <span><i class="swatch running"></i> Running</span>
        <span><i class="swatch loop"></i> Loop / re-dispatch</span>
        <span><i class="swatch waiting"></i> Waiting</span>
        <span><i class="swatch error"></i> Error</span>
      </div>
      <div class="viewport" id="viewport">
        <div class="world" id="world">
          <svg class="wires" id="wires" aria-hidden="true">
            <defs>
              <marker id="arrow" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto"><path d="M0,0 L6,3 L0,6 Z" fill="#94A3B8"/></marker>
              <marker id="arrowDone" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto"><path d="M0,0 L6,3 L0,6 Z" fill="#059669"/></marker>
              <marker id="arrowActive" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto"><path d="M0,0 L6,3 L0,6 Z" fill="#6366F1"/></marker>
              <marker id="arrowLoop" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto"><path d="M0,0 L6,3 L0,6 Z" fill="#D97706"/></marker>
            </defs>
          </svg>
          <div id="edgeLabels"></div>
          <div id="nodes"></div>
        </div>
      </div>
    </section>
    <aside class="drawer" id="drawer">
      <div class="drawer-h">
        <div>
          <h2 id="dTitle">Select a node</h2>
          <div class="meta" id="dMeta"></div>
        </div>
        <span class="pill waiting" id="dStatus"><i class="dot"></i> Waiting</span>
      </div>
      <div class="drawer-tabs">
        <button class="drawer-tab active" data-tab="io" type="button">I/O</button>
        <button class="drawer-tab" data-tab="tools" type="button">Tools</button>
        <button class="drawer-tab" data-tab="timeline" type="button">Timeline</button>
        <button class="drawer-tab" data-tab="loop" type="button">Loop</button>
      </div>
      <div class="drawer-b">
        <div class="pane" data-pane="io">
          <dl class="kv">
            <dt>profile</dt><dd id="dProfile">—</dd>
            <dt>iteration</dt><dd id="dIter">—</dd>
            <dt>reason</dt><dd id="dReason">—</dd>
            <dt>status</dt><dd id="dNodeStatus">—</dd>
            <dt>depends</dt><dd id="dDepends">—</dd>
          </dl>
          <details class="block" open><summary><span>Input · Handoff</span></summary><pre id="dInput"></pre></details>
          <details class="block" open><summary><span>Output · Result</span></summary><pre id="dOutput"></pre></details>
        </div>
        <div class="pane" data-pane="tools" hidden>
          <div id="dTools"></div>
        </div>
        <div class="pane" data-pane="timeline" hidden><div class="timeline" id="dTimeline"></div></div>
        <div class="pane" data-pane="loop" hidden>
          <dl class="kv">
            <dt>policy</dt><dd>leader_controlled</dd>
            <dt>max</dt><dd id="dLoopMax">—</dd>
            <dt>current</dt><dd id="dLoopCur">—</dd>
            <dt>break_on</dt><dd>leader.accept | max_reached | worker.error</dd>
            <dt>context</dt><dd>每轮新 Turn；注入 prev_result_summary</dd>
          </dl>
          <details class="block" open><summary>循环安全约束</summary><pre>1. 同一 Worker 并发实例 ≤ 1
2. 达到 MaxIterations 后禁止再 dispatch
3. 每轮 handoff 必须带 iteration + reason
4. Trace 必须画出 loop 边</pre></details>
          <details class="block" open><summary>Prev result summary</summary><pre id="dPrev"></pre></details>
        </div>
      </div>
    </aside>
  </div>
</div>
<script>
let data = @@SAFE@@;
const NODE_W=220, NODE_H=118;
let scale=1, selectedId=null;
const nodesEl=document.getElementById('nodes');
const wiresEl=document.getElementById('wires');
const labelsEl=document.getElementById('edgeLabels');
const worldEl=document.getElementById('world');
const viewport=document.getElementById('viewport');

function esc(s){return String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));}
function statusUi(raw){
  const s=String(raw||'').toLowerCase();
  if(s.includes('success')||s==='succeeded') return {cls:'success', text:'Success'};
  if(s.includes('fail')||s.includes('error')||s==='cancelled') return {cls:'error', text: raw||'Error'};
  if(s.includes('queue')) return {cls:'waiting', text:'Queued'};
  if(s.includes('run')) return {cls:'running', text:'Running'};
  if(s.includes('wait')||s.includes('ready')||s.includes('pending')||s.includes('await')) return {cls:'waiting', text: raw||'Waiting'};
  return {cls:'waiting', text: raw||'Waiting'};
}
function nodeStatus(sub){
  if(!sub) return statusUi(data.status);
  if((sub.iteration||1)>1 && /run|wait|ready/i.test(sub.status||'')) return {cls:'looping', text:`Loop ${sub.iteration}/${sub.maxIterations||1}`, pill:'loop'};
  if((sub.iteration||1)>1 && /success/i.test(sub.status||'')) return {cls:'success', text:`Done ${sub.iteration}/${sub.maxIterations||1}`, pill:'success'};
  const u=statusUi(sub.status); return {...u, pill:u.cls};
}
function elapsed(){
  const start=new Date(data.createdAt||Date.now());
  const end=data.completedAt?new Date(data.completedAt):new Date(data.updatedAt||Date.now());
  const sec=Math.max(0,Math.round((end-start)/1000));
  const m=String(Math.floor(sec/60)).padStart(2,'0');
  const s=String(sec%60).padStart(2,'0');
  return m+':'+s;
}
function loopInfo(){
  const looping=(data.subtasks||[]).filter(s=>(s.iteration||1)>1);
  if(!looping.length) return null;
  const s=looping.sort((a,b)=>(b.iteration||0)-(a.iteration||0))[0];
  return `${s.title||s.id} loop ${s.iteration}/${s.maxIterations||1}`;
}

function renderStatus(){
  document.getElementById('title').textContent='Team Trace · '+(data.teamName||data.teamId||data.taskId);
  document.getElementById('subtitle').textContent='Goal: '+(data.goal||'')+' · 实时 · 可拖拽 · Worker 循环';
  const st=statusUi(data.status);
  const loop=loopInfo();
  document.getElementById('statusbar').innerHTML=
    `<span class="pill ${st.cls}"><i class="dot"></i> ${esc(st.text)}</span>`+
    (loop?`<span class="pill loop"><i class="dot"></i> ${esc(loop)}</span>`:'')+
    `<span>task_id · ${esc(data.taskId)}</span>`+
    `<span>elapsed · ${elapsed()}</span>`+
    `<span>workers · ${(data.subtasks||[]).length}</span>`+
    `<span>拖拽节点排版 · 布局回写宿主</span>`;
}

const defaultLayout={leader:{x:560,y:80}, agg:{x:560,y:620}, submit:{x:560,y:860}};
(data.subtasks||[]).forEach((s,i)=>{ defaultLayout[s.id]={x:180+i*380,y:320}; });
const layout=Object.assign({}, defaultLayout, data.layout||{});
let isDragging=false;
let drawerScrollTop=0;
const openToolKeys=new Set();
renderStatus();

function captureDrawerUi(){
  const body=document.querySelector('.drawer-b');
  if(body) drawerScrollTop=body.scrollTop;
  openToolKeys.clear();
  document.querySelectorAll('#dTools details.block').forEach((el,i)=>{
    const key=el.querySelector('summary span')?.textContent||String(i);
    if(el.open) openToolKeys.add(key);
  });
}
function restoreDrawerUi(){
  const body=document.querySelector('.drawer-b');
  if(body) body.scrollTop=drawerScrollTop;
  document.querySelectorAll('#dTools details.block').forEach((el,i)=>{
    const key=el.querySelector('summary span')?.textContent||String(i);
    if(openToolKeys.size) el.open=openToolKeys.has(key);
  });
}
function fingerprintPayload(d){
  const subs=(d.subtasks||[]).map(s=>s.id+':'+s.status+':'+s.iteration+(s.result||'').length).join('|');
  const ev=(d.events||[]).length;
  const tr=Object.keys(d.transcripts||{}).map(k=>{
    const arr=d.transcripts[k]||[];
    return k+':'+arr.length+':'+(arr[arr.length-1]?.at||'');
  }).join(';');
  return [d.taskId,d.status,d.updatedAt,d.finalResult||'',subs,ev,tr].join('~');
}
let lastPayloadFp=fingerprintPayload(data);

window.applyTraceUpdate=function(next){
  if(!next) return;
  if(isDragging) return;
  const fp=fingerprintPayload(next);
  if(fp===lastPayloadFp) return;
  lastPayloadFp=fp;
  const keepSelection=selectedId;
  const drawerClosed=document.getElementById('workspace')?.classList.contains('drawer-closed');
  const activeTab=document.querySelector('.drawer-tab.active')?.dataset?.tab||'io';
  captureDrawerUi();
  data=next;
  (data.subtasks||[]).forEach((s,i)=>{ if(!layout[s.id]) layout[s.id]={x:180+i*380,y:320}; });
  if(next.layout){ Object.keys(next.layout).forEach(k=>{ if(!layout[k]) layout[k]=next.layout[k]; }); }
  renderStatus();
  renderNodes();
  if(keepSelection) selectNode(keepSelection, {preserveUi:true});
  if(drawerClosed){
    document.getElementById('workspace').classList.add('drawer-closed');
    document.getElementById('toggleDrawer').textContent='展开详情';
  }
  document.querySelectorAll('.drawer-tab').forEach(tab=>{
    const on=tab.dataset.tab===activeTab;
    tab.classList.toggle('active', on);
  });
  document.querySelectorAll('.pane').forEach(p=>{ p.hidden=p.dataset.pane!==activeTab; });
  restoreDrawerUi();
};

function port(id,side){
  const p=layout[id]||{x:0,y:0}; const cx=p.x+NODE_W/2, cy=p.y+NODE_H/2;
  if(side==='bottom') return {x:cx,y:p.y+NODE_H};
  if(side==='top') return {x:cx,y:p.y};
  if(side==='right') return {x:p.x+NODE_W,y:cy};
  if(side==='left') return {x:p.x,y:cy};
  return {x:cx,y:cy};
}
function curve(a,b,loop){
  if(loop){ const midX=Math.max(a.x,b.x)+120; return `M ${a.x} ${a.y} C ${midX} ${a.y}, ${midX} ${b.y}, ${b.x} ${b.y}`; }
  const dy=Math.max(40, Math.abs(b.y-a.y)*.45);
  return `M ${a.x} ${a.y} C ${a.x} ${a.y+dy}, ${b.x} ${b.y-dy}, ${b.x} ${b.y}`;
}
function buildEdges(){
  const edges=[];
  (data.subtasks||[]).forEach(s=>{
    const u=nodeStatus(s);
    const wire=u.cls==='success'?'done':(u.cls==='running'||u.cls==='looping'?'active':'');
    edges.push({from:'leader', to:s.id, type:wire, label:(s.iteration||1)>1?`dispatch #${s.iteration}`:'dispatch', loop:false});
    if((s.iteration||1)>1){
      edges.push({from:'leader', to:s.id, type:'loop'+( /run/i.test(s.status||'')?' active':''), label:`loop iter ${s.iteration}/${s.maxIterations||1}`, loop:true});
    }
    edges.push({from:s.id, to:'agg', type:wire, label:u.cls==='success'?'result':'partial', loop:false});
  });
  edges.push({from:'agg', to:'submit', type:statusUi(data.status).cls==='success'?'done':'', label:'final', loop:false});
  (data.events||[]).forEach(ev=>{
    const t=(ev.type||'').toLowerCase();
    if(t.includes('loop') || t.includes('redispatch')){
      const to=ev.toNodeId||ev.subTaskId||ev.nodeId;
      if(to && layout[to]) edges.push({from:'leader', to, type:'loop active', label:ev.message||'loop', loop:true});
    }
  });
  return edges;
}
function redrawWires(){
  const defs=wiresEl.querySelector('defs');
  wiresEl.innerHTML=''; if(defs) wiresEl.appendChild(defs);
  labelsEl.innerHTML='';
  buildEdges().forEach(e=>{
    if(!layout[e.from]||!layout[e.to]) return;
    const a=port(e.from, e.loop?'right':'bottom');
    const b=port(e.to, e.loop?'right':'top');
    const path=document.createElementNS('http://www.w3.org/2000/svg','path');
    path.setAttribute('d', curve(a,b,e.loop));
    path.setAttribute('class', ('wire '+(e.type||'')).trim());
    wiresEl.appendChild(path);
    if(e.label){
      const label=document.createElement('div');
      label.className='edge-label'+(e.loop?' loop':'');
      if(e.loop){ label.style.left=(Math.max(a.x,b.x)+120)+'px'; label.style.top=((a.y+b.y)/2)+'px'; }
      else { label.style.left=((a.x+b.x)/2)+'px'; label.style.top=((a.y+b.y)/2)+'px'; }
      label.textContent=e.label; labelsEl.appendChild(label);
    }
  });
}
function postLayout(){ try{ chrome.webview.postMessage({type:'traceLayout', layout}); }catch(err){} }
function enableDrag(el){
  let sx=0,sy=0,ox=0,oy=0,dragging=false;
  el.addEventListener('pointerdown',ev=>{
    if(ev.button!==0) return; dragging=true; isDragging=true; el.dataset.didDrag='0'; el.classList.add('dragging');
    el.setPointerCapture(ev.pointerId); sx=ev.clientX; sy=ev.clientY;
    const id=el.dataset.id; ox=layout[id].x; oy=layout[id].y; ev.stopPropagation();
  });
  el.addEventListener('pointermove',ev=>{
    if(!dragging) return;
    const dx=(ev.clientX-sx)/scale, dy=(ev.clientY-sy)/scale;
    if(Math.abs(dx)+Math.abs(dy)>3) el.dataset.didDrag='1';
    const id=el.dataset.id; layout[id]={x:Math.max(16,ox+dx), y:Math.max(16,oy+dy)};
    el.style.left=layout[id].x+'px'; el.style.top=layout[id].y+'px'; redrawWires();
  });
  el.addEventListener('pointerup',()=>{ if(!dragging) return; dragging=false; isDragging=false; el.classList.remove('dragging'); postLayout(); });
}
function fmtTime(v){ try{ return new Date(v).toLocaleTimeString(); }catch{ return String(v||''); } }
function relatedEvents(id){
  return (data.events||[]).filter(e=>{
    const ids=[e.nodeId,e.fromNodeId,e.toNodeId,e.subTaskId];
    return id==='leader' || id==='agg' || id==='submit' || ids.includes(id);
  });
}
function nodeTranscript(id){
  const map=data.transcripts||{};
  return map[id]||[];
}
function pillForToolStatus(status){
  const s=String(status||'').toLowerCase();
  if(s.includes('fail')) return {cls:'error', text:'failed'};
  if(s.includes('run')) return {cls:'running', text:'running'};
  if(s.includes('success')||s==='ok'||s==='succeeded') return {cls:'success', text:'ok'};
  return {cls:'waiting', text:status||'tool'};
}
function renderTools(id){
  const host=document.getElementById('dTools'); host.innerHTML='';
  const items=nodeTranscript(id);
  if(!items.length){
    host.innerHTML='<div class="meta" style="color:var(--muted);padding:8px 2px">暂无模型回复或工具调用。成员开始运行后会实时出现在这里。</div>';
    return;
  }
  items.forEach((item, idx)=>{
    const kind=String(item.kind||'');
    const details=document.createElement('details');
    details.className='block';
    details.open = idx >= items.length-6;
    if(kind==='assistant'){
      const body=[item.reasoning?`Reasoning:\n${item.reasoning}`:'', item.text||''].filter(Boolean).join('\n\n')||'(empty)';
      details.innerHTML=`<summary><span>Model</span><span class="pill running">assistant</span></summary><pre>${esc(body)}</pre>`;
    } else if(kind==='user'){
      details.innerHTML=`<summary><span>Handoff / User</span><span class="pill waiting">user</span></summary><pre>${esc(item.text||'')}</pre>`;
    } else if(kind==='tool_call' || kind==='tool'){
      const st=pillForToolStatus(item.status||(kind==='tool_call'?'running':'succeeded'));
      const name=item.toolName||'tool';
      const parts=[];
      if(item.arguments) parts.push('Arguments:\n'+item.arguments);
      if(item.summary) parts.push('Summary:\n'+item.summary);
      if(item.detail) parts.push('Result:\n'+item.detail);
      if(!parts.length && item.text) parts.push(item.text);
      details.innerHTML=`<summary><span>${esc(name)}</span><span class="pill ${st.cls}">${esc(st.text)}</span></summary><pre>${esc(parts.join('\n\n')||'(no detail)')}</pre>`;
    } else {
      details.innerHTML=`<summary><span>${esc(kind||'event')}</span></summary><pre>${esc(item.text||item.message||JSON.stringify(item))}</pre>`;
    }
    host.appendChild(details);
  });
}
function renderTimeline(id){
  const host=document.getElementById('dTimeline'); host.innerHTML='';
  const steps=[];
  relatedEvents(id).forEach(ev=>{
    const t=(ev.type||'').toLowerCase();
    const cls=t.includes('loop')||t.includes('redispatch')?'loop':(t.includes('fail')||t.includes('error')?'run':(t.includes('complete')||t.includes('success')?'ok':'run'));
    steps.push({at:ev.at, cls, what:ev.type||'', detail:ev.message||''});
  });
  nodeTranscript(id).forEach(item=>{
    const kind=String(item.kind||'');
    if(kind==='assistant'){
      const preview=(item.text||item.reasoning||'').slice(0,160);
      steps.push({at:item.at, cls:'run', what:'model.response', detail:preview});
    } else if(kind==='tool_call'){
      steps.push({at:item.at, cls:'run', what:`tool.call · ${item.toolName||''}`, detail:(item.arguments||'').slice(0,160)});
    } else if(kind==='tool'){
      const failed=/fail/i.test(item.status||'');
      steps.push({at:item.at, cls:failed?'run':'ok', what:`tool.result · ${item.toolName||''}`, detail:(item.summary||item.detail||'').slice(0,160)});
    } else if(kind==='user'){
      steps.push({at:item.at, cls:'ok', what:'handoff', detail:(item.text||'').slice(0,160)});
    }
  });
  steps.sort((a,b)=>new Date(a.at)-new Date(b.at));
  if(!steps.length){ host.innerHTML='<div class="step"><div class="step-dot"></div><div class="step-body"><div class="what">暂无事件</div></div></div>'; return; }
  steps.forEach(ev=>{
    const el=document.createElement('div'); el.className='step '+ev.cls;
    el.innerHTML=`<div class="step-dot"></div><div class="step-body"><div class="when">${esc(fmtTime(ev.at))}</div><div class="what">${esc(ev.what)}</div><div class="detail">${esc(ev.detail||'')}</div></div>`;
    host.appendChild(el);
  });
}
function setTextIfChanged(el, value){
  const next=value??'';
  if(el && el.textContent!==next) el.textContent=next;
}
function selectNode(id, opts){
  selectedId=id;
  const preserve=!!(opts&&opts.preserveUi);
  document.querySelectorAll('.node').forEach(n=>n.classList.toggle('selected', n.dataset.id===id));
  const sub=(data.subtasks||[]).find(s=>s.id===id);
  let title='Leader', meta='orchestrator', profile=data.leaderProfileId||'leader', iter='—', reason='—', input=data.goal||'', output=data.finalResult||data.error||'', depends='—', prev='—', max='—', cur='—', statusText=statusUi(data.status);
  if(id==='agg'){ title='Aggregate Results'; meta='leader · collect'; input='收集子任务结果'; output=(data.subtasks||[]).filter(s=>/success/i.test(s.status||'')).map(s=>s.title||s.id).join(', ')||'尚未齐套'; }
  else if(id==='submit'){ title='Submit Final'; meta='leader · team_submit_result'; input='提交最终包'; output=data.finalResult||'尚未开始'; }
  else if(sub){
    const u=nodeStatus(sub);
    title=sub.title||sub.id; meta=`member · ${sub.profileId||''} · iter ${sub.iteration}/${sub.maxIterations}`;
    profile=sub.profileId||'—'; iter=`${sub.iteration} / ${sub.maxIterations}`; reason=sub.reason||'—';
    input=sub.handoff||'(none)'; output=sub.result||sub.error||'(none)';
    depends=(sub.dependsOn&&sub.dependsOn.length)?sub.dependsOn.join(', '):'—';
    prev=sub.prevResultSummary||'(none)'; max=String(sub.maxIterations||1); cur=String(sub.iteration||1);
    statusText={cls:u.pill||u.cls, text:u.text};
  }
  // Prefer latest assistant text for output when Result 尚未回写.
  const transcript=nodeTranscript(id);
  if((!output || output==='(none)') && transcript.length){
    for(let i=transcript.length-1;i>=0;i--){
      if(transcript[i].kind==='assistant' && transcript[i].text){ output=transcript[i].text; break; }
    }
  }
  setTextIfChanged(document.getElementById('dTitle'), title);
  setTextIfChanged(document.getElementById('dMeta'), meta);
  setTextIfChanged(document.getElementById('dProfile'), profile);
  setTextIfChanged(document.getElementById('dIter'), iter);
  setTextIfChanged(document.getElementById('dReason'), reason);
  setTextIfChanged(document.getElementById('dNodeStatus'), statusText.text);
  setTextIfChanged(document.getElementById('dDepends'), depends);
  setTextIfChanged(document.getElementById('dInput'), input);
  setTextIfChanged(document.getElementById('dOutput'), output);
  setTextIfChanged(document.getElementById('dPrev'), prev);
  setTextIfChanged(document.getElementById('dLoopMax'), max);
  setTextIfChanged(document.getElementById('dLoopCur'), cur);
  const st=document.getElementById('dStatus'); st.className='pill '+statusText.cls; st.innerHTML=`<i class="dot"></i> ${esc(statusText.text)}`;
  renderTools(id);
  renderTimeline(id);
  if(!preserve){
    document.getElementById('workspace').classList.remove('drawer-closed');
    document.getElementById('toggleDrawer').textContent='收起详情';
  }
}
function renderNodes(){
  nodesEl.innerHTML='';
  const leaderUi=statusUi(data.status);
  const defs=[
    {id:'leader', name:'Leader', kind:'Leader', kindClass:'leader', ui:leaderUi, meta:'plan + dispatch', foot:[`${(data.subtasks||[]).length} workers`, elapsed()]},
    ...(data.subtasks||[]).map(s=>{ const u=nodeStatus(s); return {id:s.id, name:s.title||s.id, kind:'Worker', kindClass:'', ui:u, meta:`${s.profileId||''} · ${s.status}`, foot:[`iter ${s.iteration}/${s.maxIterations}`, s.completedAt?'done':'—']}; }),
    {id:'agg', name:'Aggregate Results', kind:'Leader', kindClass:'leader', ui:{cls:'waiting',text:'Waiting',pill:'waiting'}, meta:'collect + review', foot:[`${(data.subtasks||[]).filter(s=>/success/i.test(s.status||'')).length}/${(data.subtasks||[]).length} ready`,'—']},
    {id:'submit', name:'Submit Final', kind:'Leader', kindClass:'leader', ui:statusUi(data.status), meta:'team_submit_result', foot:['artifact','—']}
  ];
  defs.forEach(n=>{
    if(!layout[n.id]) layout[n.id]={x:100,y:100};
    const el=document.createElement('article');
    const cls=n.ui.cls==='looping'?'looping':n.ui.cls;
    el.className=`node ${cls}`+(selectedId===n.id?' selected':'');
    el.dataset.id=n.id; el.style.left=layout[n.id].x+'px'; el.style.top=layout[n.id].y+'px';
    const pill=n.ui.pill||n.ui.cls;
    el.innerHTML=`<div class="node-h"><span class="node-kind ${n.kindClass}">${esc(n.kind)}</span><span class="pill ${pill}"><i class="dot"></i> ${esc(n.ui.text)}</span></div>
      <div class="node-b"><div class="node-name">${esc(n.name)}</div><div class="node-meta">${esc(n.meta)}</div><div class="progress"><i></i></div>
      <div class="node-foot"><span>${esc(n.foot[0])}</span><span>${esc(n.foot[1])}</span></div></div>`;
    enableDrag(el);
    el.addEventListener('click',()=>{ if(el.dataset.didDrag==='1'){el.dataset.didDrag='0'; return;} selectNode(n.id); });
    nodesEl.appendChild(el);
  });
  redrawWires();
}
function applyZoom(){ worldEl.style.transform=`scale(${scale})`; document.getElementById('zoomLabel').textContent=Math.round(scale*100)+'%'; }
document.getElementById('zoomIn').onclick=()=>{ scale=Math.min(1.6, scale+0.1); applyZoom(); };
document.getElementById('zoomOut').onclick=()=>{ scale=Math.max(0.6, scale-0.1); applyZoom(); };
document.getElementById('zoomLabel').onclick=()=>{ scale=1; applyZoom(); };
document.getElementById('fitView').onclick=()=>{ scale=0.85; applyZoom(); viewport.scrollTo({left:80,top:0,behavior:'smooth'}); };
document.getElementById('resetLayout').onclick=()=>{
  Object.keys(layout).forEach(k=>delete layout[k]);
  Object.assign(layout, JSON.parse(JSON.stringify(defaultLayout)));
  renderNodes(); if(selectedId) selectNode(selectedId); postLayout();
};
document.getElementById('toggleDrawer').onclick=()=>{
  const closed=document.getElementById('workspace').classList.toggle('drawer-closed');
  document.getElementById('toggleDrawer').textContent=closed?'展开详情':'收起详情';
};
document.getElementById('closeBtn').onclick=()=>{ try{ chrome.webview.postMessage({type:'close'}); }catch(err){} };
document.querySelectorAll('.drawer-tab').forEach(tab=>{
  tab.addEventListener('click',()=>{
    document.querySelectorAll('.drawer-tab').forEach(t=>t.classList.remove('active'));
    tab.classList.add('active');
    const id=tab.dataset.tab;
    document.querySelectorAll('.pane').forEach(p=>{ p.hidden=p.dataset.pane!==id; });
  });
});
renderNodes();
const first=(data.subtasks||[]).find(s=>(s.iteration||1)>1) || (data.subtasks||[])[0] || {id:'leader'};
selectNode(first.id||'leader');
applyZoom();
</script>
</body>
</html>
""");
        return sb.ToString()
            .Replace("@@THEME@@", theme, StringComparison.Ordinal)
            .Replace("@@TITLE@@", title, StringComparison.Ordinal)
            .Replace("@@GOAL@@", goal, StringComparison.Ordinal)
            .Replace("@@SAFE@@", safe, StringComparison.Ordinal);
}
}
