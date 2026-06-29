const API = {
  async get(path) { const r = await fetch(path); if (!r.ok) throw Error(await r.text()); return r.json(); },
  async post(path, body) { const r = await fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }); if (!r.ok) throw Error(await r.text()); return r.json(); },
  async put(path, body) { const r = await fetch(path, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }); if (!r.ok) throw Error(await r.text()); return r.json(); },
  async delete(path) { const r = await fetch(path, { method: 'DELETE' }); if (!r.ok) throw Error(await r.text()); return r.json(); },
};

function $(sel, parent = document) { return parent.querySelector(sel); }
function $$(sel, parent = document) { return [...parent.querySelectorAll(sel)]; }
function html(strings, ...vals) { return strings.reduce((a, s, i) => a + s + (vals[i] ?? ''), ''); }
function escape(s) { const d = document.createElement('div'); d.textContent = s; return d.innerHTML; }

function toast(msg, type = 'success') {
  const el = document.getElementById('toast');
  el.textContent = msg;
  el.className = `toast ${type}`;
  setTimeout(() => el.classList.add('hidden'), 3000);
}

function formatDate(iso) {
  const d = new Date(iso);
  return d.toLocaleDateString() + ' ' + d.toLocaleTimeString();
}

function formatTokens(u) {
  if (!u) return '-';
  const parts = [`${u.prompt}↑ ${u.completion}↓ (${u.total} total)`];
  if (u.cached) parts.push(`${u.cached} cached`);
  if (u.cost) parts.push(`$${u.cost < 0.01 ? u.cost.toFixed(6) : u.cost.toFixed(4)}`);
  return parts.join(' · ');
}

function formatDuration(ms) {
  if (ms == null || isNaN(ms)) return '-';
  if (ms < 1000) return `${Math.round(ms)}ms`;
  const s = ms / 1000;
  if (s < 60) return `${s.toFixed(s < 10 ? 1 : 0)}s`;
  const m = Math.floor(s / 60), rem = Math.round(s % 60);
  return `${m}m ${rem}s`;
}

function formatCost(c) {
  if (!c) return '$0';
  return '$' + (c < 0.01 ? c.toFixed(6) : c.toFixed(4));
}

const TEST_SELECTION_STORAGE_KEY = 'open-agent-qa.selected-tests';

function loadSelectedTests() {
  try {
    const value = localStorage.getItem(TEST_SELECTION_STORAGE_KEY);
    return value === null ? null : new Set(JSON.parse(value));
  } catch {
    return null;
  }
}

function saveSelectedTests() {
  try {
    const paths = $$('#test-list input[type="checkbox"]:checked').map(input => input.dataset.path);
    localStorage.setItem(TEST_SELECTION_STORAGE_KEY, JSON.stringify(paths));
  } catch {}
}

function promptArtifactUrl(runId, artifactPath, fileName) {
  const encodedPath = artifactPath.split(/[/\\]/).map(encodeURIComponent).join('/');
  return `/api/runs/${encodeURIComponent(runId)}/prompts/${encodedPath}/${fileName}`;
}

// ---- Router ----
function render(html) {
  document.getElementById('app').innerHTML = html;
}

function navigate(hash) {
  history.pushState(null, '', hash);
  handleRoute();
}

window.addEventListener('hashchange', handleRoute);
window.addEventListener('popstate', handleRoute);

// Auto-reload a long-open SPA tab when the server has been rebuilt/restarted, so the user
// never keeps running stale app.js (hash navigation alone does not re-fetch the script).
let APP_VERSION = null;
async function checkVersion() {
  try {
    const r = await fetch('/api/version', { cache: 'no-store' });
    const { version } = await r.json();
    if (APP_VERSION === null) APP_VERSION = version;
    else if (version !== APP_VERSION) { location.reload(); return true; }
  } catch {}
  return false;
}
setInterval(checkVersion, 15000);

async function handleRoute() {
  if (await checkVersion()) return; // a reload is in flight
  const hash = location.hash.slice(1) || '/';
  updateNav(hash);

  try {
    if (hash === '/' || hash === '/dashboard') await renderDashboard();
    else if (hash === '/chat') await renderChatPage();
    else if (hash === '/run') await renderRunPage();
    else if (hash.startsWith('/runs/')) await renderRunReportPage(decodeURIComponent(hash.slice('/runs/'.length)));
    else if (hash === '/runs') await renderRunsPage();
    else if (hash === '/logs') await renderLogsPage();
    else if (hash === '/config') await renderConfigPage();
    else if (hash === '/issues') await renderIssuesPage();
    else render(`<div class="loading">Page not found</div>`);
  } catch (e) {
    render(`<div class="loading" style="color:var(--red)">Error: ${escape(e.message)}</div>`);
  }

  updateIssueBadge();
}

// ---- Run Artifacts Page ----
async function renderRunsPage() {
  const runs = await API.get('/api/runs').catch(() => []);
  render(html`
    <h2 style="margin-bottom:20px">Run Artifacts</h2>
    <div class="card">
      <div class="card-header">Saved Runs <span class="spacer"></span><button class="btn btn-sm" type="button" onclick="navigate('#/runs')">Refresh</button></div>
      ${runs.length === 0 ? '<div style="color:var(--text-muted)">No saved runs yet.</div>' : html`
        <div class="table-wrap"><table>
          <thead><tr><th>Started</th><th>Status</th><th>Progress</th><th>Report</th><th>Raw data (for AI)</th><th></th></tr></thead>
          <tbody>${runs.map(run => html`
            <tr>
              <td><code style="font-size:12px">${escape(run.id)}</code><br><span style="color:var(--text-muted);font-size:12px">${formatDate(run.startedAt)}</span></td>
              <td>${escape(run.status || 'unknown')}${run.error ? `<br><span style="color:var(--red);font-size:12px">${escape(run.error)}</span>` : ''}</td>
              <td>${run.completed ?? 0} / ${run.total ?? 0}</td>
              <td><button class="btn btn-sm btn-primary" type="button" onclick="navigate('#/runs/${encodeURIComponent(run.id)}')">📊 View Report</button></td>
              <td>
                <a class="btn btn-sm" href="/api/runs/${encodeURIComponent(run.id)}/artifacts/report.json" target="_blank">report.json</a>
                <a class="btn btn-sm" href="/api/runs/${encodeURIComponent(run.id)}/artifacts/chat.jsonl" target="_blank">chat.jsonl</a>
                <a class="btn btn-sm" href="/api/runs/${encodeURIComponent(run.id)}/artifacts/system.log" target="_blank">system.log</a>
                <a class="btn btn-sm" href="/api/runs/${encodeURIComponent(run.id)}/artifacts/issues.json" target="_blank">issues.json</a>
                <button class="btn btn-sm" type="button" onclick="showRunPrompts('${escape(run.id)}')">Prompts</button>
              </td>
              <td>
                <button class="btn btn-sm" type="button" onclick="openRunFolder('${escape(run.id)}')">📁 Open Folder</button>
                <a class="btn btn-sm" href="/api/runs/${encodeURIComponent(run.id)}/download">ZIP</a>
                <button class="btn btn-sm" type="button" onclick="deleteRun('${escape(run.id)}')">Delete</button>
              </td>
            </tr>`).join('')}</tbody>
        </table></div>`}
    </div>

    ${runs.length >= 2 ? html`
    <div class="card">
      <div class="card-header">Compare runs <span style="font-weight:400;font-size:13px;color:var(--text-muted)">— did a skill / prompt / tool change help?</span></div>
      <div style="display:flex;gap:10px;align-items:center;flex-wrap:wrap">
        <label style="font-size:12px;color:var(--text-muted)">before</label>
        <select id="cmp-before">${runs.map((r, i) => `<option value="${escape(r.id)}" ${i === 1 ? 'selected' : ''}>${escape(r.id)}</option>`).join('')}</select>
        <label style="font-size:12px;color:var(--text-muted)">after</label>
        <select id="cmp-after">${runs.map((r, i) => `<option value="${escape(r.id)}" ${i === 0 ? 'selected' : ''}>${escape(r.id)}</option>`).join('')}</select>
        <button class="btn btn-sm btn-primary" type="button" onclick="compareRuns()">Compare</button>
      </div>
      <div id="cmp-result"></div>
    </div>` : ''}

    <div id="run-prompts"></div>
  `);
}

