const me = T.requireRole('Student');
T.netChip(document.getElementById('net'));
document.getElementById('logout').onclick = T.logout;

const $ = (id) => document.getElementById(id);
let session = null;
let shownAt = Date.now();
let busy = false;
let chosenLang = null; // null → learner's home language from the roster

if (matchMedia('(max-width: 820px)').matches) $('layout').style.gridTemplateColumns = '1fr';

function add(kind, text, action) {
  const el = document.createElement('div');
  el.className = `msg ${kind} ${action || ''}`;
  el.textContent = text;
  $('chat').appendChild(el);
  $('chat').scrollTop = $('chat').scrollHeight;
  return el;
}

function setItem(skillTitle, prompt, mastery) {
  $('skill').textContent = skillTitle;
  $('question').textContent = prompt;
  if (mastery != null) { $('mbar').style.width = `${Math.round(mastery * 100)}%`; $('mpct').textContent = T.pct(mastery); }
  shownAt = Date.now();
}

async function start() {
  $('chat').innerHTML = '';
  try {
    session = await T.api('/api/student/sessions', { method: 'POST', body: { language: chosenLang } });
    $('lang').value = session.language;
    setItem(session.skillTitle, session.itemPrompt, session.mastery);
    add('tutor', session.tutorMessage);
    $('text').focus();
  } catch (e) {
    add('tutor', e.message, 'Escalate');
  }
  progress();
}

async function send(text) {
  if (!session || busy || !text.trim()) return;
  busy = true; $('send').disabled = true;
  add('me', text);
  const bubble = add('tutor', '…');
  const seconds = (Date.now() - shownAt) / 1000;
  try {
    const res = await T.api(`/api/student/sessions/${session.sessionId}/turns/stream`, { method: 'POST', body: { text, responseSeconds: seconds }, raw: true });
    if (!res.ok) throw new Error((await res.json()).detail || res.statusText);
    let streamed = '';
    await T.readSse(res, (ev, data) => {
      if (ev === 'token') { streamed += data; bubble.textContent = streamed; $('chat').scrollTop = $('chat').scrollHeight; }
      if (ev === 'done') {
        bubble.className = `msg tutor ${data.action}`;
        if (data.newItem) setItem(data.skillTitle, data.itemPrompt, data.masteryAfter);
        else if (data.masteryAfter != null) setItem(data.skillTitle, data.itemPrompt, data.masteryAfter);
        progress();
      }
    });
  } catch (e) {
    bubble.textContent = navigator.onLine ? `Sorry — ${e.message}` : 'You are offline. Your hub will reconnect soon.';
  } finally {
    busy = false; $('send').disabled = false; $('text').focus();
  }
}

async function progress() {
  try {
    const p = await T.api('/api/student/progress');
    $('hello').textContent = `${p.name}'s progress`;
    $('progress').innerHTML = p.skills.map(s => `
      <div style="margin:10px 0">
        <div style="display:flex;justify-content:space-between;gap:8px"><span>${T.esc(s.title)}</span>
          <span class="faint">${s.mastery.attempts ? T.pct(s.mastery.pKnown) : 'not started'}</span></div>
        <div class="bar-track"><div class="bar-fill" style="width:${Math.round(s.mastery.pKnown * 100)}%;${s.mastery.mastered ? '' : 'background:var(--indigo)'}"></div></div>
      </div>`).join('') + (p.mastered.length ? `<p class="chip good" style="margin-top:8px"><span class="dot"></span>${p.mastered.length} skill(s) mastered</p>` : '');
  } catch { /* offline: keep last view */ }
}

$('form').onsubmit = (e) => { e.preventDefault(); const t = $('text').value; $('text').value = ''; send(t); };
$('stuck').onclick = () => send("I don't understand");
$('new').onclick = start;
$('lang').onchange = async () => {
  chosenLang = $('lang').value;
  if (session) await T.api(`/api/student/sessions/${session.sessionId}/language`, { method: 'PUT', body: { language: $('lang').value } }).catch(() => { });
  T.toast(`Hints will now include ${$('lang').value} words.`);
};

if (me) start();
