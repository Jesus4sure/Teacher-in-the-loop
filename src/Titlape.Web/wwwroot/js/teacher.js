const me = T.requireRole('Teacher', 'Admin');
T.netChip(document.getElementById('net'));
document.getElementById('logout').onclick = T.logout;

const $ = (id) => document.getElementById(id);
const classId = new URLSearchParams(location.search).get('class') || me?.classes?.[0] || 'cls-ng-p4a';
let data = null;
let showHandled = false;
const short = (code) => code.split('-').slice(2).join('-');
const nameOf = (p) => data?.names?.[p] ?? p;
const sevChip = { Urgent: 'urgent', Act: 'bad', Watch: 'warn', Info: 'indigo' };

async function load() {
  try { data = await T.api(`/api/teacher/classes/${classId}/overview`); }
  catch (e) { T.toast(e.message); return; }
  $('where').textContent = `${data.classroom.name} · ${data.school.name}, ${data.school.region}`;
  renderKpis(); renderAlerts(); renderHeat(); renderProposals(); loadTraces();
}

function renderKpis() {
  const st = data.students;
  const attempted = st.flatMap(s => Object.values(s.mastery).filter(m => m.attempts > 0));
  const avg = attempted.length ? attempted.reduce((a, m) => a + m.pKnown, 0) / attempted.length : null;
  const atRisk = st.filter(s => s.risk >= 0.7).length;
  const m = data.metrics;
  const [trust, trustN] = [data.trust.mean, data.trust.n];
  const tiles = [
    [st.length, 'Learners'],
    [T.pct(avg), 'Average mastery (practised skills)'],
    [atRisk, 'Learners at risk'],
    [m.pending, 'Alerts awaiting you'],
    [T.pct(m.actedRate), 'Alerts acted on'],
    [trustN ? `${trust.toFixed(1)}/5` : '—', `Explanation usefulness (n=${trustN})`],
  ];
  $('kpis').innerHTML = tiles.map(([v, l]) => `<div class="kpi"><div class="v">${T.esc(v)}</div><div class="l">${T.esc(l)}</div></div>`).join('');
  $('alertNote').innerHTML = `${m.digestSuppressed} low-priority signals folded into the digest to avoid alert fatigue · <a href="#" id="toggleHandled">${showHandled ? 'hide' : 'show'} handled</a>`;
  $('toggleHandled').onclick = (e) => { e.preventDefault(); showHandled = !showHandled; renderKpis(); renderAlerts(); };
}

function shapChart(attrs) {
  if (!attrs?.length) return '';
  const max = Math.max(...attrs.map(a => Math.abs(a.contribution)), 0.0001);
  return `<div class="shap" role="img" aria-label="Why this learner was flagged">${attrs.map(a => {
    const w = Math.abs(a.contribution) / max * 50;
    return `<div class="lbl" title="${T.esc(a.plainLanguage)}">${T.esc(a.feature)}</div>
            <div class="axis" title="${T.esc(a.plainLanguage)}"><div class="seg ${a.contribution >= 0 ? 'pos' : 'neg'}" style="width:${w}%"></div></div>`;
  }).join('')}</div><p class="faint" style="margin:6px 0 0">Red raises risk, green lowers it, compared with the class average (exact Shapley values).</p>`;
}

