// core.js —— 框架层：DOM 工具 / 页内弹窗 / API / 登录 / 导航路由 / 卡片渲染基元。最先加载。
const $ = (id) => document.getElementById(id);
const el = (tag, cls, txt) => { const e = document.createElement(tag); if (cls) e.className = cls; if (txt != null) e.textContent = txt; if (tag === 'input') { e.autocomplete = 'off'; e.setAttribute('data-lpignore', 'true'); } return e; };
const enc = encodeURIComponent;

// 复制文本到剪贴板，返回 Promise<boolean> 表示是否成功。
// 仅在安全上下文（https/localhost）用 navigator.clipboard；局域网 http 下该 API 被禁用（甚至 writeText 会挂起/拒绝），
// 故此时直接退回 execCommand 方案（配合 focus + setSelectionRange 才能在多数浏览器/移动端可靠选中）。
async function copyText(t) {
  if (window.isSecureContext && navigator.clipboard && navigator.clipboard.writeText) {
    try { await navigator.clipboard.writeText(t); return true; } catch (e) { /* 退回 execCommand */ }
  }
  return fallbackCopy(t);
}
function fallbackCopy(t) {
  const ta = document.createElement('textarea');
  ta.value = t;
  ta.style.position = 'fixed'; ta.style.top = '0'; ta.style.left = '0'; ta.style.opacity = '0';
  ta.setAttribute('readonly', '');
  document.body.appendChild(ta);
  ta.focus(); ta.select();
  try { ta.setSelectionRange(0, ta.value.length); } catch (e) { }
  let ok = false;
  try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
  document.body.removeChild(ta);
  return ok;
}

// ---------- 页内弹窗（替代浏览器原生 alert/confirm/prompt，避免使用浏览器顶部弹窗）----------
// 返回 Promise：alert→resolve(undefined)；confirm→resolve(true/false)；prompt→resolve(字符串或 null)
let _modalCancel = null;   // 遮罩点击时用于取消当前弹窗的回调（保证 Promise 一定被 resolve，不悬挂）
function uiModal({ title, message, input, defaultValue, okText = '确定', cancelText, danger }) {
  return new Promise(resolve => {
    const ov = $('modal'), box = $('modalBox');
    box.innerHTML = '';
    const done = (v) => { _modalCancel = null; ov.classList.remove('show'); document.removeEventListener('keydown', onKey); resolve(v); };
    if (title) box.appendChild(el('h3', null, title));
    if (message != null && message !== '') box.appendChild(el('div', 'modal-msg', message));
    let field = null;
    if (input) {
      field = el('input', 'modal-input'); field.value = defaultValue || '';
      field.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); done(field.value); } });
      box.appendChild(field);
    }
    const btns = el('div', 'modal-btns');
    if (cancelText) { const c = el('button', 'icon-btn', cancelText); c.onclick = () => done(input ? null : false); btns.appendChild(c); }
    const ok = el('button', 'icon-btn primary' + (danger ? ' danger' : ''), okText);
    ok.onclick = () => done(input ? field.value : true);
    btns.appendChild(ok); box.appendChild(btns);
    // Esc 取消 / Enter 确认（有输入框时 Enter 由输入框处理）
    function onKey(e) {
      if (e.key === 'Escape') { e.preventDefault(); done(input ? null : false); }
      else if (e.key === 'Enter' && !input) { e.preventDefault(); done(true); }
    }
    _modalCancel = () => done(input ? null : false);
    document.addEventListener('keydown', onKey);
    ov.classList.add('show');
    (field || ok).focus();
  });
}
function uiAlert(message, title) { return uiModal({ title: title || '提示', message, okText: '确定' }); }
function uiConfirm(message, opts = {}) { return uiModal({ title: opts.title || '确认', message, okText: opts.okText || '确定', cancelText: opts.cancelText || '取消', danger: opts.danger }); }
function uiPrompt(message, defaultValue, title) { return uiModal({ title: title || '输入', message, input: true, defaultValue, cancelText: '取消' }); }
$('modal').addEventListener('click', e => { if (e.target.id === 'modal' && _modalCancel) _modalCancel(); });