window.compareRuns = async function() {
  const before = document.getElementById('cmp-before').value;
  const after = document.getElementById('cmp-after').value;
  const area = document.getElementById('cmp-result');
  if (before === after) { area.innerHTML = '<div style="color:var(--yellow);margin-top:10px">Pick two different runs.</div>'; return; }
  area.innerHTML = '<div style="color:var(--text-muted);margin-top:10px">Comparing…</div>';
  try {
    const c = await API.get(`/api/runs/${encodeURIComponent(before)}/compare/${encodeURIComponent(after)}`);
    const rows = [
      ['Tests passed', 'passed', true], ['Tests errored', 'errored', false], ['Tool calls', 'toolCalls', false],
      ['Skills advertised', 'advertisedSkills', true], ['Distinct skills loaded', 'distinctSkillsLoaded', true],
      ['load_skill calls', 'loadSkillCalls', true], ['Prompt tokens', 'promptTokens', false],
      ['Completion tokens', 'completionTokens', false], ['Cached tokens', 'cachedTokens', true],
      ['Cost (USD)', 'cost', false], ['Duration (ms)', 'durationMs', false],
    ];
    const cell = (label, key, higherBetter) => {
      const b = c.before[key], a = c.after[key], d = a - b;
      const fmt = key === 'cost' ? (x => '$' + Number(x).toFixed(6)) : (x => Number(x).toLocaleString());
      const color = d === 0 ? 'var(--text-muted)' : ((d > 0) === higherBetter ? 'var(--green)' : 'var(--red)');
      const deltaTxt = d === 0 ? '' : ` (${d > 0 ? '+' : ''}${key === 'cost' ? d.toFixed(6) : d.toLocaleString()})`;
      return `<tr><td>${label}</td><td style="text-align:right">${fmt(b)}</td><td style="text-align:right">${fmt(a)}</td><td style="color:${color}">${deltaTxt}</td></tr>`;
    };
    const changes = (c.changes || []).map(d => {
      const col = d.change === 'fixed' ? 'var(--green)' : d.change === 'regressed' ? 'var(--red)' : 'var(--text-muted)';
      return `<tr><td style="color:${col}">${escape(d.change)}</td><td colspan="3">${escape(d.test)} (tool calls ${d.toolCallsBefore} → ${d.toolCallsAfter})</td></tr>`;
    }).join('');
    area.innerHTML = html`<div class="table-wrap" style="margin-top:12px"><table>
      <thead><tr><th>Metric</th><th style="text-align:right">before</th><th style="text-align:right">after</th><th>Δ</th></tr></thead>
      <tbody>${rows.map(r => cell(r[0], r[1], r[2])).join('')}
      ${changes ? `<tr><td colspan="4" style="padding-top:10px;color:var(--text-muted);font-size:12px">Per-test changes</td></tr>${changes}` : `<tr><td colspan="4" style="color:var(--text-muted);font-size:12px">No per-test status changes.</td></tr>`}
      </tbody></table></div>`;
  } catch (e) { area.innerHTML = `<div style="color:var(--red);margin-top:10px">${escape(e.message)}</div>`; }
};

window.showRunPrompts = async function(id) {
  const area = document.getElementById('run-prompts');
  try {
    const prompts = await API.get(`/api/runs/${encodeURIComponent(id)}/artifacts/manifest.json`);
    area.innerHTML = html`<div class="card"><div class="card-header">Prompt Artifacts <span class="spacer"></span><button class="btn btn-sm" type="button" onclick="document.getElementById('run-prompts').innerHTML=''">Close</button></div>
      <div class="table-wrap"><table><thead><tr><th>Prompt</th><th>Status</th><th>Actionable Files</th></tr></thead><tbody>
      ${prompts.map(prompt => html`<tr><td>${escape(prompt.name)}<br><span style="color:var(--text-muted);font-size:12px">${escape(prompt.path)}</span></td><td>${escape(prompt.status)}${prompt.error ? `<br><span style="color:var(--red);font-size:12px">${escape(prompt.error)}</span>` : ''}</td><td>
        <a class="btn btn-sm" target="_blank" href="${promptArtifactUrl(id, prompt.artifactPath, 'prompt.md')}">Prompt</a>
        <a class="btn btn-sm" target="_blank" href="${promptArtifactUrl(id, prompt.artifactPath, 'chat.jsonl')}">Chat</a>
        <a class="btn btn-sm" target="_blank" href="${promptArtifactUrl(id, prompt.artifactPath, 'system.log')}">Log</a>
        <a class="btn btn-sm" target="_blank" href="${promptArtifactUrl(id, prompt.artifactPath, 'result.json')}">Result</a>
      </td></tr>`).join('')}</tbody></table></div></div>`;
  } catch (e) { toast(`Could not load prompt artifacts: ${e.message}`, 'error'); }
};

window.deleteRun = async function(id) {
  if (!confirm(`Delete run ${id} and all of its artifacts?`)) return;
  try { await API.delete(`/api/runs/${encodeURIComponent(id)}`); toast('Run deleted'); await renderRunsPage(); }
  catch (e) { toast(`Delete failed: ${e.message}`, 'error'); }
};

window.openRunFolder = async function(id) {
  try { await API.post(`/api/runs/${encodeURIComponent(id)}/open`, {}); toast('Opened run folder in Explorer'); }
  catch (e) { toast(`Could not open folder: ${e.message}`, 'error'); }
};

// ---- Human-readable Run Report (visual conversation view) ----
async function renderRunReportPage(id) {
  let report, manifest = [];
  try { report = await API.get(`/api/runs/${encodeURIComponent(id)}/artifacts/report.json`); }
  catch (e) {
    render(html`<div style="margin-bottom:16px"><button class="btn" onclick="navigate('#/runs')">← Back to Runs</button></div>
      <div class="card" style="color:var(--red)">No report found for run <code>${escape(id)}</code>. ${escape(e.message)}</div>`);
    return;
  }
  try { manifest = await API.get(`/api/runs/${encodeURIComponent(id)}/artifacts/manifest.json`); } catch {}
  const artifactByPath = {};
  for (const m of manifest) artifactByPath[m.path] = m.artifactPath;

  const results = report.results || [];
  const failed = results.filter(r => r.error).length;
  const passed = results.length - failed;
  const sum = results.reduce((a, r) => {
    const u = r.tokenUsage;
    if (u) { a.prompt += u.prompt || 0; a.completion += u.completion || 0; a.total += u.total || 0; a.cached += u.cached || 0; a.cost += u.cost || 0; }
    return a;
  }, { prompt: 0, completion: 0, total: 0, cached: 0, cost: 0 });
  const cacheRate = sum.prompt > 0 ? Math.round((sum.cached / sum.prompt) * 100) : 0;

  render(html`
    <div style="display:flex;align-items:center;gap:12px;margin-bottom:16px">
      <button class="btn" onclick="navigate('#/runs')">← Back to Runs</button>
      <h2 style="margin:0">Run Report</h2>
      <span class="spacer"></span>
      <button class="btn btn-sm" onclick="openRunFolder('${escape(id)}')">📁 Open Folder</button>
      <a class="btn btn-sm" href="/api/runs/${encodeURIComponent(id)}/artifacts/report.json" target="_blank">Raw JSON</a>
      <a class="btn btn-sm" href="/api/runs/${encodeURIComponent(id)}/download">ZIP</a>
    </div>

    <div class="card">
      <div class="card-header">Summary</div>
      <div class="report-meta">
        <div><b>Run</b><br><code style="font-size:12px">${escape(id)}</code></div>
        <div><b>Started</b><br>${formatDate(report.startedAt)}</div>
        <div><b>Total time</b><br>${formatDuration(report.durationMs)}</div>
        <div><b>Model</b><br>${escape(report.config?.model || 'unknown')}</div>
        <div><b>Tests</b><br><span style="color:var(--green)">${passed} passed</span>${failed ? ` · <span style="color:var(--red)">${failed} errored</span>` : ''}</div>
        <div><b>Tokens</b><br>${sum.total.toLocaleString()} (${sum.prompt.toLocaleString()}↑ ${sum.completion.toLocaleString()}↓)</div>
        <div><b>Cache hits</b><br>${sum.cached.toLocaleString()} (${cacheRate}% of input)</div>
        <div><b>Total cost</b><br><span style="color:var(--green)">${formatCost(sum.cost)}</span></div>
      </div>
    </div>

    ${results.map((r, i) => renderConversationCard(r, i, id, artifactByPath[r.test?.path])).join('')}
  `);
}

