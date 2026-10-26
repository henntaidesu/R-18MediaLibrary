// help.js —— 使用帮助：各页面的「?」按钮打开右侧帮助面板，显示使用手册里对应的章节。
// 手册正文由服务端 /api/help 按章节拆好并转成 HTML（与桌面端共用 HelpManual 的解析器），这里只负责显示与跳转。
// 章节号即手册的「## N.」编号，三种语言一致；带小节号（如 '11.5'）时打开该章并滚到那一节。

// 当前页面对应的手册章节：? 按钮不传 topic 时按所在分区 / 层级 / 搜索来源推断
function helpTopic() {
  if (section === 'search') return { dlsite: '7', fanbox: '9', fanboxcc: '9', fantia: '9', ehentai: '10.1', pixiv: '10.2' }[srcKind(searchSrc)] || '7';
  if (section === 'download') return dlView === 'downloaded' ? '11.5' : '11';
  if (section === 'watch') return '12';
  if (section === 'accounts') return '13';
  if (section === 'settings') return '14';
  const st = stack.length ? stack[stack.length - 1] : null;
  const v = st ? st.view : '';
  if (v === 'detail') return '5';
  if (v === 'files') return '6';
  if (section === 'medialib') return v === 'libs' ? '4' : '4.1';
  return '4.3';   // 作品标签 / 作品社团 / 作品形式 / 我的收藏
}

// 「?」按钮：topic 缺省时点下去再按当前页面推断（来源 / 视图可能在建好按钮之后才切换）
function helpButton(topic, cls) {
  const b = el('button', 'icon-btn help-btn' + (cls ? ' ' + cls : ''), '?');
  b.title = '使用帮助'; b.setAttribute('aria-label', '使用帮助');
  b.onclick = (ev) => { ev.stopPropagation(); openHelp(topic || helpTopic()); };
  return b;
}

const HELP = { data: {}, lang: '', ch: '', isOpen: false };
function helpLangPref() { try { return localStorage.getItem('helpLang') || ''; } catch (e) { return ''; } }

async function helpLoad(lang) {
  const key = lang || '';
  if (!HELP.data[key]) {
    const d = await api('/api/help' + (lang ? '?lang=' + enc(lang) : ''));
    HELP.data[key] = d; HELP.data[d.lang] = d;
  }
  return HELP.data[key];
}

async function openHelp(topic) {
  const panel = $('helpPanel');
  if (!HELP.isOpen) {
    HELP.isOpen = true;
    panel.classList.add('show'); $('helpBackdrop').classList.add('show');
    // 同看图：压一层历史，系统返回手势只关掉帮助面板，不离开当前页
    history.pushState({ nav: true, help: true }, '');
  }
  $('helpBody').innerHTML = '<div class="empty"><span class="spin"></span> 加载中…</div>';
  let d;
  try { d = await helpLoad(HELP.lang || helpLangPref()); } catch (e) { $('helpBody').innerHTML = '<div class="empty">帮助加载失败</div>'; return; }
  HELP.lang = d.lang;
  const ch = String(topic || '').split('.')[0];
  showHelpChapter(d.chapters.some(c => c.no === ch) ? ch : d.chapters.length ? d.chapters[0].no : '', topic);
}

function showHelpChapter(no, topic) {
  const d = HELP.data[HELP.lang];
  if (!d) return;
  const idx = d.chapters.findIndex(c => c.no === no);
  if (idx < 0) return;
  HELP.ch = no;
  const sel = $('helpChapter'); sel.innerHTML = '';
  d.chapters.forEach(c => { const o = el('option', null, `${c.no}. ${c.title}`); o.value = c.no; sel.appendChild(o); });
  sel.value = no;
  const lang = $('helpLang'); lang.innerHTML = '';
  d.languages.forEach(l => { const o = el('option', null, l.name); o.value = l.code; lang.appendChild(o); });
  lang.value = d.lang;

  const c = d.chapters[idx];
  const body = $('helpBody'); body.innerHTML = '';
  body.appendChild(el('h2', null, `${c.no}. ${c.title}`));
  const art = el('div', 'help-doc'); art.innerHTML = c.html; body.appendChild(art);
  // 正文里的「第 N 节」：就地换章
  art.querySelectorAll('a.hxref').forEach(a => { a.onclick = (ev) => { ev.preventDefault(); showHelpChapter(a.dataset.ch); }; });
  $('helpPrev').disabled = idx === 0;
  $('helpNext').disabled = idx === d.chapters.length - 1;
  body.scrollTop = 0;
  // 带小节号：滚到那一节并短暂高亮
  if (topic && String(topic).includes('.')) {
    const h = art.querySelector(`[data-sec="${CSS.escape(String(topic))}"]`);
    if (h) { h.scrollIntoView({ block: 'start' }); h.classList.add('flash'); setTimeout(() => h.classList.remove('flash'), 1600); }
  }
}

function helpStep(delta) {
  const d = HELP.data[HELP.lang];
  if (!d) return;
  const i = d.chapters.findIndex(c => c.no === HELP.ch) + delta;
  if (i >= 0 && i < d.chapters.length) showHelpChapter(d.chapters[i].no);
}

async function setHelpLang(lang) {
  try { localStorage.setItem('helpLang', lang); } catch (e) { }
  const ch = HELP.ch;
  try { HELP.lang = (await helpLoad(lang)).lang; } catch (e) { return; }
  showHelpChapter(ch);
}

// ✕ / 点遮罩 / Esc 都经 history.back() 关闭，由 popstate 再调 hideHelp()，历史栈里不残留多出的一层
function closeHelp() {
  if (!HELP.isOpen) return;
  if (history.state && history.state.help) history.back(); else hideHelp();
}
function hideHelp() {
  HELP.isOpen = false;
  $('helpPanel').classList.remove('show'); $('helpBackdrop').classList.remove('show');
}

$('helpChapter').onchange = () => showHelpChapter($('helpChapter').value);
$('helpLang').onchange = () => setHelpLang($('helpLang').value);
$('helpPrev').onclick = () => helpStep(-1);
$('helpNext').onclick = () => helpStep(1);
$('helpBtn').onclick = () => openHelp(helpTopic());
document.addEventListener('keydown', e => { if (HELP.isOpen && e.key === 'Escape' && !$('modal').classList.contains('show')) closeHelp(); });

// 分区标题 + 右端的 ?（系统设置各分区各自对应手册的不同章节）
function helpHeading(title, topic) {
  const h = el('h3', null, title);
  h.appendChild(helpButton(topic));
  return h;
}
