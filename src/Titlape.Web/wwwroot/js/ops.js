const me = T.requireRole('Admin', 'Teacher');
T.netChip(document.getElementById('net'));
document.getElementById('logout').onclick = T.logout;
const $ = (id) => document.getElementById(id);
const isAdmin = me?.role === 'Admin';
const kb = (b) => b >= 1024 ? `${(b / 1024).toFixed(1)} KB` : `${b} B`;

async function load() {
  let s;
  try { s = await T.api('/api/ops/status'); } catch (e) { T.toast(e.message); return; }
  $('node').textContent = `${s.node} · curriculum ${s.rag.pack}`;
  $('kpis').innerHTML = [
    [s.online ? 'Online' : 'Offline', `Backhaul (${s.backhaul})`],
    [`${Math.round(s.power.batteryPercent)}%`, s.power.solarCharging ? 'Battery · solar charging' : 'Battery · discharging'],
    [s.replica.edgeDocs, 'Documents on edge replica'],
    [s.replica.cloudDocs, 'Documents synced to cloud'],
    [s.audit.entries, s.audit.verification.intact ? 'Audit entries · chain intact' : 'Audit entries · CHAIN BROKEN'],
    [s.rag.chunks, 'Syllabus passages indexed'],
  ].map(([v, l]) => `<div class="kpi"><div class="v">${T.esc(v)}</div><div class="l">${T.esc(l)}</div></div>`).join('');

  $('online').checked = s.online; $('online').disabled = !isAdmin;
  $('battery').value = s.power.batteryPercent; $('battery').disabled = !isAdmin;
  $('batteryV').textContent = `${Math.round(s.power.batteryPercent)}%${s.power.lowPowerMode ? ' (low-power mode)' : ''}`;
  $('syncNow').disabled = !isAdmin;
  $('route').innerHTML = `<b>Routing now:</b> ${T.esc(s.routing.model)} <span class="chip">${T.esc(s.routing.site)}</span><br><span class="faint">${T.esc(s.routing.reason)}</span>`;

  $('engines').innerHTML = `<thead><tr><th>Engine</th><th>Site</th><th>Quant.</th><th>Signature</th><th>Calls</th><th>p95</th></tr></thead><tbody>${s.engines.map(e => `
    <tr><td class="mono">${T.esc(e.name)}</td><td>${T.esc(e.site)}</td><td>${T.esc(e.quantization)}</td>
    <td>${e.verified ? '<span class="chip good">verified</span>' : '<span class="chip bad">unverified</span>'}</td>
    <td>${e.stats?.totalCalls ?? 0}</td><td>${e.stats ? e.stats.p95LatencyMs.toFixed(2) + ' ms' : '—'}</td></tr>`).join('')}
    ${s.catalogue.filter(m => !s.engines.some(e => e.name === m.name)).map(m => `<tr class="faint"><td class="mono">${T.esc(m.name)}</td><td>${T.esc(m.site)}</td><td>${T.esc(m.quantization)}</td><td colspan="3">catalogue — not installed</td></tr>`).join('')}</tbody>`;
  $('engNote').textContent = 'Set Titlape:EdgeSlmUrl to a llama.cpp server (or Titlape:CloudLlmUrl to vLLM) to route real SLM traffic; unverified models are never used.';

  $('sync').innerHTML = `<thead><tr><th>Time</th><th>Result</th><th>Docs ↑/↓</th><th>Wire</th><th>REST/JSON equiv.</th><th>Saving</th><th>Conflicts</th><th>Est. 3G time</th></tr></thead><tbody>${s.sync.map(r => `
    <tr><td class="faint">${new Date(r.at).toLocaleTimeString()}</td>
    <td>${r.performed ? '<span class="chip good">synced</span>' : `<span class="chip warn">${T.esc(r.skippedReason)}</span>`}</td>
    <td>${r.docsPushed} / ${r.docsPulled}</td><td>${kb(r.wireBytes)}${r.compressionUsed ? ' <span class="faint">(brotli)</span>' : ''}</td>
    <td>${kb(r.jsonEquivalentBytes)}</td><td>${r.performed && r.jsonEquivalentBytes ? T.pct(r.payloadReductionVsJson) : '—'}</td>
    <td>${r.conflictsResolved}</td><td>${r.estimatedTransferSeconds.toFixed(2)} s</td></tr>`).join('') || '<tr><td class="empty" colspan="8">No sync yet.</td></tr>'}</tbody>`;

  $('grants').innerHTML = Object.entries(s.toolGrants).map(([a, tools]) => `<div><b>${T.esc(a)}</b>: ${tools.map(T.esc).join(', ')}</div>`).join('');

  const audit = await T.api('/api/ops/audit?n=60');
  $('auditStatus').innerHTML = audit.verification.intact
    ? `<span class="chip good"><span class="dot"></span>HMAC chain verified</span> <span class="faint">${audit.verification.entries} entries</span>`
    : `<span class="chip bad"><span class="dot"></span>Broken at #${audit.verification.firstBrokenSequence}</span> ${T.esc(audit.verification.detail)}`;
  $('audit').innerHTML = `<thead><tr><th>#</th><th>Actor</th><th>Action</th><th>Detail</th></tr></thead><tbody>${audit.entries.map(e => `
    <tr><td class="faint">${e.sequence}</td><td class="mono">${T.esc(e.actor)}</td><td>${T.esc(e.action)}</td><td class="faint">${T.esc(e.detail)}</td></tr>`).join('')}</tbody>`;

  const bias = await T.api('/api/ops/bias');
  $('bias').innerHTML = bias.map(b => `<div style="margin:10px 0">
    <div><b>${T.esc(b.metric)}</b> by ${T.esc(b.attribute)} <span class="chip ${b.flagged ? 'bad' : 'good'}">ratio ${b.disparityRatio.toFixed(2)}</span></div>
    <div class="faint">${b.groups.map(g => `${T.esc(g.group)} ${T.pct(g.rate)} (n=${g.n})`).join(' · ')}</div>
    <div class="faint">${T.esc(b.interpretation)}</div></div>`).join('');
}