// Render one tool call as a collapsible step. `clock` is a mutable {prevEnd} carried across
// steps so each shows the wait (gap) since the previous step ended.
function toolStepHtml(tc, idx, clock) {
  const start = tc.startedAt ? new Date(tc.startedAt).getTime() : null;
  const end = tc.endedAt ? new Date(tc.endedAt).getTime() : null;
  const gap = (start != null && clock.prevEnd != null) ? start - clock.prevEnd : null;
  if (end != null) clock.prevEnd = end;
  const dur = tc.durationMs != null ? tc.durationMs : (start != null && end != null ? end - start : null);
  const meta = [
    gap != null && gap > 0 ? `<span class="gap-badge" title="time since previous step">+${formatDuration(gap)} wait</span>` : '',
    dur != null ? `<span class="tool-meta">${formatDuration(dur)}</span>` : '',
  ].filter(Boolean).join(' ');
  const input = JSON.stringify(tc.input ?? null, null, 2);
  const output = typeof tc.output === 'string' ? tc.output : JSON.stringify(tc.output ?? null, null, 2);
  return html`<details class="tool-step">
    <summary><span class="tool-meta">#${idx}</span> <span class="tool-name">${escape(tc.tool)}</span> ${meta}</summary>
    <div class="tool-meta" style="padding:8px 12px 0">Input</div>
    <pre>${escape(input.length > 4000 ? input.slice(0, 4000) + '\n… (truncated)' : input)}</pre>
    <div class="tool-meta" style="padding:8px 12px 0">Output</div>
    <pre>${escape(output && output.length > 6000 ? output.slice(0, 6000) + '\n… (truncated)' : (output || '(no output)'))}</pre>
  </details>`;
}

function renderConversationCard(r, i, runId, artifactPath) {
  const u = r.tokenUsage;
  const ok = !r.error;
  const trace = r.trace || [];
  const turns = r.conversation;
  const clock = { prevEnd: r.startedAt ? new Date(r.startedAt).getTime() : null };
  let stepNo = 0;
  const userBubble = html`<div class="chat-msg chat-msg-user"><div class="chat-msg-bubble">${escape(r.test?.prompt || '(no prompt)')}</div></div>`;
  const errorBubble = html`<div class="chat-msg chat-msg-assistant"><div class="chat-msg-bubble" style="border-color:var(--red);color:var(--red)"><strong>Error:</strong> ${escape(r.error)}</div></div>`;

  let convoBody;
  if (turns && turns.length) {
    // Turn-by-turn: each assistant interaction (text + its tool calls) in chronological order,
    // with that single LLM call's token/cost effect shown under it.
    convoBody = userBubble + turns.map((turn, ti) => {
      const textHtml = turn.text ? html`<div class="chat-msg chat-msg-assistant"><div class="chat-msg-bubble">${escape(turn.text)}</div></div>` : '';
      const stepsHtml = (turn.toolCalls || []).map(tc => toolStepHtml(tc, ++stepNo, clock)).join('');
      const usageHtml = turn.usage
        ? html`<div class="turn-usage">↳ interaction ${ti + 1} · ${formatTokens(turn.usage)}</div>`
        : '';
      return textHtml + stepsHtml + usageHtml;
    }).join('') + (ok ? '' : errorBubble);
  } else {
    // Fallback for runs captured before per-turn data existed: flat list of tool calls.
    const steps = trace.map(tc => toolStepHtml(tc, ++stepNo, clock)).join('');
    convoBody = userBubble
      + (trace.length ? html`<div class="chat-msg-label">${trace.length} tool call${trace.length === 1 ? '' : 's'} (flat — re-run for the turn-by-turn view)</div>${steps}` : '')
      + (ok ? html`<div class="chat-msg chat-msg-assistant"><div class="chat-msg-bubble">${escape(r.response || '(empty response)')}</div></div>` : errorBubble);
  }

  const rawLinks = artifactPath ? html`
    <span class="spacer"></span>
    <a class="btn btn-sm" target="_blank" href="${promptArtifactUrl(runId, artifactPath, 'chat.jsonl')}">chat.jsonl</a>
    <a class="btn btn-sm" target="_blank" href="${promptArtifactUrl(runId, artifactPath, 'result.json')}">result.json</a>
    <a class="btn btn-sm" target="_blank" href="${promptArtifactUrl(runId, artifactPath, 'system.log')}">log</a>` : '';

  return html`
    <div class="card">
      <div class="card-header">
        <span style="color:${ok ? 'var(--green)' : 'var(--red)'}">${ok ? '✓' : '✕'}</span>&nbsp;${escape(r.test?.name || 'test')}
        ${rawLinks}
      </div>
      <div class="report-meta" style="margin-bottom:12px">
        <div><b>Duration</b><br>${formatDuration(r.durationMs)}</div>
        <div><b>When</b><br>${r.startedAt ? formatDate(r.startedAt) : '-'}</div>
        ${turns && turns.length ? html`<div><b>Interactions</b><br>${turns.length}</div>` : ''}
        <div><b>Tool calls</b><br>${trace.length}</div>
        <div><b>Tokens</b><br>${u ? formatTokens(u) : '-'}</div>
        <div><b>Cost</b><br><span style="color:var(--green)">${formatCost(u?.cost)}</span></div>
      </div>

      <div class="convo">${convoBody}</div>

      ${r.test?.expected || r.test?.asserts ? html`<details style="margin-top:10px">
        <summary style="cursor:pointer;color:var(--text-muted);font-size:13px">Reviewer notes (Expected / Assert — not sent to the agent)</summary>
        ${r.test?.expected ? `<div class="chat-msg-label">Expected</div><pre style="white-space:pre-wrap;font-size:12px;background:var(--bg);padding:10px;border-radius:var(--radius)">${escape(r.test.expected)}</pre>` : ''}
        ${r.test?.asserts ? `<div class="chat-msg-label">Assert</div><pre style="white-space:pre-wrap;font-size:12px;background:var(--bg);padding:10px;border-radius:var(--radius)">${escape(r.test.asserts)}</pre>` : ''}
      </details>` : ''}
    </div>`;
}

function updateNav(hash) {
  $$('.nav-link').forEach(a => {
    const href = a.getAttribute('href');
    a.classList.toggle('active', href === '#/' && (hash === '/' || hash === '/dashboard') || href === `#${hash}`);
  });
}