const SECTIONS = [
  { key: 'medialib', label: '媒体库', root: 'libs',     icon: '📚' },
  { key: 'download', label: '下载管理',                  icon: '⬇' },
  { key: 'search',   label: '作品搜索',                    icon: '🔍' },
  { key: 'tag',      label: '作品标签', root: 'genres',   icon: '🏷' },
  { key: 'maker',    label: '作品社团', root: 'allmakers', icon: '🏢' },
  { key: 'type',     label: '作品形式', root: 'types',   icon: '🎬' },
  { key: 'favorite', label: '我的收藏', root: 'favorites', icon: '♥' },
  { key: 'settings', label: '系统设置',                     icon: '⚙' },
];
const MAKER_SORTS = ['作品数 多→少', '作品数 少→多', '社团名 A→Z'];
const WORK_SORTS  = ['发售日 新→旧', '发售日 旧→新', 'RJ号 新→旧', 'RJ号 旧→新'];
const AGE = { '1': '全年龄', '2': 'R-15', '3': 'R-18' };
const VIDEO_EXTS = ['.mp4', '.mkv', '.avi', '.wmv', '.mov', '.flv', '.webm', '.m4v', '.ts', '.mpg', '.mpeg'];
const AUDIO_EXTS = ['.mp3', '.wav', '.flac', '.m4a', '.aac', '.ogg', '.wma', '.opus'];
const IMG_EXTS = ['.jpg', '.jpeg', '.png', '.gif', '.webp'];
const fileUrl = (id, rel) => `/api/file?id=${enc(id)}&path=${enc(rel)}`;
// 缩略图 URL：服务端把图片压到 128KB 以内再发，用于缩略图网格（点开大图仍用 fileUrl 原图）
const thumbUrl = (id, rel) => `${fileUrl(id, rel)}&thumb=1`;

let section = 'medialib';
let stack = [];            // 卡片区导航栈
let curItems = [];         // 卡片区原始数据（本地搜索过滤）
let makerSort = 0, workSort = 0;
let pollTimer = null, usageTimer = null;

async function api(path) {
  const r = await fetch(path, { headers: { 'Accept': 'application/json' } });
  if (r.status === 401) { showLogin(false); throw new Error('unauth'); }
  return r.json();
}
async function apiPost(path, body) {
  const r = await fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body || {}) });
  if (r.status === 401) { showLogin(false); throw new Error('unauth'); }
  return r.json();
}

// ---------- 登录 ----------
// 登录方式由 /api/state 决定：设了访问密码才画密码框，程序开了门户单点登录才画「门户登录」
let loginState = { password: true, sso: false };
let currentUser = null;   // 单点登录进来的用户 { username, display }；密码登录为 null
function showLogin(initial) {
  const st = loginState;
  $('pwPart').hidden = !st.password;
  $('ssoBtn').hidden = !st.sso;
  $('ssoOr').hidden = !(st.password && st.sso);
  $('loginTitle').textContent = st.password ? '需要访问密码' : '请登录';
  $('login').classList.add('show');
  if (!initial) $('loginErr').textContent = '会话已失效，请重新登录';
  // 门户那一路出错时后端 303 回 ?sso_error=...；显示后从地址栏抹掉，免得刷新又弹一次
  const q = new URLSearchParams(location.search);
  if (q.get('sso_error')) {
    $('loginErr').textContent = '门户登录失败：' + q.get('sso_error');
    q.delete('sso_error');
    const rest = q.toString();
    history.replaceState(null, '', location.pathname + (rest ? '?' + rest : '') + location.hash);
  }
  if (st.password) $('pw').focus();
}
// 整页跳转而非 fetch：授权要浏览器自己走完门户那一趟。地址用相对路径、base 取页面所在目录，
// 经门户代理打开时（页面在 /api/proxy/<卡片>/ 底下）回调才会落回代理底下
function ssoLogin() {
  const base = location.origin + location.pathname.replace(/[^/]*$/, '');
  location.href = 'api/sso/login?base=' + encodeURIComponent(base);
}
async function doLogout() {
  try { await fetch('api/logout', { method: 'POST' }); } catch (e) {}
  location.reload();
}
async function doLogin() {
  $('loginErr').textContent = '';
  const r = await apiPost('/api/login', { password: $('pw').value });
  if (r.ok) { $('login').classList.remove('show'); $('pw').value = ''; selectSection('medialib'); }
  else $('loginErr').textContent = '密码错误';
}
// Enter 提交由登录表单的 onsubmit 处理，无需额外监听（避免重复提交）