function renderAlerts() {
  const list = data.alerts.filter(a => showHandled || a.status === 'Pending');
  if (!list.length) { $('alerts').innerHTML = `<div class="card empty">No alerts need your attention right now.</div>`; return; }
  $('alerts').innerHTML = list.map(a => {
    const done = a.status !== 'Pending';
    const proposal = a.proposalId ? data.proposals.find(p => p.id === a.proposalId) : null;
    return `<article class="card alert ${a.severity} ${done ? 'done' : ''}" data-id="${a.id}">
      <div class="head">
        <span class="chip ${sevChip[a.severity]}"><span class="dot"></span>${a.severity}</span>
        ${a.skillCode ? `<span class="chip">${T.esc(short(a.skillCode))}</span>` : ''}
        ${done ? `<span class="chip">${T.esc(a.status)}</span>` : ''}
        <span class="faint" style="margin-left:auto">${new Date(a.createdAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</span>
      </div>
      <h3 style="margin-top:8px">${T.esc(a.title)}</h3>
      <p class="muted" style="margin:4px 0">${T.esc(a.summary)}</p>
      <p style="margin:6px 0"><b>Learners:</b> ${a.studentIds.map(p => `<a href="#" data-student="${p}">${T.esc(nameOf(p))}</a>`).join(', ')}</p>
      ${shapChart(a.attributions)}
      ${a.recommendedAction ? `<p style="margin:8px 0 6px"><b>Suggested:</b> ${T.esc(a.recommendedAction)}</p>` : ''}
      ${proposal ? `<p class="faint">Proposed pathway: ${proposal.proposedSequence.map(short).join(' → ')}</p>` : ''}
      <div class="btn-row" style="margin-top:8px">
        ${a.evidenceTraceIds.slice(-3).map((t, i) => `<button class="btn small ghost" data-trace="${t}">Evidence ${i + 1}</button>`).join('')}
      </div>
      ${done ? '' : `<div class="btn-row" style="margin-top:10px">
        ${proposal && proposal.status === 'Pending'
          ? `<button class="btn small primary" data-prop="${proposal.id}" data-approve="1">Approve pathway</button>
             <button class="btn small" data-modify="${proposal.studentId}" data-propid="${proposal.id}">Modify…</button>
             <button class="btn small" data-prop="${proposal.id}" data-approve="0">Dismiss</button>`
          : `<button class="btn small primary" data-decide="Acknowledged">I'll handle it</button>
             <button class="btn small" data-decide="Dismissed">Dismiss</button>`}
      </div>`}
      <div class="stars" style="margin-top:10px" aria-label="Rate how useful this explanation was">
        <span class="faint">Useful explanation?</span>
        ${[1, 2, 3, 4, 5].map(n => `<button data-star="${n}" aria-label="${n} of 5">★</button>`).join('')}
      </div>
    </article>`;
  }).join('');

  $('alerts').querySelectorAll('article').forEach(card => {
    const id = card.dataset.id;
    card.querySelectorAll('[data-decide]').forEach(b => b.onclick = async () => {
      await T.api(`/api/teacher/alerts/${id}/decision`, { method: 'POST', body: { decision: b.dataset.decide } });
      T.toast('Recorded in the audit log.'); load();
    });
    card.querySelectorAll('[data-prop]').forEach(b => b.onclick = async () => {
      await T.api(`/api/teacher/proposals/${b.dataset.prop}/decision`, { method: 'POST', body: { approve: b.dataset.approve === '1', note: 'from alert' } });
      T.toast(b.dataset.approve === '1' ? 'Pathway updated.' : 'Proposal dismissed.'); load();
    });
    card.querySelectorAll('[data-modify]').forEach(b => b.onclick = () => openStudent(b.dataset.modify, b.dataset.propid));
    card.querySelectorAll('[data-trace]').forEach(b => b.onclick = () => openTrace(b.dataset.trace));
    card.querySelectorAll('[data-student]').forEach(b => b.onclick = (e) => { e.preventDefault(); openStudent(b.dataset.student); });
    card.querySelectorAll('[data-star]').forEach(b => b.onclick = async () => {
      const n = +b.dataset.star;
      card.querySelectorAll('[data-star]').forEach(x => x.classList.toggle('on', +x.dataset.star <= n));
      await T.api(`/api/teacher/alerts/${id}/trust`, { method: 'POST', body: { score: n } });
      T.toast('Thanks — this helps us improve explanations.');
    });
  });
}

function renderHeat() {
  const skills = data.skills;
  $('heat').innerHTML = `<thead><tr><th>Learner</th>${skills.map(s => `<th title="${T.esc(s.title)}">${short(s.code)}</th>`).join('')}<th>Risk</th><th>Now on</th></tr></thead>
  <tbody>${data.students.map(s => `<tr class="clickable" data-p="${s.pseudonym}">
    <td><b>${T.esc(s.name)}</b><div class="faint">${T.esc(s.language)}${s.consentTranscript ? '' : ' · no transcripts'}</div></td>
    ${skills.map(k => { const m = s.mastery[k.code]; return `<td><span class="cell ${T.masteryClass(m.pKnown, m.attempts)}" title="${m.attempts} attempts, ${m.correct} correct">${m.attempts ? Math.round(m.pKnown * 100) : '·'}</span></td>`; }).join('')}
    <td style="min-width:90px"><div class="bar-track"><div class="bar-fill" style="width:${Math.round(s.risk * 100)}%;background:${s.risk >= .7 ? 'var(--bad)' : s.risk >= .4 ? 'var(--warn)' : 'var(--good)'}"></div></div><span class="faint">${T.pct(s.risk)}</span></td>
    <td class="faint">${T.esc(s.pathway.skillSequence[s.pathway.currentIndex] ? short(s.pathway.skillSequence[s.pathway.currentIndex]) : 'review (all done)')}</td>
  </tr>`).join('')}</tbody>`;
  $('heat').querySelectorAll('tr[data-p]').forEach(r => r.onclick = () => openStudent(r.dataset.p));
}