async function updateIssueBadge() {
  try {
    const issues = await API.get('/api/issues');
    const open = issues.filter(i => i.status === 'open').length;
    const badge = document.getElementById('issue-badge');
    if (open > 0) { badge.textContent = open; badge.classList.remove('hidden'); }
    else badge.classList.add('hidden');
  } catch {}
}

// ---- Test Harness ----
async function renderChatPage() {
  let info;
  let mcpTools;
  try { info = await API.get('/api/chat/info'); } catch { info = {}; }
  try { mcpTools = await API.get('/api/mcp/tools'); } catch { mcpTools = []; }

  const serverToolMap = {};
  for (const s of mcpTools) {
    serverToolMap[s.server] = s;
  }

  render(html`
    <h2 style="margin-bottom:8px">Test Harness</h2>

    <div class="card" style="margin-bottom:16px">
      <div class="card-header">Loaded Configuration</div>
      <div style="font-size:13px;line-height:1.7">
        <div><strong>Model:</strong> ${escape(info.model || 'unknown')}</div>
        <div style="margin-top:6px">
          <strong>agent.md:</strong>
          ${info.agentMd
            ? html`<span style="color:var(--green)">\u2713</span> ${escape(info.agentMd)} (${info.agentMdLength} chars)`
            : '<span style="color:var(--text-muted)">not configured</span>'}
        </div>
        <div style="margin-top:6px">
          <strong>Skills:</strong>
          ${info.skills?.length > 0
            ? html`<span style="color:var(--green)">\u2713</span> ${info.skills.length} file(s): ${info.skills.map(s => escape(s)).join(', ')}`
            : info.skillsDir
              ? html`<span style="color:var(--yellow)">dir configured but no .md files found</span>`
              : '<span style="color:var(--text-muted)">not configured</span>'}
        </div>
        <div style="margin-top:6px">
          <strong>MCP Tools:</strong>
          ${info.mcpServers?.length > 0
            ? html`<span style="color:var(--green)">\u2713</span> ${info.mcpServers.length} server(s):
              ${info.mcpServers.map(s => {
                const t = serverToolMap[s.name];
                const tools = t?.tools || [];
                const err = t?.error;
                let status;
                if (err) status = html`<span style="color:var(--red)">connection failed</span>`;
                else if (tools.length === 0) status = html`<span style="color:var(--yellow)">connected, but no tools reported</span>`;
                else status = html`<span style="color:var(--green)">${tools.length} tool(s)</span>: ${tools.map(t => escape(t.name)).join(', ')}`;
                return html`<div style="margin-left:16px;margin-top:2px"><strong>${escape(s.name)}</strong> — ${status}</div>`;
              }).join('')}`
            : '<span style="color:var(--text-muted)">none configured</span>'}
        </div>
      </div>
    </div>

    <div id="chat-container" style="display:flex;flex-direction:column;height:calc(100vh - 360px);min-height:350px">
      <div id="chat-messages" style="flex:1;overflow-y:auto;background:var(--bg);border:1px solid var(--border);border-radius:var(--radius);padding:16px;margin-bottom:12px">
        <div class="chat-msg chat-msg-system">
          <div class="chat-msg-bubble">
            Agent is ready. Send a message below.
          </div>
        </div>
      </div>

      <div id="chat-status" style="color:var(--text-muted);font-size:12px;margin-bottom:8px;min-height:18px"></div>

      <div style="display:flex;gap:8px">
        <textarea id="chat-input" rows="2" style="flex:1;resize:none" placeholder="Type a message to your agent..."></textarea>
        <button id="chat-send" class="btn btn-primary" onclick="sendChatMessage()" style="align-self:flex-end">Send</button>
      </div>
    </div>
  `);

  document.getElementById('chat-input').addEventListener('keydown', function(e) {
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendChatMessage(); }
  });
}

let chatConversation = [];

window.sendChatMessage = async function() {
  const input = document.getElementById('chat-input');
  const msg = input.value.trim();
  if (!msg) return;

  input.value = '';
  input.disabled = true;
  document.getElementById('chat-send').disabled = true;

  addChatBubble('user', msg);
  chatConversation.push({ role: 'user', content: msg });

  const statusEl = document.getElementById('chat-status');
  statusEl.textContent = 'Agent is thinking...';

  try {
    const ac = new AbortController();
    const fetchTimeout = setTimeout(() => ac.abort(), 130000);

    const res = await fetch('/api/chat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ message: msg, conversation: chatConversation }),
      signal: ac.signal,
    });
    clearTimeout(fetchTimeout);

    if (!res.ok) {
      const err = await res.text();
      addChatBubble('assistant', `Error: ${err}`);
      statusEl.textContent = 'Failed';
      return;
    }

    const data = await res.json();

    addChatBubble('assistant', data.response || '[empty response]');
    chatConversation.push({ role: 'assistant', content: data.response || '' });

    if (data.toolCalls?.length > 0) {
      const toolDiv = document.createElement('div');
      toolDiv.className = 'chat-tool-calls';
      let toolHtml = '<div class="chat-msg-label">Tool calls:</div>';
      for (const tc of data.toolCalls) {
        toolHtml += html`
          <details style="margin:4px 0;padding:8px;background:var(--bg-card);border-radius:var(--radius);font-size:12px">
            <summary style="cursor:pointer;color:var(--blue)">${escape(tc.tool)}</summary>
            <pre style="margin-top:6px;white-space:pre-wrap;word-break:break-all">${escape(JSON.stringify(tc.input, null, 2))}</pre>
            <pre style="margin-top:4px;white-space:pre-wrap;word-break:break-all;color:var(--green)">${escape(typeof tc.output === 'string' ? tc.output.slice(0,500) : JSON.stringify(tc.output, null, 2).slice(0,500))}</pre>
          </details>
        `;
      }
      toolDiv.innerHTML = toolHtml;
      document.getElementById('chat-messages').appendChild(toolDiv);
    }

    if (data.tokenUsage) {
      const u = data.tokenUsage;
      statusEl.textContent = `Tokens: ${u.prompt}↑ ${u.completion}↓ (${u.total})`;
    } else {
      statusEl.textContent = 'Done';
    }

    scrollChat();
  } catch (e) {
    addChatBubble('assistant', `Request failed: ${e.message}`);
    statusEl.textContent = 'Error';
  } finally {
    input.disabled = false;
    document.getElementById('chat-send').disabled = false;
    input.focus();
  }
};

function addChatBubble(role, text) {
  const container = document.getElementById('chat-messages');
  const div = document.createElement('div');
  div.className = `chat-msg chat-msg-${role}`;
  div.innerHTML = `<div class="chat-msg-bubble">${escape(text)}</div>`;
  container.appendChild(div);
  scrollChat();
}

function scrollChat() {
  const container = document.getElementById('chat-messages');
  if (container) container.scrollTop = container.scrollHeight;
}