// ---------- 左侧导航（桌面常驻 / 移动抽屉） ----------
function buildTabs() {
  const box = $('nav'); box.innerHTML = '';
  box.appendChild(el('div', 'brand', 'R18媒体库'));
  SECTIONS.forEach(s => {
    const b = el('div', 'navitem' + (s.key === section ? ' active' : ''));
    b.appendChild(el('span', null, s.label));
    b.onclick = () => selectSection(s.key);
    box.appendChild(b);
  });
  if (currentUser) {
    const u = el('div', 'navuser');
    const name = el('span', null, '👤 ' + (currentUser.display || currentUser.username));
    name.title = currentUser.username;
    const out = el('a', null, '退出');
    out.onclick = doLogout;
    u.appendChild(name); u.appendChild(out);
    box.appendChild(u);
  }
}
// 移动端抽屉开关
function toggleDrawer() {
  const open = $('nav').classList.toggle('open');
  $('drawerBackdrop').classList.toggle('show', open);
}
function closeDrawer() {
  $('nav').classList.remove('open');
  $('drawerBackdrop').classList.remove('show');
}
// 设备/窗口决定布局：iOS/Android 手机或窄窗 → 抽屉；Windows/macOS/iPadOS/平板 → 侧栏常驻
function applyLayout() {
  const ua = navigator.userAgent;
  const phone = /iPhone|iPod|Windows Phone/.test(ua) || (/Android/.test(ua) && /Mobile/.test(ua));
  const mobile = phone || window.innerWidth <= 700;
  document.body.classList.toggle('layout-mobile', mobile);
  document.body.classList.toggle('layout-desktop', !mobile);
  if (!mobile) closeDrawer();
}
window.addEventListener('resize', applyLayout);

// ---------- 顶栏 + 页内工具栏常驻页顶 ----------
// 顶栏高度不固定（窄屏换行、详情页多一排操作按钮），页内工具栏（一张悬浮卡片）又要正好停在它下面，
// 所以只能实测：按文档顺序取当前可见的工具栏，逐条把 top 设成「顶栏高 + 空隙 + 之前各条高度之和」，
// 最后把总高写进 --bars-h 供表头一类的二级吸顶元素使用（已下载表的 .dt th）。
// 一页可能叠两条（搜索栏 + 选择条），故再按位置打 stack-top/mid/bot，让整叠拼成同一张卡（样式见 app.css）。
// 写入前先比对旧值：值没变就不写（class 同理），否则下面的 MutationObserver 会被自己的写入再次唤醒、来回打转。
function syncStickyBars() {
  const root = document.documentElement, head = document.querySelector('header');
  const gap = parseFloat(getComputedStyle(root).getPropertyValue('--bar-gap')) || 10;
  let top = head ? Math.round(head.getBoundingClientRect().height) : 0;
  const setVar = (k, v) => { if (root.style.getPropertyValue(k) !== v) root.style.setProperty(k, v); };
  const setCls = (e, c, on) => { if (e.classList.contains(c) !== on) e.classList.toggle(c, on); };
  setVar('--topbar-h', top + 'px');
  const all = Array.from(document.querySelectorAll('#content .toolbar:not(.inline)'));
  // 藏起来的结果面板（切换搜索来源时的另两套）里的工具栏不占位，也不参与拼卡
  const bars = all.filter(b => b.offsetParent);
  all.forEach(b => {
    const i = bars.indexOf(b);
    if (i < 0 && b.style.top) b.style.top = '';
    setCls(b, 'bar-lead', i === 0);                                  // 只有最上面那条负责封住与顶栏之间的空隙
    setCls(b, 'stack-top', i === 0 && bars.length > 1);
    setCls(b, 'stack-mid', i > 0 && i < bars.length - 1);
    setCls(b, 'stack-bot', i > 0 && i === bars.length - 1);
  });
  if (bars.length) top += gap;   // 卡片与顶栏之间的空隙
  bars.forEach(bar => {
    const v = top + 'px';
    if (bar.style.top !== v) bar.style.top = v;
    top += Math.round(bar.getBoundingClientRect().height);
  });
  // --bars-h 就取整叠卡片的下沿（不含下外边距）：已下载表的表头正好贴着卡片底边吸住，
  // 顺带把那道外边距盖上，内容不会从卡片与表头之间的缝里透出来。
  setVar('--bars-h', top + 'px');
}
let _stickyRaf = 0;
function scheduleStickyBars() {
  if (_stickyRaf) return;
  _stickyRaf = requestAnimationFrame(() => { _stickyRaf = 0; syncStickyBars(); });
}
window.addEventListener('resize', scheduleStickyBars);
// 各分区自行重绘内容，这里统一盯住 DOM 变化，省去在每个 render 里逐处调用。
// 每帧最多重算一次；下载列表每秒重建一次表格也只触发一次。
new MutationObserver(scheduleStickyBars).observe(document.body,
  { childList: true, subtree: true, attributes: true, attributeFilter: ['style', 'hidden', 'class'] });