function renderProposals() {
  if (!data.proposals.length) { $('proposals').innerHTML = `<div class="empty">No proposals yet.</div>`; return; }
  $('proposals').innerHTML = data.proposals.slice(0, 8).map(p => `
    <div style="padding:10px 0;border-bottom:1px solid var(--line)">
      <div style="display:flex;gap:8px;align-items:center;flex-wrap:wrap"><b>${T.esc(nameOf(p.studentId))}</b>
        <span class="chip ${p.status === 'Pending' ? 'warn' : 'good'}">${T.esc(p.status)}</span>
        <span class="faint">by ${T.esc(p.proposedBy)} agent</span></div>
      <div class="muted">${T.esc(p.rationale)}</div>
      <div class="faint">Proposed: ${p.proposedSequence.map(short).join(' → ')}</div>
      ${p.status === 'Pending' ? `<div class="btn-row" style="margin-top:6px">
        <button class="btn small primary" data-a="${p.id}" data-v="1">Approve</button>
        <button class="btn small" data-m="${p.studentId}" data-id="${p.id}">Modify…</button>
        <button class="btn small" data-a="${p.id}" data-v="0">Dismiss</button></div>` : (p.decidedBy ? `<div class="faint">Decided by ${T.esc(p.decidedBy)}</div>` : '')}
    </div>`).join('');
  $('proposals').querySelectorAll('[data-a]').forEach(b => b.onclick = async () => {
    await T.api(`/api/teacher/proposals/${b.dataset.a}/decision`, { method: 'POST', body: { approve: b.dataset.v === '1' } });
    load();
  });
  $('proposals').querySelectorAll('[data-m]').forEach(b => b.onclick = () => openStudent(b.dataset.m, b.dataset.id));
}

async function loadTraces() {
  const traces = await T.api(`/api/teacher/classes/${classId}/traces?n=25`).catch(() => []);
  $('traces').innerHTML = `<thead><tr><th>Time</th><th>Learner</th><th>Skill</th><th>Outcome</th><th>Safety</th></tr></thead><tbody>${traces.map(t => `
    <tr class="clickable" data-t="${t.traceId}">
      <td class="faint">${new Date(t.startedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })}</td>
      <td>${T.esc(nameOf(t.learnerPseudonym))}</td><td>${T.esc(t.skillCode ? short(t.skillCode) : '')}</td>
      <td>${T.esc(t.outcome ?? '')}</td>
      <td>${t.blocked ? `<span class="chip bad">${t.blocked} blocked</span>` : '<span class="chip good">clean</span>'}</td>
    </tr>`).join('')}</tbody>`;
  $('traces').querySelectorAll('tr[data-t]').forEach(r => r.onclick = () => openTrace(r.dataset.t));
}

function dialog(title, html) {
  $('dlgTitle').textContent = title;
  $('dlgBody').innerHTML = html;
  if (!$('dlg').open) $('dlg').showModal();
}
$('dlgClose').onclick = () => $('dlg').close();

async function openTrace(id) {
  try {
    const t = await T.api(`/api/teacher/traces/${id}`);
    dialog(`Tutoring decision · ${nameOf(t.learnerPseudonym)}`, `
      <p class="faint">Trace <span class="mono">${t.traceId}</span> · ${t.skillCode ?? ''} · ${t.totalMs.toFixed(1)} ms · outcome: <b>${T.esc(t.outcome ?? '')}</b></p>
      <ol class="timeline">${t.steps.map(s => `
        <li class="${s.data.allowed === 'False' ? 'blocked' : ''}">
          <div><span class="agent">${T.esc(s.agent)}</span> <span class="faint">· ${T.esc(s.step)} · ${s.durationMs} ms</span></div>
          <div>${T.esc(s.plainLanguage)}</div>
          ${Object.keys(s.data).length ? `<div class="kv">${Object.entries(s.data).map(([k, v]) => `<span>${T.esc(k)}=${T.esc(v)}</span>`).join('')}</div>` : ''}
        </li>`).join('')}</ol>`);
  } catch (e) { T.toast(e.message); }
}