// ---- Dashboard ----
async function renderDashboard() {
  const [issues, tests] = await Promise.all([
    API.get('/api/issues').catch(() => []),
    API.get('/api/tests').catch(() => []),
  ]);

  const openIssues = issues.filter(i => i.status === 'open');
  const testCount = tests.length;

  render(html`
    <h2 style="margin-bottom:20px">Dashboard</h2>
    <div class="stats">
      <div class="stat">
        <div class="stat-value blue">${testCount}</div>
        <div class="stat-label">Test Cases</div>
      </div>
      <div class="stat">
        <div class="stat-value ${openIssues.length > 0 ? 'red' : 'green'}">${openIssues.length}</div>
        <div class="stat-label">Open Issues</div>
      </div>
      <div class="stat">
        <div class="stat-value green">${issues.length - openIssues.length}</div>
        <div class="stat-label">Resolved Issues</div>
      </div>
    </div>

    <div class="actions">
      <button class="btn btn-primary" onclick="navigate('#/run')">Run All Tests</button>
      <button class="btn" onclick="navigate('#/issues')">View Issues</button>
      <button class="btn" onclick="navigate('#/config')">Configuration</button>
    </div>

    <div class="card">
      <div class="card-header">Recent Reports</div>
      <div id="reports-list"><div class="loading">Loading...</div></div>
    </div>

    <div class="card">
      <div class="card-header">Open Issues</div>
      <div id="issues-preview">${openIssues.length === 0
        ? '<div style="color:var(--green)">No open issues. All clear!</div>'
        : renderIssueTable(openIssues.slice(0, 5))}
      </div>
    </div>
  `);

  // Load reports
  try {
    const reports = await API.get('/api/reports');
    const container = document.getElementById('reports-list');
    if (reports.length === 0) {
      container.innerHTML = '<div style="color:var(--text-muted)">No reports yet. Run some tests!</div>';
    } else {
      container.innerHTML = reports.map(r => html`
        <div style="padding:6px 0;border-bottom:1px solid var(--border);display:flex;justify-content:space-between">
          <a href="#" onclick="event.preventDefault();loadReport('${escape(r)}')" style="color:var(--blue)">${escape(r)}</a>
          <span style="color:var(--text-muted);font-size:13px">${formatDate(r.replace('report-','').replace('.json','').replace(/-/g,':').replace(/T/,' ').split(':').slice(0,2).join(':'))}</span>
        </div>
      `).join('');
    }
  } catch { document.getElementById('reports-list').innerHTML = '<div style="color:var(--text-muted)">No reports available</div>'; }
}

async function loadReport(name) {
  try {
    const report = await API.get(`/api/reports/${name}`);
    navigate('#/run');
    // Wait for render, then display results
    setTimeout(() => showRunResults(report), 100);
  } catch { toast('Failed to load report', 'error'); }
}

// ---- Run Tests Page ----
async function renderRunPage() {
  const [tests, config] = await Promise.all([
    API.get('/api/tests').catch(() => []),
    API.get('/api/config').catch(() => ({})),
  ]);
  const parallel = config.parallel ?? 1;
  const savedSelection = loadSelectedTests();
  const selectedPaths = savedSelection ?? new Set(tests);
  const selectedCount = tests.filter(test => selectedPaths.has(test)).length;

  render(html`
    <h2 style="margin-bottom:20px">Run Tests</h2>
    <div class="card">
      <div class="card-header">Select Tests
        <span class="spacer"></span>
        <button class="btn btn-sm" type="button" onclick="setAllTests(true)">Select all</button>
        <button class="btn btn-sm" type="button" onclick="setAllTests(false)">Select none</button>
      </div>
      <div class="checkbox-list" id="test-list">
        ${tests.length === 0
          ? '<div style="color:var(--text-muted)">No test files found. Run <code>init</code> or create .md files.</div>'
          : tests.map((t, i) => html`
            <label class="checkbox-item">
              <input type="checkbox" ${selectedPaths.has(t) ? 'checked' : ''} data-path="${escape(t)}" onchange="updateTestSelection()">
              <span>${escape(t.split(/[/\\]/).pop() || t)}</span>
              <span style="color:var(--text-muted);font-size:12px;margin-left:8px">${escape(t)}</span>
            </label>
          `).join('')}
      </div>
    </div>

    <div class="form-row" style="margin-bottom:16px">
      <div class="form-group">
        <label>Parallel Workers</label>
        <input type="number" id="parallel-input" value="${parallel}" min="1" max="20">
      </div>
    </div>

    <div class="actions">
      <button class="btn btn-primary" id="run-btn" type="button" onclick="runSelected()" ${selectedCount ? '' : 'disabled'}>Run Selected${selectedCount ? ` (${selectedCount})` : ''}</button>
      <button class="btn" id="setup-btn" type="button" onclick="spinUpCleanInstance()" title="Run the configured setupScript to prepare a clean testing environment">Spin up clean instance</button>
      <button class="btn" onclick="navigate('/')">Back</button>
    </div>

    <div id="setup-output"></div>

    <div id="progress-area" class="hidden">
      <div class="card">
        <div class="card-header">Progress</div>
        <div id="progress-text" role="status">Starting...</div>
        <div class="progress-bar"><div class="progress-fill" id="progress-fill" style="width:0%"></div></div>
      </div>
    </div>

    <div id="results-area"></div>
  `);
}

window.setAllTests = function(checked) {
  $$('#test-list input[type="checkbox"]').forEach(input => { input.checked = checked; });
  updateTestSelection();
};

window.updateTestSelection = function() {
  const selected = $$('#test-list input[type="checkbox"]:checked').length;
  const total = $$('#test-list input[type="checkbox"]').length;
  const btn = document.getElementById('run-btn');
  if (!btn || btn.dataset.running === 'true') return;
  saveSelectedTests();
  btn.textContent = selected ? `Run Selected (${selected})` : 'Run Selected';
  btn.disabled = selected === 0 || total === 0;
};

window.runSelected = async function() {
  const checked = $$('#test-list input[type="checkbox"]:checked');
  const files = checked.map(c => c.dataset.path);
  const parallel = parseInt(document.getElementById('parallel-input').value) || 1;

  if (files.length === 0) { toast('Select at least one test', 'error'); return; }

  const btn = document.getElementById('run-btn');
  btn.dataset.running = 'true';
  btn.disabled = true;
  btn.textContent = 'Running...';

  const progressArea = document.getElementById('progress-area');
  progressArea.classList.remove('hidden');
  const progressText = document.getElementById('progress-text');
  const progressFill = document.getElementById('progress-fill');
  progressText.textContent = `Running ${files.length} test${files.length === 1 ? '' : 's'} with ${parallel} worker${parallel === 1 ? '' : 's'}...`;
  progressFill.classList.add('running');

  try {
    const { jobId } = await API.post('/api/tests/run', { files, parallel, async: true });
    await pollRunJob(jobId);
  } catch (e) {
    const message = e.message || String(e);
    document.getElementById('results-area').innerHTML = `<div class="card" style="margin-top:16px;color:var(--red)">Run failed: ${escape(message)}</div>`;
    toast(`Run failed: ${message}`, 'error');
  } finally {
    progressFill.classList.remove('running');
    delete btn.dataset.running;
    btn.disabled = false;
    updateTestSelection();
    progressArea.classList.add('hidden');
  }
};