function stopTimers() { if (pollTimer) clearInterval(pollTimer); if (usageTimer) clearInterval(usageTimer); pollTimer = usageTimer = null; }

function selectSection(key, noRender) {
  section = key; stopTimers(); closeVideo(); closeAudio(); closeDrawer();
  if (key === 'download') dlView = 'download';   // 进入分区先归位到下载列表视图，再写基线历史（encodeHash 依赖 dlView）
  const s = SECTIONS.find(x => x.key === key);
  buildTabs();
  $('search').hidden = !s.root;
  $('sort').hidden = true;
  $('libToggle').hidden = true;   // 媒体库的「显示作品/显示社团」只属于卡片区，换分区必须收起（否则会跟到搜索/下载页上）
  $('back').hidden = true;
  $('count').textContent = '';
  $('content').innerHTML = '';
  $('search').value = '';
  $('detailActions').hidden = true; $('detailActions').innerHTML = '';   // 清除上一分区遗留的详情操作按钮
  $('topbar').classList.remove('detail-mode');
  if (s.root) { makerSort = 0; workSort = 0; stack = [{ view: s.root, ctx: {} }]; }
  // 反映到地址栏；切换分区重置历史基线（不累积层级），但 URL 可见当前分区
  history.replaceState({ nav: true, depth: s.root ? 1 : 0 }, '', encodeHash());
  if (s.root) { if (!noRender) render(); }
  else if (key === 'search') renderSearchArea();
  else if (key === 'download') renderDownloadArea();
  else if (key === 'settings') renderSettings();
}