$('online').onchange = async () => { await T.api('/api/ops/connectivity', { method: 'POST', body: { online: $('online').checked } }); load(); };
$('battery').onchange = async () => { await T.api('/api/ops/power', { method: 'POST', body: { batteryPercent: +$('battery').value, solarCharging: true } }); load(); };
$('syncNow').onclick = async () => { const r = await T.api('/api/ops/sync', { method: 'POST' }); T.toast(r.performed ? `Synced ${kb(r.wireBytes)}` : r.skippedReason); load(); };

$('runEval').disabled = !isAdmin;
$('runEval').onclick = async () => {
  $('runEval').disabled = true; $('evalNote').textContent = 'Running… (simulated learners through the full agent pipeline)';
  try {
    const r = await T.api('/api/ops/evaluation', { method: 'POST', body: {} });
    $('evalNote').textContent = `Completed in ${r.durationSeconds.toFixed(1)} s · ${r.details.turns} turns · ${r.details.learners} learners · ${r.config.days} days`;
    $('matrix').innerHTML = `<thead><tr><th>Category</th><th>RQ</th><th>Component</th><th>Metric</th><th>Result</th><th>Benchmark</th><th>Status</th><th>Design refinement trigger</th></tr></thead><tbody>${r.matrix.map(m => `
      <tr><td><b>${T.esc(m.category)}</b></td><td>${T.esc(m.researchQuestions)}</td><td>${T.esc(m.component)}</td><td>${T.esc(m.metric)}</td>
      <td>${T.esc(m.value)}${m.note ? `<div class="faint">${T.esc(m.note)}</div>` : ''}</td><td>${T.esc(m.benchmark)}</td>
      <td>${m.pass === null ? '<span class="na">N/A</span>' : m.pass ? '<span class="pass">PASS</span>' : '<span class="fail">FAIL</span>'}</td>
      <td>${m.triggerFired ? `<span class="chip bad">FIRED</span> ` : ''}<span class="faint">${T.esc(m.refinementTrigger)}</span></td></tr>`).join('')}</tbody>`;
  } catch (e) { T.toast(e.message); $('evalNote').textContent = 'Evaluation failed.'; }
  finally { $('runEval').disabled = !isAdmin; }
};

if (me) { load(); setInterval(load, 15000); }