function openStudent(pseudonym, proposalId) {
  const s = data.students.find(x => x.pseudonym === pseudonym);
  if (!s) return;
  const proposal = proposalId ? data.proposals.find(p => p.id === proposalId) : null;
  let seq = [...(proposal ? proposal.proposedSequence : s.pathway.skillSequence)];
  const mis = Object.entries(s.misconceptions || {});
  const render = () => dialog(s.name, `
    <div class="btn-row" style="margin-bottom:10px">
      <span class="chip">${T.esc(s.language)}</span><span class="chip mono">${T.esc(s.pseudonym)}</span>
      <span class="chip ${s.risk >= .7 ? 'bad' : 'good'}">risk ${T.pct(s.risk)}</span>
    </div>
    <h3>Why this risk estimate</h3>${shapChart(s.attributions)}
    <ul class="faint" style="margin:6px 0 0;padding-left:18px">${s.attributions.slice(0, 3).map(a => `<li>${T.esc(a.plainLanguage)}</li>`).join('')}</ul>
    <h3 style="margin-top:16px">Recurring errors</h3>
    ${mis.length ? `<ul style="margin:0;padding-left:18px">${mis.map(([k, v]) => `<li>${T.esc(data.misconceptionNames[k] || k)} — ${v}×</li>`).join('')}</ul>` : '<p class="faint">None recorded.</p>'}
    <h3 style="margin-top:16px">${proposal ? 'Modify the proposed pathway' : 'Learning pathway'}</h3>
    <p class="faint">Approved by ${T.esc(s.pathway.approvedBy)}. Reorder or remove skills; your decision overrides any agent proposal.</p>
    <ol id="seq" style="padding-left:20px">${seq.map((c, i) => `<li style="margin:4px 0">
      <span>${T.esc(data.skills.find(k => k.code === c)?.title ?? c)}</span>
      <span class="btn-row" style="display:inline-flex;margin-left:6px">
        <button class="btn small ghost" data-up="${i}" aria-label="Move up">↑</button>
        <button class="btn small ghost" data-down="${i}" aria-label="Move down">↓</button>
        <button class="btn small ghost" data-rm="${i}" aria-label="Remove">✕</button></span></li>`).join('')}</ol>
    <div class="btn-row"><select id="addSkill" style="width:auto">${data.skills.filter(k => !seq.includes(k.code)).map(k => `<option value="${k.code}">${T.esc(k.title)}</option>`).join('')}</select>
      <button class="btn small" id="addBtn">Add skill</button>
      <button class="btn small primary" id="saveSeq">${proposal ? 'Save my version' : 'Save pathway'}</button></div>
    <h3 style="margin-top:16px">Guardian consent</h3>
    <label class="toggle"><input type="checkbox" id="cT" ${s.consentTutoring ? 'checked' : ''}> After-school tutoring</label><br>
    <label class="toggle"><input type="checkbox" id="cX" ${s.consentTranscript ? 'checked' : ''}> Keep redacted transcripts for my review</label>
  `);
  const wire = () => {
    $('dlgBody').querySelectorAll('[data-up]').forEach(b => b.onclick = () => { const i = +b.dataset.up; if (i > 0) [seq[i - 1], seq[i]] = [seq[i], seq[i - 1]]; render(); wire(); });
    $('dlgBody').querySelectorAll('[data-down]').forEach(b => b.onclick = () => { const i = +b.dataset.down; if (i < seq.length - 1) [seq[i + 1], seq[i]] = [seq[i], seq[i + 1]]; render(); wire(); });
    $('dlgBody').querySelectorAll('[data-rm]').forEach(b => b.onclick = () => { seq.splice(+b.dataset.rm, 1); render(); wire(); });
    $('addBtn').onclick = () => { const v = $('addSkill').value; if (v) { seq.push(v); render(); wire(); } };
    $('saveSeq').onclick = async () => {
      try {
        if (proposal) await T.api(`/api/teacher/proposals/${proposal.id}/decision`, { method: 'POST', body: { approve: false, sequence: seq, note: 'teacher modified' } });
        else await T.api(`/api/teacher/students/${s.pseudonym}/pathway`, { method: 'PUT', body: { sequence: seq } });
        $('dlg').close(); T.toast('Pathway saved and audited.'); load();
      } catch (e) { T.toast(e.message); }
    };
    const consent = (purpose, el) => el.onchange = () => T.api(`/api/teacher/students/${s.studentId}/consent`, { method: 'POST', body: { purpose, granted: el.checked } })
      .then(() => T.toast('Consent recorded.')).catch(e => T.toast(e.message));
    consent('tutoring', $('cT')); consent('transcript', $('cX'));
  };
  render(); wire();
}

function live() {
  const token = T.store.get('titlape.token');
  if (!window.EventSource || !token) return;
  const es = new EventSource(`/api/teacher/classes/${classId}/live?access_token=${encodeURIComponent(token)}`);
  es.addEventListener('alert', (e) => {
    const a = JSON.parse(e.data);
    T.toast(`New ${a.severity.toLowerCase()} alert: ${a.title}`);
    load();
  });
  es.onerror = () => { $('live').className = 'chip warn'; $('live').innerHTML = '<span class="dot"></span>Reconnecting'; };
  es.onopen = () => { $('live').className = 'chip indigo'; $('live').innerHTML = '<span class="dot"></span>Live'; };
}

if (me) { load(); live(); }