window.spinUpCleanInstance = async function() {
  const btn = document.getElementById('setup-btn');
  const output = document.getElementById('setup-output');
  btn.disabled = true;
  btn.textContent = 'Spinning up...';
  output.innerHTML = html`<div class="card" style="margin-top:16px">
    <div class="card-header">Setup Script <span id="setup-status" style="font-weight:400;font-size:13px;color:var(--blue)">running…</span></div>
    <pre id="setup-log" style="background:var(--bg);padding:12px;border-radius:var(--radius);font-size:12px;white-space:pre-wrap;max-height:420px;overflow:auto">Starting…\n</pre>
  </div>`;
  const logEl = document.getElementById('setup-log');
  const statusEl = document.getElementById('setup-status');

  const setStatus = (text, color) => { statusEl.textContent = text; statusEl.style.color = color; };
  const t0 = Date.now();
  const ticker = setInterval(() => { if (statusEl.dataset.done !== '1') setStatus(`running… ${Math.floor((Date.now() - t0) / 1000)}s`, 'var(--blue)'); }, 1000);

  try {
    const res = await fetch('/api/setup/run', { method: 'POST' });
    if (!res.body) { logEl.textContent = await res.text(); }
    else {
      logEl.textContent = '';
      const reader = res.body.getReader();
      const decoder = new TextDecoder();
      let buffer = '';
      while (true) {
        const { value, done } = await reader.read();
        if (done) break;
        const chunk = decoder.decode(value, { stream: true });
        buffer += chunk;
        const atBottom = logEl.scrollHeight - logEl.scrollTop - logEl.clientHeight < 40;
        logEl.textContent += chunk;
        if (atBottom) logEl.scrollTop = logEl.scrollHeight;
      }
      const failed = /\n\[ERROR\]/.test(buffer);
      statusEl.dataset.done = '1';
      setStatus(failed ? `✕ failed (${Math.floor((Date.now() - t0) / 1000)}s)` : `✓ done (${Math.floor((Date.now() - t0) / 1000)}s)`, failed ? 'var(--red)' : 'var(--green)');
      toast(failed ? 'Setup failed — see output' : 'Clean instance ready', failed ? 'error' : 'success');
    }
  } catch (e) {
    statusEl.dataset.done = '1';
    setStatus('✕ error', 'var(--red)');
    logEl.textContent += `\nRequest failed: ${e.message}`;
    toast(`Setup request failed: ${e.message}`, 'error');
  } finally {
    clearInterval(ticker);
    btn.disabled = false;
    btn.textContent = 'Spin up clean instance';
  }
};

async function pollRunJob(jobId) {
  const job = await API.get(`/api/tests/run/${jobId}`);
  renderRunProgress(job);

  if (job.status === 'running') {
    await new Promise(resolve => setTimeout(resolve, 750));
    return pollRunJob(jobId);
  }
  if (job.status === 'failed') throw new Error(job.error || 'Run failed');

  showRunResults(job.report);
  const failed = job.report.results.filter(r => r.error).length;
  toast(`Completed: ${job.report.total - failed} passed, ${failed} failed`, failed ? 'error' : 'success');
}

function renderRunProgress(job) {
  const progressText = document.getElementById('progress-text');
  const progressFill = document.getElementById('progress-fill');
  const progressArea = document.getElementById('progress-area');
  if (!progressText || !progressFill || !progressArea) return;

  const elapsedSeconds = Math.max(0, Math.floor((Date.now() - new Date(job.startedAt).getTime()) / 1000));
  progressText.textContent = `${job.completed} of ${job.total} completed — elapsed ${elapsedSeconds}s`;
  progressFill.classList.remove('running');
  progressFill.style.width = `${job.total ? Math.round((job.completed / job.total) * 100) : 0}%`;

  let detail = document.getElementById('progress-details');
  if (!detail) {
    detail = document.createElement('div');
    detail.id = 'progress-details';
    detail.style.cssText = 'margin-top:12px;font-size:13px;line-height:1.7';
    progressArea.querySelector('.card').appendChild(detail);
  }
  detail.innerHTML = job.tests.map(test => {
    const icon = test.status === 'completed' ? '✓' : test.status === 'failed' ? '✕' : test.status === 'running' ? '…' : '○';
    const color = test.status === 'failed' ? 'var(--red)' : test.status === 'completed' ? 'var(--green)' : test.status === 'running' ? 'var(--blue)' : 'var(--text-muted)';
    const suffix = test.durationMs ? ` (${Math.round(test.durationMs / 1000)}s)` : test.error ? ` — ${escape(test.error)}` : '';
    return `<div style="color:${color}">${icon} ${escape(test.name)} — ${escape(test.status)}${suffix}</div>`;
  }).join('');
}

function showRunResults(report) {
  const area = document.getElementById('results-area');
  if (!area) return;

  area.innerHTML = html`
    <div class="stats" style="margin-top:16px">
      <div class="stat">
        <div class="stat-value blue">${report.total}</div>
        <div class="stat-label">Prompts</div>
      </div>
      <div class="stat">
        <div class="stat-value">${Math.round(report.durationMs / report.total)}ms</div>
        <div class="stat-label">Avg Duration</div>
      </div>
      <div class="stat">
        <div class="stat-value">${report.logs?.length || 0}</div>
        <div class="stat-label">Logs Saved</div>
      </div>
    </div>

    <div style="margin-bottom:16px">
      <a href="#/logs" class="btn">View All Logs</a>
    </div>

    <div class="card">
      <div class="card-header">Results <span style="font-weight:400;font-size:13px;color:var(--text-muted)">(${formatDate(report.startedAt)})</span></div>
      <div class="table-wrap">
        <table>
          <thead><tr><th>Prompt</th><th>Duration</th><th>Tokens</th><th>Tool Calls</th><th>Status</th><th></th></tr></thead>
          <tbody>
            ${report.results.map((r, i) => html`
              <tr>
                <td>${escape(r.test.name)}</td>
                <td>${r.durationMs}ms</td>
                <td>${r.tokenUsage ? r.tokenUsage.total : '-'}</td>
                <td>${r.trace.length}</td>
                <td>${r.error ? '<span class="status-fail">Error</span>' : '<span class="status-pass">OK</span>'}</td>
                <td><button class="btn btn-sm" onclick="toggleDetail('run-detail-${i}')">↕</button></td>
              </tr>
              <tr id="run-detail-${i}" style="display:none">
                <td colspan="6">
                  <div style="background:var(--bg);padding:12px;border-radius:var(--radius);font-family:monospace;font-size:12px;white-space:pre-wrap;max-height:400px;overflow:auto">
                    <strong>Timing:</strong> ${r.startedAt ? formatDate(r.startedAt) + ' → ' : ''}${r.durationMs}ms
                    ${r.tokenUsage ? '\n<strong>Tokens:</strong> ' + formatTokens(r.tokenUsage) : ''}\n\n<strong>Response:</strong>\n${escape(r.response?.slice(0, 3000) || '(empty)')}
                    ${r.trace.length > 0 ? '\n\n<strong>Tool Calls:</strong>\n' + escape(JSON.stringify(r.trace, null, 2)) : ''}
                    ${r.error ? '\n\n<strong>Error:</strong>\n' + escape(r.error) : ''}
                    ${report.logs?.[i] ? '\n\n<strong>Log:</strong> ' + escape(report.logs[i]) : ''}
                  </div>
                </td>
              </tr>
            `).join('')}
          </tbody>
        </table>
      </div>
    </div>
  `;
}

window.toggleDetail = function(id) {
  const row = document.getElementById(id);
  if (row) row.style.display = row.style.display === 'none' ? 'table-row' : 'none';
};