// ---------- URL 路由（可见地址栏路由，非隐藏）----------
// 地址栏哈希反映当前分区 + 卡片区导航路径，可分享/刷新恢复。
// 内存 stack 仍是渲染与滚动恢复的主数据源；哈希仅作 URL 呈现与新开页面时的恢复。
function encodeHash() {
  const s = SECTIONS.find(x => x.key === section);
  if (section === 'search') return encodeSearchHash();       // 作品搜索把来源与查询词编进地址栏
  if (section === 'download') return encodeDlHash();         // 下载管理有两视图，走独立编码
  if (!s || !s.root) return '#/' + section;             // 其它叶子分区（设置）
  const segs = stack.slice(1).map(e => {
    const ctx = { ...e.ctx }; delete ctx.nodes; delete ctx.open;   // nodes 文件树缓存过大、open 详情折叠态是运行时状态，均不入 URL
    const c = Object.keys(ctx).length ? '~' + encodeURIComponent(JSON.stringify(ctx)) : '';
    return e.view + c;
  });
  return '#/' + s.key + (segs.length ? '/' + segs.join('/') : '');
}
// 作品搜索分区：来源 + 查询词纳入地址栏，可刷新/分享恢复。
// 形如 #/search/dlsite、#/search/dlsite/RJ123456、#/search/fanbox/alkaloid404。
// 查询词整段 URI 编码，故 DLsite 链接里的 '/' 不会被误当作层级分隔符。
function encodeSearchHash() {
  const q = ($('sId') && $('sId').value.trim()) || '';
  return '#/search/' + searchSrc + (q ? '/' + encodeURIComponent(q) : '');
}
// 下载管理两视图（下载列表/已下载）纳入地址栏路由。形如 #/download、#/download/downloaded。
function encodeDlHash() {
  return dlView === 'downloaded' ? '#/download/downloaded' : '#/download';
}
// 子视图切换：作为可回退的路由层级压入历史并反映到地址栏（对齐卡片区 pushView）
function navDl(view) {
  dlView = view;
  history.pushState({ nav: true, dl: view }, '', encodeDlHash());
  renderDownloadArea();
}
// 搜索分区内输入查询后刷新地址栏（只 replace，不为每次查询累积历史层级）
function syncSearchHash() {
  if (section === 'search')
    history.replaceState({ nav: true }, '', encodeSearchHash());
}
// 前进/后退（popstate）落在下载管理分区时，按地址栏恢复子视图
function syncDlFromHash() {
  const p = parseHash();
  const dl = (p && p.key === 'download') ? p.dl : 'download';
  if (dl === dlView) return;
  dlView = dl;
  renderDownloadArea();
}
function parseHash() {
  const raw = (location.hash || '').replace(/^#\/?/, '');
  if (!raw) return null;
  const parts = raw.split('/').filter(Boolean);
  const s = SECTIONS.find(x => x.key === parts[0]);
  if (!s) return null;
  if (s.key === 'search') {
    // parts[1]：来源（按 SEARCH_SOURCES 校验，缺省 dlsite）；parts[2..]：查询词（整段 URI 编码）
    const known = typeof SEARCH_SOURCES !== 'undefined' && SEARCH_SOURCES.some(x => x.key === parts[1]);
    const src = known ? parts[1] : 'dlsite';
    let query = '';
    if (parts[2]) { try { query = decodeURIComponent(parts.slice(2).join('/')); } catch (e) { } }
    return { key: s.key, root: false, src, query };
  }
  if (s.key === 'download') {
    // parts[1]：downloaded（缺省 download）
    const dl = parts[1] === 'downloaded' ? 'downloaded' : 'download';
    return { key: s.key, root: false, dl };
  }
  const stk = s.root ? [{ view: s.root, ctx: {} }] : [];
  for (let i = 1; i < parts.length; i++) {
    const t = parts[i].indexOf('~');
    const view = t < 0 ? parts[i] : parts[i].slice(0, t);
    let ctx = {};
    if (t >= 0) { try { ctx = JSON.parse(decodeURIComponent(parts[i].slice(t + 1))); } catch (e) { } }
    stk.push({ view, ctx });
  }
  return { key: s.key, root: !!s.root, stack: stk };
}

// ========== 卡片区（媒体库/标签/作品形式/收藏）==========
// 每深入一级 push 一个历史项：iOS/安卓的"返回"手势触发 popstate 即可逐级回退，并恢复浏览位置
// 跨分区跳到媒体库里的某个作品详情（下载管理的「跳转作品」）：落在媒体库根之上一层，返回即回媒体库首页。
// 不先画媒体库首页——两次 render 并发时先发的那次可能后回来，把详情页盖掉
function gotoWork(id) {
  selectSection('medialib', true);
  pushView('detail', { id });
}
function pushView(view, ctx) {
  if (stack.length) { const cur = stack[stack.length - 1]; cur.scroll = window.scrollY; cur.loaded = _cardLoaded; }
  stack.push({ view, ctx: ctx || {} });
  history.pushState({ nav: true, depth: stack.length }, '', encodeHash());
  render();
}
async function popView() {
  if (stack.length <= 1) return false;
  stack.pop();
  const top = stack[stack.length - 1];
  _restoreLoaded = top.loaded || 0;   // 让懒加载网格先补足之前展开的卡片数，再恢复滚动位置
  await render();
  window.scrollTo(0, top.scroll || 0);   // 返回上一级后恢复之前的浏览位置
  return true;
}
window.addEventListener('popstate', () => {
  if (LB.isOpen) { LB.hide(); return; }   // 看图层先退：返回手势只关看图，不离开当前页
  if (section === 'search') return;   // 搜索分区内不压历史层级（来源/查询词只 replace）
  if (section === 'download') { syncDlFromHash(); return; }
  const s = SECTIONS.find(x => x.key === section);
  if (s && s.root && stack.length > 1) popView();
});
$('back').onclick = () => history.back();   // 走 popstate，与手势行为一致

async function render() {
  const st = stack[stack.length - 1];
  $('back').hidden = stack.length <= 1;
  $('sort').hidden = true;
  $('libToggle').hidden = true;
  $('detailActions').hidden = true; $('detailActions').innerHTML = '';   // 顶栏操作按钮仅作品详情页使用
  $('topbar').classList.remove('detail-mode');   // 详情页专属排布，离开即复原
  $('search').hidden = (st.view === 'detail' || st.view === 'files');   // 作品详情/查看作品页不显示搜索框
  $('content').innerHTML = '';
  $('count').textContent = '';
  try {
    if (st.view === 'libs') await renderLibs();
    else if (st.view === 'libview') await renderLibView(st.ctx);
    else if (st.view === 'genres') await renderGenres();
    else if (st.view === 'types') await renderTypes();
    else if (st.view === 'allmakers') await renderAllMakers();
    else if (st.view === 'favorites') await renderWorks('/api/favorites', '我的收藏', true);
    else if (st.view === 'makers') await renderMakers(st.ctx);
    else if (st.view === 'genreworks') await renderWorks('/api/genreworks?genre=' + enc(st.ctx.genre), st.ctx.label || st.ctx.genre, true);
    else if (st.view === 'works') await renderWorks(worksUrl(st.ctx), st.ctx.label || st.ctx.maker || '未知社团', true, st.ctx);
    else if (st.view === 'filter') await renderWorks(`/api/filter?col=${enc(st.ctx.col)}&val=${enc(st.ctx.val)}`, st.ctx.label, true);
    else if (st.view === 'detail') await renderDetail(st.ctx);
    else if (st.view === 'files') await renderFiles(st.ctx);
  } catch (e) { if (e.message !== 'unauth') $('content').appendChild(el('div', 'empty', '加载失败')); }
}

function setSort(options, current, onChange) {
  const s = $('sort'); s.hidden = false; s.innerHTML = '';
  options.forEach((o, i) => { const op = el('option', null, o); op.value = i; s.appendChild(op); });
  s.value = current; s.onchange = () => onChange(parseInt(s.value));
}

function filtered() { const kw = $('search').value.trim().toLowerCase(); return kw ? curItems.filter(i => i._key.includes(kw)) : curItems; }

let lastMap = null, lastUnit = '';
function drawGroups(items, mapFn, unit) {
  lastMap = mapFn; lastUnit = unit;
  const items2 = filtered();
  const works = items.reduce((s, i) => s + (i.works || i.count || 0), 0);
  $('count').textContent = $('search').value.trim() ? `共 ${items.length} ${unit}，匹配 ${items2.length} 个` : `共 ${items.length} ${unit}，${works} 个作品`;
  const grid = el('div', 'grid groups');
  items2.forEach(i => {
    const m = mapFn(i);
    const c = el('div', 'card group-card');
    // 社团卡一律留出左侧头像位（m.avatar），没有头像的社团那一格就空着——
    // 这样同一行里有头像和没头像的卡片版式一致，名称起始位置对得上。
    // 有图时：图床接管就直取图床，取不到回退源站地址；再取不到就移除图片，但位置仍在
    if (m.avatar) {
      c.classList.add('avatar-slot');   // 切到 2:8 两栏栅格
      if (m.icon) {
        const ic = el('img', 'gicon'); ic.loading = 'lazy'; ic.src = m.icon;
        ic.onerror = m.iconFallback
          ? () => { ic.onerror = () => ic.remove(); ic.src = m.iconFallback; }
          : () => ic.remove();
        c.appendChild(ic);
      }
    }
    // 右侧信息块：名称 + 作品数竖排
    const info = el('div', 'ginfo');
    info.append(el('div', 'gt', m.title), el('div', 'gc', m.caption));
    c.appendChild(info);
    // 右上角改名按钮（只有社团卡给 onEdit）：别让点击冒泡成"进入该分组"
    if (m.onEdit) {
      const eb = el('button', 'gedit', '🖊');
      eb.title = '自定义显示名称';
      eb.onclick = (ev) => { ev.stopPropagation(); m.onEdit(); };
      c.appendChild(eb);
    }
    c.onclick = m.onClick;
    grid.appendChild(c);
  });
  const host = $('content'); host.innerHTML = ''; host.appendChild(items2.length ? grid : el('div', 'empty', '没有内容'));
}
function makeWorkCard(w) {
  const c = el('div', 'card');
  const cov = el('div', 'cover');
  if (w.cover) {
    const img = el('img'); img.loading = 'lazy';
    const local = `/api/cover?id=${enc(w.id)}&thumb=1`;
    // 图床接管该封面时直取图床（不经本服务、不读本机 HDD）；图床取不到再回退本地
    img.src = w.coverUrl || local;
    if (w.coverUrl) img.onerror = () => { img.onerror = null; img.src = local; };
    cov.appendChild(img);
  }
  cov.appendChild(el('div', 'badge rj', w.id));
  if (w.type) cov.appendChild(el('div', 'badge type', w.type));
  c.appendChild(cov); c.appendChild(el('div', 'wt', w.name || w.id));
  c.onclick = () => pushView('detail', { id: w.id });
  return c;
}
// 分批懒加载：一次只建 CARD_BATCH 张卡片，滚动接近底部时再补下一批。
// 媒体库上千个作品、或单个作品内上百张图片时，避免一次性构建 DOM + 发起大量图片请求。
const CARD_BATCH = 30;
let _cardIO = null;   // 全局仅一个 observer（同一时刻只有一个懒加载网格在展示）
let _cardLoaded = 0;      // 当前懒加载网格已渲染的卡片数（供 pushView 记录、popView 恢复）
let _restoreLoaded = 0;   // 从详情返回时需先补足的卡片数，使内容够高以恢复滚动位置（消费一次即清零）
// 把 items 分批用 makeFn 渲染进「已在 DOM 中」的 grid；哨兵滚入视口（提前 600px）时补下一批。
// unobserve→observe 触发一次新的相交回调，处理"补完后哨兵仍在视口"的连续加载。
function lazyGridFill(grid, items, makeFn) {
  if (_cardIO) { _cardIO.disconnect(); _cardIO = null; }
  let idx = 0;
  const renderNext = () => {
    const end = Math.min(idx + CARD_BATCH, items.length);
    const frag = document.createDocumentFragment();
    for (; idx < end; idx++) frag.appendChild(makeFn(items[idx]));
    grid.appendChild(frag);
    _cardLoaded = idx;
  };
  // 首批渲染：普通进入渲染一批；从详情返回时补足之前展开的批数（_restoreLoaded），保证内容够高、可恢复滚动位置
  const target = Math.min(Math.max(CARD_BATCH, _restoreLoaded), items.length);
  _restoreLoaded = 0;
  do { renderNext(); } while (idx < target);
  if (idx >= items.length) return;
  const sentinel = el('div'); sentinel.style.height = '1px'; grid.after(sentinel);
  // rootMargin 为 0：哨兵（网格末尾）真正滚入视口——即滑块拉到底——时才加载下一批，
  // 不做提前预取。unobserve→observe 触发新回调，处理"补完后仍在底部"的连续加载。
  _cardIO = new IntersectionObserver(es => {
    if (!es[0].isIntersecting) return;
    _cardIO.unobserve(sentinel);
    renderNext();
    if (idx < items.length) _cardIO.observe(sentinel);
    else { _cardIO.disconnect(); _cardIO = null; sentinel.remove(); }
  }, { rootMargin: '0px' });
  _cardIO.observe(sentinel);
}
function lazyCards(host, items, makeFn, emptyText) {
  if (_cardIO) { _cardIO.disconnect(); _cardIO = null; }
  host.innerHTML = '';
  if (!items.length) { host.appendChild(el('div', 'empty', emptyText)); return; }
  const grid = el('div', 'grid cards'); host.appendChild(grid);
  lazyGridFill(grid, items, makeFn);
}
function drawWorks(items) {
  lastMap = null;
  const items2 = filtered();
  $('count').textContent = $('search').value.trim() ? `共 ${items.length} 个作品，匹配 ${items2.length} 个` : `共 ${items.length} 个作品`;
  lazyCards($('content'), items2, makeWorkCard, '没有内容');
}
