// download.js —— 下载管理分区：下载列表 + 已下载表格（搜索已拆为独立的「DLsite 搜索」分区，见 search.js）。
// ========== 下载管理（下载列表 / 已下载 两视图就地切换）==========
let dlExpanded = new Set();
// 展开着的下载源分区（键 = 分区 key，如 pixiv / ehentai）；默认全部折叠，标题行上看汇总
let dlSectionOpen = new Set();
// 下载目录树里被手动折叠的文件夹（键 = 番号 + '\n' + 文件夹相对路径）；默认展开，对齐 WPF
let dlFolderCollapsed = new Set();
let dlView = 'download';   // download | downloaded：两视图就地切换，不算一级导航
// 视图调度：默认下载列表，可就地切到已下载。每次切换先清掉下载轮询定时器，避免重复叠加。
function renderDownloadArea() {
  stopTimers();
  $('count').textContent = '';   // 下载管理两视图都不显示计数行；就地切换不走 selectSection，要自己清
  if (dledObserver) { dledObserver.disconnect(); dledObserver = null; }
  if (dlView === 'downloaded') renderDownloaded();
  else renderDownloadSection();
}
function renderDownloadSection() {
  $('title').textContent = '';
  const host = $('content'); host.innerHTML = '';
  const bar = el('div', 'toolbar');
  const dled = el('button', 'icon-btn', '已下载'); dled.onclick = () => navDl('downloaded');
  const startBtn = el('button', 'icon-btn primary', '开始下载'); startBtn.id = 'engineBtn';
  startBtn.onclick = async () => { const running = startBtn.dataset.running === '1'; await apiPost('/api/engine', { action: running ? 'stop' : 'start' }); loadDownloads(); };
  // 没有「清除已完成」：已入库的作品由服务端（/api/downloads 时）自动移出列表，到「已下载」里看
  const cn = el('button', 'icon-btn', '清除无可用连接'); cn.onclick = async () => { await apiPost('/api/clearnolink'); loadDownloads(); };
  // 清空列表：唯一会删掉等待中任务的批量动作，红色 + 二次确认
  const ca = el('button', 'icon-btn danger', '清空列表');
  ca.onclick = async () => {
    if (!await uiConfirm('确定要清空整个下载列表吗？等待中的任务也会被删除。', { title: '清空下载列表', okText: '清空', danger: true })) return;
    await apiPost('/api/clearall'); loadDownloads();
  };
  // 全部重新解析：所有解析失败分卷重新排队，所有"无可用下载连接"占位重新自动解析
  // busy 标记：请求在途时按住禁用，别让每秒轮询把它又点亮
  const ra = el('button', 'icon-btn', '全部重新解析');
  ra.onclick = async () => { ra.dataset.busy = '1'; ra.disabled = true; try { await apiPost('/api/reparseall'); } finally { ra.dataset.busy = '0'; } loadDownloads(); };
  // debrid-link 使用量卡片：与按钮同一行、靠右；点击查看各网盘流量详情
  const usage = el('div', 'usage-card'); usage.id = 'usage';
  usage.innerHTML = '<div class="ut" id="usageText">debrid-link 使用量 --</div><div class="bar"><i id="usageBar" style="width:0;background:#a78bfa"></i></div>';
  usage.style.cursor = 'pointer'; usage.title = '点击查看各网盘流量详情';
  usage.onclick = showUsageDetail;
  // 可用性由每秒轮询的 /api/downloads 回填（见 loadDownloads），初始先禁用，避免空列表时点了没反应
  cn.id = 'clearNoLinkBtn'; ca.id = 'clearAllBtn'; ra.id = 'reparseAllBtn';
  cn.disabled = ca.disabled = ra.disabled = true;
  // 顺序：视图切换 → 引擎控制（主按钮）→ 修复 → 清理（由轻到重，破坏性最强的清空列表压在最后）→ 用量 / 帮助
  bar.append(dled, startBtn, ra, cn, ca, usage, helpButton());
  host.appendChild(bar);
  host.appendChild(el('div', null)).id = 'dlList';
  loadDownloads(); loadUsage();
  pollTimer = setInterval(loadDownloads, 1000);
  usageTimer = setInterval(loadUsage, 15000);
}
async function loadDownloads() {
  if (section !== 'download' || dlView !== 'download') return;
  let d; try { d = await api('/api/downloads'); } catch (e) { return; }
  const btn = $('engineBtn');
  if (btn) {
    if (d.engine.running) { btn.dataset.running = '1'; if (d.engine.stopRequested) { btn.textContent = '暂停中…'; btn.disabled = true; } else { btn.textContent = '暂停下载'; btn.disabled = false; } }
    else { btn.dataset.running = '0'; btn.textContent = '开始下载'; btn.disabled = false; }
  }
  // 工具栏其余按钮按列表内容灰显（服务端算好，桌面端同源：DownloadListActions.FlagsOf）
  const acts = d.actions || {};
  const setEnabled = (id, on) => { const b = $(id); if (b && b.dataset.busy !== '1') b.disabled = !on; };
  setEnabled('clearNoLinkBtn', acts.canClearNoLink);
  setEnabled('clearAllBtn', acts.canClearAll);
  setEnabled('reparseAllBtn', acts.canReparseAll);
  const list = $('dlList'); if (!list) return;
  // 用户正在该列表内选中文本时跳过本次重建：否则每秒刷新会清掉选区，导致文件名/失败原因无法复制
  const sel = window.getSelection && window.getSelection();
  if (sel && !sel.isCollapsed && sel.anchorNode && list.contains(sel.anchorNode)) return;
  if (!d.groups.length) { list.innerHTML = '<div class="empty">下载列表为空</div>'; return; }
  list.innerHTML = '';
  // 一级：按下载源分区（各源并行下载，见 DownloadLanes），默认折叠，标题行显示该源的下载汇总；
  // 二级：分区内照旧是作品卡 → 目录树
  (d.sections || []).forEach(s => {
    const open = dlSectionOpen.has(s.key);
    const sec = el('div', 'dlsec');
    const head = el('div', 'dlshead');
    head.append(el('span', 'dtoggle', open ? '▾' : '▸'), el('span', 'dlsname', s.label));
    const sum = el('span', 'dlssum');
    const part = (n, text, cls) => { if (n) sum.appendChild(el('span', cls, `${text} ${n}`)); };
    part(s.downloading, '下载中', 'on');
    part(s.waiting, '等待', 'wait');
    part(s.failed, '失败', 'bad');
    sum.appendChild(el('span', null, `已完成 ${s.done}/${s.files}`));
    sum.appendChild(el('span', null, `${s.works} 个作品`));
    head.append(sum, el('span', 'dsp', s.speed || ''));
    head.onclick = () => { if (dlSectionOpen.has(s.key)) dlSectionOpen.delete(s.key); else dlSectionOpen.add(s.key); loadDownloads(); };
    sec.appendChild(head);
    if (open) {
      const body = el('div', 'dlsbody');
      d.groups.filter(g => g.section === s.key).forEach(g => body.appendChild(renderDlGroup(g)));
      sec.appendChild(body);
    }
    list.appendChild(sec);
  });
}
// 下载列表里的一个作品（父行 + 可展开的目录树）
function renderDlGroup(g) {
  const c = el('div', 'dl');
  const row = el('div', 'drow');
  const open = dlExpanded.has(g.id);
  const tog = el('span', 'dtoggle', open ? '▾' : '▸');
  // 显示作品名（过长截断，标题悬浮显示全名）；无名称时回退到番号
  const rj = el('div', 'rj', g.name || g.id || '(未知)'); rj.title = g.name ? `${g.name}（${g.id}）` : (g.id || '');
  const toggle = () => { if (dlExpanded.has(g.id)) dlExpanded.delete(g.id); else dlExpanded.add(g.id); loadDownloads(); };
  tog.onclick = toggle; rj.onclick = toggle;
  // 进度条恒为蓝色（对齐 WPF）；状态用文字颜色区分，不给进度条上色
  const prog = el('div', 'dprog'); const pi = el('i'); pi.style.width = g.pct + '%';
  prog.appendChild(pi); prog.appendChild(el('span', 'dpct', g.pct + '%'));
  const sp = el('div', 'dsp', g.speed || '');
  const stt = el('div', 'dst', g.statusText); stt.style.color = g.color; stt.title = g.statusText;
  row.append(tog, rj, prog, stt, sp);
  const act = el('div', 'dacts');
  if (g.canOpen) { const b = el('button', 'mini', '跳转作品'); b.onclick = () => gotoWork(g.id); act.appendChild(b); }
  if (g.canResume) { const b = el('button', 'mini', '下载'); b.onclick = async () => { await apiPost('/api/resumework', { id: g.id }); loadDownloads(); }; act.appendChild(b); }
  if (g.canPause) { const b = el('button', 'mini', '停止'); b.onclick = async () => { await apiPost('/api/pausework', { id: g.id }); loadDownloads(); }; act.appendChild(b); }
  if (g.canReparse) {
    const rp = el('button', 'mini', '重新解析'); rp.onclick = async () => { await apiPost('/api/reparse', { id: g.id }); loadDownloads(); };
    const rs = el('button', 'mini', '重新搜索'); rs.onclick = async () => { if (await uiConfirm(`将删除 ${g.id} 已下载的分卷与文件夹，并重新搜索。是否继续？`, { danger: true })) { await apiPost('/api/research', { id: g.id }); gotoSearch(g.id); } };
    act.append(rp, rs);
  }
  if (g.canDelete) { const b = el('button', 'mini danger', '删除'); b.onclick = async () => { if (await uiConfirm(`将从下载列表删除 ${g.id}，并删除其作品记录与下载缓存。是否继续？`, { danger: true })) { await apiPost('/api/deletework', { id: g.id }); loadDownloads(); } }; act.appendChild(b); }
  if (act.children.length) row.appendChild(act);
  c.appendChild(row);
  const ch = el('div', 'children' + (open ? ' open' : ''));
  if (g.children && g.children.length) { const tree = el('div', 'dtree'); renderDlTree(g.id, buildDlTree(g.children), tree); ch.appendChild(tree); }
  c.appendChild(ch);
  return c;
}
// 把扁平分卷列表按各文件的 relPath（含子目录）重建为文件夹/文件目录树（镜像 WPF RebuildTree）
function buildDlTree(children) {
  const root = { entries: [], _dirs: {} };
  children.forEach(f => {
    const parts = (f.relPath || f.fileName || '').split('/').filter(Boolean);
    let node = root, path = '';
    for (let i = 0; i < parts.length - 1; i++) {   // 末段为文件名，前面均为目录
      path = path ? path + '/' + parts[i] : parts[i];
      let dir = node._dirs[parts[i]];
      if (!dir) { dir = { entries: [], _dirs: {}, name: parts[i], path }; node._dirs[parts[i]] = dir; node.entries.push({ dir }); }
      node = dir;
    }
    node.entries.push({ file: f });
  });
  return root;
}
function renderDlTree(id, node, host) {
  node.entries.forEach(e => {
    if (e.dir) {
      const d = e.dir, key = id + '\n' + d.path, collapsed = dlFolderCollapsed.has(key);
      const head = el('div', 'dfhead');
      const arrow = el('span', 'arrow', collapsed ? '▸' : '▾');
      head.append(arrow, el('span', 'fname', d.name), el('span', 'fcnt', `${d.entries.length} 项`));
      const body = el('div', 'dfbody'); body.style.display = collapsed ? 'none' : '';
      head.onclick = () => {
        const c = dlFolderCollapsed.has(key);
        if (c) dlFolderCollapsed.delete(key); else dlFolderCollapsed.add(key);
        arrow.textContent = c ? '▾' : '▸'; body.style.display = c ? '' : 'none';
      };
      renderDlTree(id, d, body);
      host.append(head, body);
    } else {
      const f = e.file;
      const fr = el('div', 'dfile');
      fr.appendChild(el('div', 'fn', f.fileName));
      const bar = el('div', 'fbar'); const i = el('i'); i.style.width = f.pct + '%'; bar.appendChild(i); fr.appendChild(bar);
      fr.appendChild(el('div', 'fsp', f.speed || ''));
      const st = el('div', 'fst', f.statusText); st.style.color = f.color; fr.appendChild(st);
      if (f.url) {
        const cp = el('button', 'mini', '复制链接');
        cp.onclick = async () => {
          const ok = await copyText(f.url);
          const orig = '复制链接';
          cp.textContent = ok ? '已复制' : '复制失败';
          setTimeout(() => { cp.textContent = orig; }, 1200);
        };
        fr.appendChild(cp);
      }
      host.appendChild(fr);
      if (f.errorReason) host.appendChild(el('div', 'ferr', f.errorReason));
    }
  });
}
let lastUsage = null;   // 最近一次 /api/usage 结果，供点击卡片弹出详情用
async function loadUsage() {
  if (section !== 'download' || dlView !== 'download') return;
  let d; try { d = await api('/api/usage'); } catch (e) { return; }
  lastUsage = d;
  const t = $('usageText'), b = $('usageBar'); if (!t || !b) return;
  if (d.percent == null) { t.textContent = 'debrid-link 使用量 --'; b.style.width = '0'; return; }
  b.style.width = d.percent + '%';
  t.textContent = 'debrid-link 使用量' + (d.resetText ? ` · ${d.resetText} 后重置` : '');
}
// 点击顶部使用量卡片：列出各网盘用量并标出流量已用尽的网盘
function showUsageDetail() {
  const d = lastUsage;
  const lines = [];
  if (d && d.percent != null) lines.push(`总用量 ${d.percent}%` + (d.resetText ? `（${d.resetText} 后重置）` : ''));
  const hosts = (d && d.hosts) || [];
  if (hosts.length) {
    lines.push('', '各网盘用量：');
    hosts.slice().sort((a, b) => b.percent - a.percent).forEach(h => lines.push(`    ${h.host}  ${h.percent}%`));
  }
  const exhausted = (d && d.exhausted) || [];
  if ((d && d.accountFull) || exhausted.length) {
    lines.push('', '流量已用尽的网盘：');
    if (d.accountFull) lines.push('    账户总流量（所有网盘）');
    exhausted.forEach(h => lines.push('    ' + h));
  } else if (!hosts.length) {
    lines.push('', '暂无网盘流量用尽');
  }
  uiAlert(lines.join('\n') || 'debrid-link 使用量 --', 'debrid-link 流量详情');
}