// ---- Logs ----
async function renderLogsPage() {
  let logs;
  try { logs = await API.get('/api/logs'); } catch { logs = []; }

  render(html`
    <h2 style="margin-bottom:20px">Run Logs</h2>
    ${logs.length === 0
      ? '<div class="loading">No logs found. Run some prompts first.</div>'
      : html`
    <div class="table-wrap">
      <table>
        <thead><tr><th>Prompt</th><th>Duration</th><th>Tokens</th><th>Tool Calls</th><th>Status</th><th></th><th></th></tr></thead>
        <tbody>
          ${logs.map((l, i) => html`
            <tr>
              <td>${escape(l.test?.name ?? l.testName ?? '(unknown)')}</td>
              <td>${l.durationMs}ms</td>
              <td>${l.tokenUsage ? l.tokenUsage.total : '-'}</td>
              <td>${l.trace?.length ?? 0}</td>
              <td>${l.error ? '<span class="status-fail">Error</span>' : '<span class="status-pass">OK</span>'}</td>
              <td><button class="btn btn-sm" onclick="toggleDetail('log-detail-${i}')">↕</button></td>
              <td><button class="btn btn-sm btn-primary" onclick="flagIssue(${i})">Flag Issue</button></td>
            </tr>
            <tr id="log-detail-${i}" style="display:none">
              <td colspan="7">
                <div style="background:var(--bg);padding:12px;border-radius:var(--radius);font-family:monospace;font-size:12px;white-space:pre-wrap;max-height:400px;overflow:auto">
                  <strong>Timing:</strong> ${l.startedAt ? formatDate(l.startedAt) + ' → ' : ''}${l.durationMs}ms
                  ${l.tokenUsage ? '\n<strong>Tokens:</strong> ' + formatTokens(l.tokenUsage) : ''}\n\n<strong>Response:</strong>\n${escape(l.response?.slice(0, 3000) || '(empty)')}
                  ${l.trace?.length > 0 ? '\n\n<strong>Tool Calls:</strong>\n' + escape(JSON.stringify(l.trace, null, 2).slice(0, 2000)) : ''}
                  ${l.error ? '\n\n<strong>Error:</strong>\n' + escape(l.error) : ''}
                </div>
              </td>
            </tr>
          `).join('')}
        </tbody>
      </table>
    </div>`}
  `);

  window.flagIssue = async function(idx) {
    const log = logs[idx];
    if (!log) return;
    const summary = prompt('Issue summary:', `Review needed for "${log.testName}"`);
    if (!summary) return;
    try {
      const issue = await API.post(`/api/logs/${encodeURIComponent(log.name)}/issue`, { summary });
      toast(`Issue ${issue.id} created`);
      updateIssueBadge();
    } catch (e) { toast('Failed: ' + e.message, 'error'); }
  };
}

// ---- Config Page ----
function label(name, tip) {
  return html`<div class="label-row"><label>${escape(name)}</label><span class="info-icon" data-tip="${escape(tip)}">i</span></div>`;
}

async function renderConfigPage() {
  let config;
  let mcpRaw;
  let agentData;
  try { config = await API.get('/api/config'); } catch { config = {}; }
  try { mcpRaw = await API.get('/api/mcp-config'); } catch { mcpRaw = { mcpServers: {} }; }
  try { agentData = await API.get('/api/agents'); } catch { agentData = { active: '', agents: [] }; }
  const activeAgent = agentData.active || '(none)';

  render(html`
    <h2 style="margin-bottom:20px">Configuration</h2>

    <div class="card">
      <div class="card-header">Agents <span class="spacer"></span><span style="font-weight:400;font-size:13px;color:var(--text-muted)">active: <strong style="color:var(--blue)">${escape(activeAgent)}</strong></span></div>
      <div class="info-callout">
        Each folder under <code>Agents/</code> is a self-contained agent: its <code>agent.md</code> (system prompt),
        <code>skills/</code>, <code>mcp.json</code> tools, and an optional <code>agent.json</code> that overrides the
        test directory / setup script. The active agent is stored in <code>.env</code> as
        <code>OPENAGENTQA_AGENT</code> — switch it from the selector in the top bar.
      </div>
      ${(agentData.agents || []).length === 0
        ? '<div style="color:var(--text-muted)">No agents found under the Agents folder.</div>'
        : html`<div class="table-wrap"><table>
            <thead><tr><th></th><th>Agent</th><th>Description</th><th>Tests</th><th>Skills</th><th>MCP</th><th>Setup</th></tr></thead>
            <tbody>${agentData.agents.map(a => html`<tr>
              <td>${a.name === agentData.active ? '<span style="color:var(--green)">●</span>' : ''}</td>
              <td><strong>${escape(a.name)}</strong></td>
              <td style="font-size:13px;color:var(--text-muted);max-width:340px">${escape(a.description || '')}</td>
              <td style="font-size:12px">${a.testDir ? '<span title="' + escape(a.testDir) + '">override</span>' : 'default'}</td>
              <td>${a.skillCount}</td>
              <td>${a.mcpServerCount}</td>
              <td>${a.hasSetupScript ? '✓' : '—'}</td>
            </tr>`).join('')}</tbody></table></div>`}
    </div>

    <div class="card">
      <div class="card-header">Provider & Model</div>
      <div class="form-row">
        <div class="form-group">
          ${label('Provider', 'Currently only OpenRouter is supported. The provider is fixed and non-configurable.')}
          <div style="padding:8px 12px;background:var(--bg);border:1px solid var(--border);border-radius:var(--radius);color:var(--text-muted);font-size:14px">openrouter</div>
        </div>
        <div class="form-group">
          ${label('Model', 'Select a model from OpenRouter. The list is fetched from OpenRouter\'s models API.')}
          <select id="cfg-model" style="width:100%">
            <option value="">Loading models...</option>
          </select>
        </div>
      </div>
      <div class="form-row">
        <div class="form-group">
          ${label('Temperature', 'Controls randomness (0.0–2.0). Lower values (0–0.3) for deterministic tests, higher (0.7+) for creative tasks.')}
          <input id="cfg-temp" type="number" step="0.1" min="0" max="2" value="${config.temperature ?? 0.3}">
        </div>
        <div class="form-group">
          ${label('Max Steps', 'Maximum multi-turn iterations per test (prompt + tool calls). Prevents infinite loops. Each step is one LLM call.')}
          <input id="cfg-steps" type="number" min="1" max="100" value="${config.maxSteps ?? 20}">
        </div>
      </div>
    </div>

    <div class="card">
      <div class="card-header">Test Directory <span class="spacer"></span><span style="font-weight:400;font-size:13px;color:var(--text-muted)">resolved for <strong>${escape(activeAgent)}</strong></span></div>
      <div class="info-callout">
        <strong>How tests work:</strong> Each <code>.md</code> file in this directory is a test case
        (subdirectories scanned recursively). This is the <strong>default</strong> — an agent can override it
        with <code>testDir</code> in its <code>agent.json</code>. The box below shows the directory currently in
        effect for the active agent.
      </div>
      <div class="form-group">
        ${label('Test Directory (default)', 'Path to the root folder containing test .md files. All subdirectories are scanned recursively. An agent\'s agent.json testDir overrides this.')}
        <input id="cfg-testdir" value="${escape(config.testDir || './tests')}">
      </div>
      <div class="form-row">
        <div class="form-group">
          ${label('Parallel Workers', 'Number of tests to run concurrently. Higher values finish faster but consume more API tokens at once. Default: 1 (sequential).')}
          <input id="cfg-parallel" type="number" min="1" max="20" value="${config.parallel ?? 1}">
        </div>
      </div>
    </div>

    <div class="card">
      <div class="card-header">Clean Environment</div>
      <div class="info-callout">
        The setup script (PowerShell) runs on demand from the Run page ("Spin up clean instance") to prepare a
        fresh testing environment. It is configured per-agent via <code>setupScript</code> in the agent's
        <code>agent.json</code> (or a global <code>setupScript</code> default). Below is the script in effect for
        the active agent — blank means the "Spin up clean instance" action is disabled.
      </div>
      <div class="form-group">
        ${label('Setup script (active agent)', 'Resolved from the active agent\'s agent.json (setupScript), or the global default. Edit it in the agent folder.')}
        <div style="padding:8px 12px;background:var(--bg);border:1px solid var(--border);border-radius:var(--radius);color:var(--text-muted);font-size:13px;font-family:monospace;word-break:break-all">${escape(config.setupScript || '(none)')}</div>
      </div>
    </div>

    <div class="card">
      <div class="card-header">MCP Servers <span class="spacer"></span><span style="font-weight:400;font-size:13px;color:var(--text-muted)">edits <strong>${escape(activeAgent)}</strong>/mcp.json</span>
        <span class="info-icon" data-tip="MCP (Model Context Protocol) servers provide tools the agent can use during tests. Saved to the ACTIVE agent's mcp.json (Agents/<agent>/mcp.json). Uses the standard { mcpServers: { name: { command, args, env } } } format." style="vertical-align:middle;margin-left:6px">i</span>
      </div>
      <div class="form-group">
        ${label('mcp.json', 'Edit the active agent\'s raw mcp.json. Switch agents in the top bar to edit a different one. Uses { mcpServers: { name: { command, args, env } } }.')}
        <textarea id="cfg-mcp-raw" rows="8" style="font-family:monospace;font-size:13px">${escape(JSON.stringify(mcpRaw, null, 2))}</textarea>
      </div>
    </div>

    <div class="card">
      <div class="card-header">Issue Tracking</div>
      <div class="form-row">
        <div class="form-group">
          ${label('Enabled', 'When enabled, every failed test automatically creates an issue in .harness/issues/. Issues persist across runs and can be resolved via the UI or CLI.')}
          <select id="cfg-tracker-enabled">
            <option value="true" ${config.tracker?.enabled ? 'selected' : ''}>Yes</option>
            <option value="false" ${!config.tracker?.enabled ? 'selected' : ''}>No</option>
          </select>
        </div>
        <div class="form-group">
          ${label('Issues directory', 'Directory where issue JSON files are stored. Relative to project root.')}
          <input id="cfg-tracker-dir" value="${escape(config.tracker?.dir || '.harness/issues')}">
        </div>
      </div>
    </div>

    <button class="btn btn-primary" onclick="saveConfig()">Save Configuration</button>
  `);

  const select = document.getElementById('cfg-model');
  const currentModel = config.model || 'openai/gpt-4o';

  try {
    const modelsData = await API.get('/api/models');
    const models = modelsData.data || [];
    select.innerHTML = models
      .sort((a, b) => (a.id || '').localeCompare(b.id || ''))
      .map(m => `<option value="${escape(m.id)}" ${m.id === currentModel ? 'selected' : ''}>${escape(m.id)}${m.name && m.name !== m.id ? ' — ' + escape(m.name) : ''}</option>`)
      .join('');
    if (!models.find(m => m.id === currentModel)) {
      select.innerHTML += `<option value="${escape(currentModel)}" selected>${escape(currentModel)}</option>`;
    }
  } catch {
    select.innerHTML = `<option value="${escape(currentModel)}" selected>${escape(currentModel)}</option>`;
  }
}

