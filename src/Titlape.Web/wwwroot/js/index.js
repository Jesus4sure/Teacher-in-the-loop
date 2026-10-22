T.netChip(document.getElementById('net'));
const go = async (id, page) => { try { await T.login(id); location.href = page; } catch (e) { T.toast(e.message); } };
T.api('/api/demo/users').then(u => {
  document.getElementById('students').innerHTML = u.students.map(s =>
    `<button class="btn" data-id="${T.esc(s.id)}"><span>${T.esc(s.name)}</span><span class="faint">${T.esc(s.language)} · ${T.esc(s.school.split(' ')[0])}</span></button>`).join('');
  document.getElementById('teachers').innerHTML = u.teachers.map(t =>
    `<button class="btn" data-id="${T.esc(t.id)}"><span>${T.esc(t.name)}</span><span class="faint">${T.esc(t.school)}</span></button>`).join('');
  document.querySelectorAll('#students button').forEach(b => b.onclick = () => go(b.dataset.id, '/student.html'));
  document.querySelectorAll('#teachers button').forEach(b => b.onclick = () => go(b.dataset.id, '/teacher.html'));
}).catch(e => T.toast('Could not reach the hub: ' + e.message));
document.getElementById('admin').onclick = () => go('admin', '/ops.html');