// ========== 已下载（镜像 WPF DownloadedPage：可搜索/状态筛选/列排序/标记已品悦）==========
// 状态值是数据库原值（筛选/比较用），显示文案另映射：已品悦 = 已入媒体库，即下载流程的终点，本页显示为「下载完成」；
// 已下载 = 文件下好了但还没进媒体库（解压/移动未完成），显示为「待入库」
const DLED_STATES = [['', '全部'], ['下载中', '下载中'], ['已下载', '待入库'], ['已品悦', '下载完成']];
const DLED_STATE_TEXT = { '已品悦': '下载完成', '已下载': '待入库' };
// 第一列不止 RJ 号（还有 FB/FT/EH/PX 等来源的作品号），统称「下载ID」
const DLED_COLS = [['id', '下载ID'], ['name', '作品名称'], ['maker', '社团'], ['type', '类型'], ['state', '状态'], ['downTime', '下载时间']];
const DLED_STATE_COLOR = { '已品悦': '#4ade80', '已下载': '#60a5fa', '下载中': '#facc15' };
const DLED_PAGE = 100;   // 每批渲染的行数：全量几千行一次性建 DOM 会卡住页面，滚到底再补下一批
let dledAll = [], dledKw = '', dledState = '', dledSort = null;   // 默认按服务端顺序（下载时间倒序）
let dledRows = [], dledShown = 0, dledObserver = null;
function renderDownloaded() {
  $('title').textContent = '';
  const host = $('content'); host.innerHTML = '';
  const bar = el('div', 'toolbar');
  const back = el('button', 'icon-btn', '← 下载'); back.onclick = () => navDl('download');
  const refresh = el('button', 'icon-btn', '刷新'); refresh.onclick = loadDownloaded;
  const search = el('input'); search.className = 'grow'; search.placeholder = '搜索 下载ID / 作品名 / 社团'; search.value = dledKw;
  const filter = el('select');
  DLED_STATES.forEach(([v, t]) => { const o = el('option', null, t); o.value = v; filter.appendChild(o); });
  filter.value = dledState;
  let deb = null;
  search.oninput = () => { clearTimeout(deb); deb = setTimeout(() => { dledKw = search.value.trim(); drawDownloaded(); }, 250); };
  filter.onchange = () => { dledState = filter.value; drawDownloaded(); };
  bar.append(back, refresh, search, filter, helpButton());
  host.appendChild(bar);
  const wrap = el('div'); wrap.id = 'dledWrap'; host.appendChild(wrap);
  loadDownloaded();
}
async function loadDownloaded() {
  if (section !== 'download' || dlView !== 'downloaded') return;
  let d; try { d = await api('/api/downloaded'); } catch (e) { return; }
  dledAll = d.works || [];
  drawDownloaded();
}
function drawDownloaded() {
  const wrap = $('dledWrap'); if (!wrap) return;
  const kw = dledKw.toLowerCase();
  let rows = dledAll.filter(w => (!dledState || w.state === dledState) &&
    (!kw || `${w.id} ${w.name} ${w.maker}`.toLowerCase().includes(kw)));
  if (dledSort) {
    const { prop, dir } = dledSort, mul = dir === 'asc' ? 1 : -1;
    const key = prop === 'state' ? (w => DLED_STATE_TEXT[w.state] || w.state || '') : (w => w[prop] || '');
    // Intl.Collator 比逐次 localeCompare 快一个数量级（后者每次都要重建比较规则）
    const coll = new Intl.Collator(undefined, { sensitivity: 'base' });
    rows = rows.slice().sort((a, b) => coll.compare(key(a), key(b)) * mul);
  }
  dledRows = rows; dledShown = 0;
  if (dledObserver) { dledObserver.disconnect(); dledObserver = null; }
  wrap.innerHTML = '';
  if (!rows.length) { wrap.appendChild(el('div', 'empty', '没有内容')); return; }
  const table = el('table', 'dt'); table.id = 'dledTable';
  const thead = el('tr');
  DLED_COLS.forEach(([prop, label]) => {
    const arrow = dledSort && dledSort.prop === prop ? (dledSort.dir === 'asc' ? ' ▲' : ' ▼') : '';
    const th = el('th', (prop === 'maker' || prop === 'type' || prop === 'downTime') ? 'hide-sm' : null, label + arrow);
    th.onclick = () => { dledSort = { prop, dir: dledSort && dledSort.prop === prop && dledSort.dir === 'asc' ? 'desc' : 'asc' }; drawDownloaded(); };
    thead.appendChild(th);
  });
  thead.appendChild(el('th', null, ''));
  table.appendChild(thead);
  // 行内按钮走事件委托：一张表一个监听，不给几千个按钮各挂一个闭包
  table.onclick = async (ev) => {
    const b = ev.target.closest('button[data-act]'); if (!b) return;
    const id = b.closest('tr').dataset.id;
    if (b.dataset.act === 'go') gotoWork(id);
    else if (b.dataset.act === 'mark') { await apiPost('/api/mark', { id }); loadDownloaded(); }
  };
  wrap.appendChild(table);
  // 末尾哨兵：滚到距底 600px 内就补下一批
  const sentinel = el('div'); sentinel.style.height = '1px'; wrap.appendChild(sentinel);
  dledMore();
  dledObserver = new IntersectionObserver(es => { if (es.some(e => e.isIntersecting)) dledMore(); }, { rootMargin: '600px' });
  dledObserver.observe(sentinel);
}
// 追加下一批行（DocumentFragment 一次性插入，只触发一次排版）
function dledMore() {
  const table = $('dledTable'); if (!table || dledShown >= dledRows.length) return;
  const frag = document.createDocumentFragment();
  for (const w of dledRows.slice(dledShown, dledShown + DLED_PAGE)) {
    const tr = el('tr'); tr.dataset.id = w.id;
    tr.appendChild(el('td', null, w.id));
    tr.appendChild(el('td', null, w.name));
    tr.appendChild(el('td', 'hide-sm', w.maker));
    tr.appendChild(el('td', 'hide-sm', w.type));
    const stTd = el('td'); const pill = el('span', 'pill', DLED_STATE_TEXT[w.state] || w.state);
    pill.style.color = DLED_STATE_COLOR[w.state] || 'var(--muted)'; stTd.appendChild(pill); tr.appendChild(stTd);
    tr.appendChild(el('td', 'hide-sm', w.downTime));
    const actTd = el('td');
    const go = el('button', 'mini', '跳转作品'); go.dataset.act = 'go'; actTd.appendChild(go);
    // 仅「待入库」（库内值 已下载）可手动标记为下载完成（对齐 WPF 右键菜单）
    if (w.state === '已下载') { const mk = el('button', 'mini', '标记为下载完成'); mk.dataset.act = 'mark'; actTd.appendChild(mk); }
    tr.appendChild(actTd);
    frag.appendChild(tr);
  }
  table.appendChild(frag);
  dledShown = Math.min(dledShown + DLED_PAGE, dledRows.length);
}