window.saveConfig = async function() {
  const body = {
    provider: "openrouter",
    model: document.getElementById('cfg-model').value,
    testDir: document.getElementById('cfg-testdir').value,
    temperature: parseFloat(document.getElementById('cfg-temp').value),
    maxSteps: parseInt(document.getElementById('cfg-steps').value),
    parallel: parseInt(document.getElementById('cfg-parallel').value),
    tracker: {
      enabled: document.getElementById('cfg-tracker-enabled').value === 'true',
      dir: document.getElementById('cfg-tracker-dir').value,
    },
  };
  try {
    await API.put('/api/config', body);

    const mcpRaw = document.getElementById('cfg-mcp-raw').value;
    try {
      const parsed = JSON.parse(mcpRaw);
      await API.put('/api/mcp-config', parsed);
    } catch (e) {
      toast('MCP config has invalid JSON: ' + e.message, 'error');
      return;
    }

    toast('Configuration saved');
  } catch (e) { toast('Save failed: ' + e.message, 'error'); }
};

// ---- Issues Page ----
async function renderIssuesPage() {
  let issues;
  try { issues = await API.get('/api/issues'); } catch { issues = []; }

  const openIssues = issues.filter(i => i.status === 'open');
  const resolvedIssues = issues.filter(i => i.status === 'resolved');

  render(html`
    <h2 style="margin-bottom:20px">Issues</h2>
    <div class="stats">
      <div class="stat">
        <div class="stat-value ${openIssues.length > 0 ? 'red' : 'green'}">${openIssues.length}</div>
        <div class="stat-label">Open</div>
      </div>
      <div class="stat">
        <div class="stat-value green">${resolvedIssues.length}</div>
        <div class="stat-label">Resolved</div>
      </div>
      <div class="stat">
        <div class="stat-value blue">${issues.length}</div>
        <div class="stat-label">Total</div>
      </div>
    </div>

    <div class="card">
      <div class="card-header">Open Issues</div>
      ${openIssues.length === 0
        ? '<div style="color:var(--green)">No open issues.</div>'
        : renderIssueTable(openIssues, true)}
    </div>

    ${resolvedIssues.length > 0 ? html`
      <div class="card">
        <div class="card-header">Resolved Issues</div>
        ${renderIssueTable(resolvedIssues, false)}
      </div>
    ` : ''}
  `);
}

function renderIssueTable(issues, showResolve = false) {
  if (issues.length === 0) return '<div style="color:var(--text-muted)">None</div>';
  return html`
    <div class="table-wrap">
      <table>
        <thead><tr><th>ID</th><th>Severity</th><th>Test</th><th>Summary</th><th>Created</th>${showResolve ? '<th></th>' : ''}<th></th></tr></thead>
        <tbody>
          ${issues.map(i => html`
            <tr>
              <td><strong>${escape(i.id)}</strong></td>
              <td class="${i.severity === 'error' ? 'status-fail' : 'status-open'}">${escape(i.severity)}</td>
              <td style="font-size:13px">${escape(i.test.split(/[/\\]/).pop() || i.test)}</td>
              <td style="max-width:250px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${escape(i.summary)}</td>
              <td style="font-size:13px;white-space:nowrap">${formatDate(i.created)}</td>
              ${showResolve ? html`<td><button class="btn btn-sm btn-primary" onclick="resolveIssue('${escape(i.id)}')">Resolve</button></td>` : ''}
              <td><button class="btn btn-sm" onclick="toggleDetail('issue-detail-${escape(i.id)}')">↕</button></td>
            </tr>
            <tr id="issue-detail-${escape(i.id)}" style="display:none">
              <td colspan="${showResolve ? 7 : 6}">
                <div style="background:var(--bg);padding:12px;border-radius:var(--radius);font-family:monospace;font-size:12px;white-space:pre-wrap;max-height:300px;overflow:auto">
                  ${escape(i.trace.slice(0, 2000))}
                </div>
              </td>
            </tr>
          `).join('')}
        </tbody>
      </table>
    </div>
  `;
}

window.resolveIssue = async function(id) {
  try {
    await API.post(`/api/issues/${id}/resolve`, {});
    toast(`Issue ${id} resolved`);
    renderIssuesPage();
    updateIssueBadge();
  } catch (e) { toast('Failed to resolve: ' + e.message, 'error'); }
};

// ---- Agent selector (nav) ----
async function loadAgentSelector() {
  const sel = document.getElementById('agent-select');
  if (!sel) return;
  try {
    const { active, agents } = await API.get('/api/agents');
    if (!agents || agents.length === 0) { sel.innerHTML = '<option>(no agents)</option>'; sel.disabled = true; return; }
    sel.disabled = false;
    sel.innerHTML = agents.map(a => `<option value="${escape(a.name)}" ${a.name === active ? 'selected' : ''}>${escape(a.name)}</option>`).join('');
  } catch { sel.innerHTML = '<option>(unavailable)</option>'; }
}

window.switchAgent = async function(name) {
  try {
    await API.post('/api/agents/active', { agent: name });
    toast(`Switched to agent: ${name}`);
    // Reload so every page (tests, MCP, prompt, setup) reflects the new agent.
    setTimeout(() => location.reload(), 350);
  } catch (e) {
    toast(`Could not switch agent: ${e.message}`, 'error');
    loadAgentSelector();
  }
};

// ---- Startup ----
loadAgentSelector();
handleRoute();
