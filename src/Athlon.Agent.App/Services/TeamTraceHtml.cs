using System.Text.Json;
using Athlon.Agent.Core.Teams;

namespace Athlon.Agent.App.Services;

public static class TeamTraceHtml
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Build(TeamTask task, IReadOnlyList<TeamTraceEvent> events)
    {
        var payload = JsonSerializer.Serialize(new
        {
            taskId = task.TaskId,
            goal = task.Goal,
            status = task.Status.ToString(),
            layout = task.TraceLayout,
            subtasks = task.SubTasks.Select(s => new
            {
                id = s.Id,
                title = s.Title,
                status = s.Status.ToString(),
                profileId = s.AssigneeProfileId,
                iteration = s.Iteration,
                maxIterations = s.MaxIterations,
                result = s.Result,
                handoff = s.Handoff,
                reason = s.RedispatchReason
            }),
            events = events.Select(e => new
            {
                e.Type,
                e.At,
                e.NodeId,
                e.FromNodeId,
                e.ToNodeId,
                e.Iteration,
                e.Message
            })
        }, JsonOptions);

        var safe = payload.Replace("</", "<\\/", StringComparison.Ordinal);
        return $$"""
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1"/>
<title>Team Trace</title>
<style>
:root{--bg:#0B1220;--panel:#1E293B;--fg:#F1F5F9;--muted:#94A3B8;--border:#334155;--accent:#818CF8;--ok:#34D399;--run:#60A5FA;--wait:#64748B;--loop:#FBBF24;--err:#F87171;--dot:rgba(129,140,248,.14);--font:Segoe UI,Microsoft YaHei UI,sans-serif;--mono:ui-monospace,Consolas,monospace}
*{box-sizing:border-box}html,body{height:100%;margin:0;font:13px/1.45 var(--font);color:var(--fg);background:var(--bg);overflow:hidden}
.top{height:44px;display:flex;align-items:center;justify-content:space-between;padding:0 12px;border-bottom:1px solid var(--border);background:#0F172A}
.sub{color:var(--muted);font-size:11px;max-width:60%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.pill{display:inline-flex;gap:6px;align-items:center;padding:2px 8px;border-radius:999px;font-size:11px;font-weight:650}
.pill.run{background:rgba(96,165,250,.14);color:var(--run)}.pill.ok{background:rgba(52,211,153,.14);color:var(--ok)}.pill.loop{background:rgba(251,191,36,.14);color:var(--loop)}
.wrap{display:grid;grid-template-columns:1fr 340px;height:calc(100% - 44px)}
.canvas{position:relative;overflow:auto;background:radial-gradient(circle at 1px 1px,var(--dot) 1px,transparent 0) 0 0/22px 22px}
.world{position:relative;width:1400px;height:900px;margin:16px}
.node{position:absolute;width:200px;border:2px solid var(--border);border-radius:12px;background:var(--panel);padding:10px;cursor:grab;user-select:none;box-shadow:0 8px 24px rgba(0,0,0,.35)}
.node.selected{border-color:var(--accent);box-shadow:0 0 0 3px rgba(129,140,248,.35)}
.node.ok{border-color:var(--ok)}.node.run{border-color:var(--run)}.node.loop{border-color:var(--loop)}.node.wait{opacity:.9}
.node h4{margin:0 0 4px;font-size:13px}.meta{color:var(--muted);font:11px var(--mono)}
svg.wires{position:absolute;inset:0;width:100%;height:100%;pointer-events:none}
.wire{fill:none;stroke:#64748B;stroke-width:2}.wire.ok{stroke:var(--ok)}.wire.loop{stroke:var(--loop);stroke-dasharray:2 6}.wire.run{stroke:var(--accent);stroke-dasharray:6 6}
.drawer{border-left:1px solid var(--border);background:#0F172A;overflow:auto;padding:12px}
.drawer h3{margin:0 0 8px;font-size:14px}.block{border:1px solid var(--border);border-radius:10px;margin:8px 0;overflow:hidden}
.block summary{padding:8px 10px;background:#1E293B;cursor:pointer;font-weight:650}
.block pre{margin:0;padding:10px;white-space:pre-wrap;font:11.5px/1.4 var(--mono);max-height:220px;overflow:auto}
.note{position:absolute;left:12px;bottom:12px;background:#0F172A;border:1px solid var(--border);border-radius:10px;padding:8px 10px;color:var(--muted);font-size:11px;max-width:360px}
</style>
</head>
<body>
<div class="top">
  <div>
    <div id="title">Team Trace</div>
    <div class="sub" id="goal"></div>
  </div>
  <div id="statusPill" class="pill run">Running</div>
</div>
<div class="wrap">
  <section class="canvas"><div class="world" id="world"><svg class="wires" id="wires"></svg><div id="nodes"></div></div>
    <div class="note">Drag nodes to rearrange. Orange dashed edges = loop redispatch. Layout posts back to host.</div>
  </section>
  <aside class="drawer">
    <h3 id="dTitle">Select a node</h3>
    <div class="meta" id="dMeta"></div>
    <details class="block" open><summary>Handoff / Input</summary><pre id="dIn"></pre></details>
    <details class="block" open><summary>Result / Output</summary><pre id="dOut"></pre></details>
    <details class="block" open><summary>Timeline</summary><pre id="dTime"></pre></details>
  </aside>
</div>
<script>
const data = {{safe}};
const NODE_W=200, NODE_H=96;
const defaultLayout = { leader:{x:520,y:40}, submit:{x:520,y:700} };
data.subtasks.forEach((s,i)=>{ defaultLayout[s.id]={x:120+i*260,y:280}; });
defaultLayout.agg={x:520,y:500};
const layout = Object.assign({}, defaultLayout, data.layout||{});
const world=document.getElementById('world');
const wires=document.getElementById('wires');
const nodesEl=document.getElementById('nodes');
document.getElementById('goal').textContent = data.goal||'';
document.getElementById('title').textContent = 'Team Trace · '+data.taskId;
const st=document.getElementById('statusPill');
st.textContent=data.status; st.className='pill '+(data.status==='Succeeded'?'ok':data.status==='Failed'?'run':'run');

function statusClass(s){
  s=(s||'').toLowerCase();
  if(s.includes('success')||s==='succeeded') return 'ok';
  if(s.includes('run')) return 'run';
  if(s.includes('fail')||s.includes('error')) return 'run';
  return 'wait';
}
function port(id,side){const p=layout[id]||{x:0,y:0};const cx=p.x+NODE_W/2,cy=p.y+NODE_H/2;
  if(side==='bottom')return{x:cx,y:p.y+NODE_H}; if(side==='top')return{x:cx,y:p.y}; if(side==='right')return{x:p.x+NODE_W,y:cy}; return{x:cx,y:cy};}
function curve(a,b,loop){if(loop){const mx=Math.max(a.x,b.x)+110;return `M ${a.x} ${a.y} C ${mx} ${a.y}, ${mx} ${b.y}, ${b.x} ${b.y}`;}
  const dy=Math.max(40,Math.abs(b.y-a.y)*.45);return `M ${a.x} ${a.y} C ${a.x} ${a.y+dy}, ${b.x} ${b.y-dy}, ${b.x} ${b.y}`;}

function redraw(){
  wires.innerHTML='';
  const edges=[];
  edges.push({from:'leader',to:null,type:'ok'});
  data.subtasks.forEach(s=>{edges.push({from:'leader',to:s.id,type:statusClass(s.status),loop:s.iteration>1});});
  data.subtasks.forEach(s=>{edges.push({from:s.id,to:'agg',type:statusClass(s.status)});});
  edges.push({from:'agg',to:'submit',type:'wait'});
  edges.forEach(e=>{
    if(!e.to) return;
    const loop=!!e.loop;
    const a=port(e.from, loop?'right':'bottom');
    const b=port(e.to, loop?'right':'top');
    const p=document.createElementNS('http://www.w3.org/2000/svg','path');
    p.setAttribute('d', curve(a,b,loop));
    p.setAttribute('class','wire '+(loop?'loop':e.type));
    wires.appendChild(p);
  });
  // also loop edges from events
  (data.events||[]).filter(ev=>ev.Type==='loop.edge'||ev.type==='loop.edge').forEach(ev=>{
    const to=ev.ToNodeId||ev.toNodeId; if(!to) return;
    const a=port('leader','right'); const b=port(to,'right');
    const p=document.createElementNS('http://www.w3.org/2000/svg','path');
    p.setAttribute('d', curve(a,b,true));
    p.setAttribute('class','wire loop');
    wires.appendChild(p);
  });
}

function select(id, meta){
  document.querySelectorAll('.node').forEach(n=>n.classList.toggle('selected', n.dataset.id===id));
  document.getElementById('dTitle').textContent=meta.title;
  document.getElementById('dMeta').textContent=meta.meta||'';
  document.getElementById('dIn').textContent=meta.input||'(none)';
  document.getElementById('dOut').textContent=meta.output||'(none)';
  const lines=(data.events||[]).filter(e=>(e.NodeId||e.nodeId||e.ToNodeId||e.toNodeId)===id || id==='leader')
    .map(e=>`${e.At||e.at||''} ${e.Type||e.type} ${e.Message||e.message||''}`);
  document.getElementById('dTime').textContent=lines.join('\n')||'(no events)';
}

function makeNode(id, title, meta, cls, x, y, detail){
  const el=document.createElement('article');
  el.className='node '+cls; el.dataset.id=id; el.style.left=x+'px'; el.style.top=y+'px';
  el.innerHTML=`<h4>${title}</h4><div class="meta">${meta}</div>`;
  let sx,sy,ox,oy,drag=false;
  el.addEventListener('pointerdown',ev=>{drag=true;el.setPointerCapture(ev.pointerId);sx=ev.clientX;sy=ev.clientY;ox=layout[id].x;oy=layout[id].y;});
  el.addEventListener('pointermove',ev=>{if(!drag)return; layout[id]={x:Math.max(8,ox+(ev.clientX-sx)),y:Math.max(8,oy+(ev.clientY-sy))}; el.style.left=layout[id].x+'px'; el.style.top=layout[id].y+'px'; redraw();});
  el.addEventListener('pointerup',()=>{drag=false; postLayout();});
  el.addEventListener('click',()=>select(id, detail));
  nodesEl.appendChild(el);
}

function postLayout(){
  try{ chrome.webview.postMessage({type:'traceLayout', layout}); }catch(e){}
}

makeNode('leader','Leader','plan + dispatch', statusClass(data.status), layout.leader.x, layout.leader.y, {title:'Leader',meta:'orchestrator',input:data.goal,output:'see timeline'});
data.subtasks.forEach(s=>{
  const loc=layout[s.id]||{x:100,y:280};
  const cls=s.iteration>1?'loop':statusClass(s.status);
  makeNode(s.id, s.title||s.id, `iter ${s.iteration}/${s.maxIterations}`, cls, loc.x, loc.y, {title:s.title, meta:s.profileId, input:s.handoff, output:s.result||s.reason||''});
});
makeNode('agg','Aggregate','collect', 'wait', layout.agg.x, layout.agg.y, {title:'Aggregate',meta:'leader',input:'collect results',output:''});
makeNode('submit','Submit Final','team_submit_result', statusClass(data.status), layout.submit.x, layout.submit.y, {title:'Submit',meta:'final',input:'',output:''});
redraw();
if(data.subtasks[0]) select(data.subtasks[0].id, {title:data.subtasks[0].title, meta:data.subtasks[0].profileId, input:data.subtasks[0].handoff, output:data.subtasks[0].result||''});
</script>
</body>
</html>
""";
    }
}
