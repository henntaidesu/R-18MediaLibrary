// watch.js —— 作品监控（一级导航「作品监控」，分区 key watch；2026-10-29 由「作家监控」改名）。
// 页内二级导航按来源分开：Pawchive（作家监控）/ Pixiv（收藏监控），地址栏 #/watch、#/watch/pixiv。
// 原先是「系统设置 → FANBOX 作家监控」分区，2026-10-26 起独立成页。
//
// Pawchive：数据在服务端的 fanbox_watch 表里，后台按各作家自己的间隔轮询 pawchive：
// 发现比"添加监控那一刻"更新的投稿，就按该作家设定的媒体库自动入队下载。
// 判"新"看的是投稿的发布时间而不是"本地有没有这篇"，所以手动删掉的作品不会被一次次下回来。
// 添加监控的入口仍在 作品搜索 → Pawchive 的作家主页（「+ 监控作家」）——加监控本就要先选到作家。
//
// Pixiv：数据在 pixiv_watch 表里，一个已登录的 pixiv 账号一张卡，在本页直接「开启监控」。
// 判"新"看收藏号（随收藏先后递增），旧作新收藏也算新；开启时可选「获取历史收藏」，
// 服务端按从旧到新分批慢速入队（两件之间歇「入队间隔」秒，429 时冷却 60 秒），中途停下下一轮接着来。

// 轮询间隔档位（分钟 → 文案）；服务端 FanboxWatchService.IntervalChoices 是同一组
const FB_WATCH_INTERVALS = [
  [30, '30 分钟'], [60, '1 小时'], [120, '2 小时'], [360, '6 小时'],
  [720, '12 小时'], [1440, '24 小时'], [4320, '3 天'],
];
// Pixiv 批量入队时两件作品之间的间隔（秒）
const PX_WATCH_PACES = [1, 2, 3, 5, 10, 20, 30];
const WATCH_TABS = [['fanbox', 'Pawchive'], ['pixiv', 'Pixiv']];
let watchTab = 'fanbox';   // fanbox | pixiv：二级导航当前页（切分区不重置，回来仍停在上次那页）
let _fbWatchPoll = null;
let _pxWatchPoll = null;

async function renderWatchPage() {
  $('title').textContent = '';   // 同下载管理：页顶只留工具栏卡片，顶栏无内容时自动收起
  const host = $('content'); host.innerHTML = '<div class="empty"><span class="spin"></span> 加载中…</div>';
  const body = el('div');
  await watchRefresh(body);
  if (section !== 'watch' || host.contains(body)) return;   // 加载期间已切走则不画
  host.innerHTML = ''; host.appendChild(body);
}

function watchRefresh(body) {
  return watchTab === 'pixiv' ? pxWatchRefresh(body) : fbWatchRefresh(body);
}

// 二级导航：放在工具栏最前（同作品搜索把来源下拉放在最前），切换只 replace 地址栏、不压历史层级
function watchTabs(body) {
  const seg = el('div', 'seg');
  WATCH_TABS.forEach(([k, t]) => {
    const b = el('button', 'icon-btn' + (watchTab === k ? ' on' : ''), t);
    b.onclick = () => {
      if (watchTab === k) return;
      watchTab = k;
      history.replaceState({ nav: true }, '', encodeHash());
      watchRefresh(body);
    };
    seg.appendChild(b);
  });
  return seg;
}

// ========== Pawchive：作家监控 ==========

async function fbWatchRefresh(body) {
  let d;
  try { d = await api('/api/fanbox/watches'); } catch (e) { return; }
  if (body.isConnected === false && body.childNodes.length) return;   // 已离开本页（首次渲染时 body 尚未挂上，放行）
  if (watchTab !== 'fanbox') return;   // 请求期间已切到别的二级页
  body.innerHTML = '';

  // 页顶工具栏卡片：二级导航 / 总开关 / 立即检查全部 / 新建监控时的默认间隔
  const bar = el('div', 'toolbar');
  const sw = el('button', 'icon-btn' + (d.enabled ? ' on' : ''), '自动监控：' + (d.enabled ? '开' : '关'));
  sw.title = '关闭后仅停止后台自动轮询，「立即检查」仍可用';
  sw.onclick = async () => { await apiPost('/api/fanbox/watch/config', { enabled: !d.enabled }); fbWatchRefresh(body); };
  const checkAll = el('button', 'icon-btn', '立即检查全部');
  checkAll.disabled = !d.watches.length;
  checkAll.onclick = async () => { await apiPost('/api/fanbox/watch/check', {}); pollFbWatch(body); };
  bar.append(watchTabs(body), sw, checkAll, el('span', 'ro', '默认间隔'),
    fbIntervalSelect(d.interval, v => apiPost('/api/fanbox/watch/config', { interval: v })), helpButton('12'));
  body.appendChild(bar);

  const status = el('div', 'note'); status.id = 'fbWatchStatus';
  status.textContent = d.busy ? '⏳ ' + (d.status || '正在检查…') : (d.summary || '');
  body.appendChild(status);

  if (!d.watches.length) {
    body.appendChild(el('div', 'empty', '还没有监控任何作家。到「作品搜索 → Pawchive」搜到作家后进入作家主页，点「+ 监控作家」即可添加。'));
    return;
  }
  const grid = el('div', 'wgrid');
  d.watches.forEach(w => grid.appendChild(fbWatchCard(w, body)));
  body.appendChild(grid);
  if (d.busy) pollFbWatch(body);
}

// 一位作家一张小卡（.wcard）：头像 + 名称 + 状态 / 检查时间 / 间隔与去向 / 上次结果 / 按钮
function fbWatchCard(w, body) {
  const now = el('button', 'mini', '立即检查');
  now.onclick = async () => { await apiPost('/api/fanbox/watch/check', { id: w.id }); pollFbWatch(body); };
  const toggle = el('button', 'mini', w.enabled ? '暂停' : '启用');
  toggle.onclick = async () => { await apiPost('/api/fanbox/watch/update', { id: w.id, enabled: !w.enabled }); fbWatchRefresh(body); };
  const open = el('button', 'mini', '打开主页');
  open.onclick = () => fbGotoArtist(w.id, w.name);   // 跨分区跳到 作品搜索 → Pawchive 的作家主页
  const del = el('button', 'mini danger', '删除');
  del.onclick = async () => {
    if (!await uiConfirm(`不再监控「${w.name}」？
已经下载的作品不受影响。`, { danger: true })) return;
    await apiPost('/api/fanbox/watch/update', { id: w.id, remove: true });
    if (typeof fbArtist !== 'undefined' && fbArtist && fbArtist.id === w.id) { fbWatched = false; fbUpdateWatchBtn(); }
    fbWatchRefresh(body);
  };
  return watchCard({
    icon: w.icon ? '/api/fanbox/image?url=' + enc(w.icon) : '', avatar: true,
    name: w.name, on: w.enabled, state: w.enabled ? '监控中' : '已暂停',
    w, unit: '篇', actions: [now, toggle, open, del],
    onInterval: async v => { await apiPost('/api/fanbox/watch/update', { id: w.id, interval: v }); fbWatchRefresh(body); },
  });
}

// 检查时间只留「月-日 时:分」（卡片窄，年份与秒没用；不是这个格式就原样显示）
function watchShortTime(t) {
  const m = /^\d{4}-(\d{2}-\d{2}) (\d{2}:\d{2})/.exec(t || '');
  return m ? m[1] + ' ' + m[2] : (t || '');
}

// 两页共用的小卡骨架。o.w 为监控记录时画出检查时间 / 间隔 / 上次结果，否则只画 o.meta 一行
function watchCard(o) {
  const card = el('div', 'wcard' + (o.w && !o.on ? ' off' : ''));
  const head = el('div', 'wh');
  if (o.avatar) {
    const av = el('img', 'wav'); av.loading = 'lazy'; av.alt = '';
    if (o.icon) { av.src = o.icon; av.onerror = () => av.removeAttribute('src'); }
    head.appendChild(av);
  }
  const nm = el('span', 'wnm', o.name); nm.title = o.name;
  head.append(nm, el('span', 'wst' + (o.on ? ' on' : ''), o.state));
  card.appendChild(head);

  const w = o.w;
  if (w) {
    const t = [w.lastCheck ? '上次 ' + watchShortTime(w.lastCheck) : '尚未检查过'];
    if (w.next) t.push('下次 ' + watchShortTime(w.next));
    if (w.downloaded) t.push(`累计 ${w.downloaded} ${o.unit}`);
    const meta = el('div', 'wm', t.join(' · '));
    meta.title = [w.lastCheck ? '上次检查 ' + w.lastCheck : '尚未检查过',
      w.next ? '下次检查 ' + w.next : '',
      w.downloaded ? `累计自动下载 ${w.downloaded} ${o.unit}` : ''].filter(Boolean).join('\n');
    card.appendChild(meta);

    const row = el('div', 'wr');
    const lib = el('span', 'wm', w.lib ? `→ 媒体库「${w.lib}」` : '→ 缓存目录');
    lib.title = w.lib ? `自动下载到媒体库「${w.lib}」` : '自动下载到缓存目录';
    row.append(fbIntervalSelect(w.interval, o.onInterval), lib);
    row.title = '轮询间隔';
    card.appendChild(row);

    const res = el('div', 'wm', '上次结果：' + (w.lastResult || '—'));
    res.title = res.textContent;
    card.appendChild(res);
  } else if (o.meta) {
    card.appendChild(el('div', 'wm', o.meta));
  }

  const acts = el('div', 'wa');
  o.actions.forEach(b => acts.appendChild(b));
  card.appendChild(acts);
  return card;
}

function fbIntervalSelect(val, on) {
  const s = el('select');
  s.style.width = '110px';
  const opts = FB_WATCH_INTERVALS.slice();
  // 服务端存的值不在档位里（例如手工改过库）时补一档，免得下拉把它悄悄改掉
  if (!opts.some(o => o[0] === val)) opts.unshift([val, val + ' 分钟']);
  opts.forEach(([v, t]) => { const o = el('option', null, t); o.value = v; s.appendChild(o); });
  s.value = String(val);
  s.onchange = () => on(parseInt(s.value, 10));
  return s;
}

// 检查进行中时每 1.5s 刷新一次状态，检查结束即停并重绘（同扫描/图床迁移的做法）
function pollFbWatch(body) {
  if (_fbWatchPoll) { clearInterval(_fbWatchPoll); _fbWatchPoll = null; }
  _fbWatchPoll = setInterval(async () => {
    const box = $('fbWatchStatus');
    if (!box || !document.body.contains(box)) { clearInterval(_fbWatchPoll); _fbWatchPoll = null; return; }
    let s;
    try { s = await api('/api/fanbox/watches'); } catch (e) { return; }
    box.textContent = s.busy ? '⏳ ' + (s.status || '正在检查…') : (s.summary || '');
    if (!s.busy) { clearInterval(_fbWatchPoll); _fbWatchPoll = null; fbWatchRefresh(body); }
  }, 1500);
}

// ========== Pixiv：收藏监控 ==========

async function pxWatchRefresh(body) {
  let d;
  try { d = await api('/api/pixiv/watches'); } catch (e) { return; }
  if (body.isConnected === false && body.childNodes.length) return;
  if (watchTab !== 'pixiv') return;
  body.innerHTML = '';

  // 工具栏：二级导航 / 总开关 / 立即检查全部 / 默认间隔 / 入队间隔（防 429）
  const bar = el('div', 'toolbar');
  const sw = el('button', 'icon-btn' + (d.enabled ? ' on' : ''), '自动监控：' + (d.enabled ? '开' : '关'));
  sw.title = '关闭后仅停止后台自动轮询，「立即检查」仍可用';
  sw.onclick = async () => { await apiPost('/api/pixiv/watch/config', { enabled: !d.enabled }); pxWatchRefresh(body); };
  const checkAll = el('button', 'icon-btn', '立即检查全部');
  checkAll.disabled = !d.watches.length;
  checkAll.onclick = async () => { await apiPost('/api/pixiv/watch/check', {}); pollPxWatch(body); };
  const pace = el('select'); pace.style.width = '80px';
  PX_WATCH_PACES.forEach(v => { const o = el('option', null, v + ' 秒'); o.value = v; pace.appendChild(o); });
  pace.value = String(d.pace);
  pace.title = '批量入队（尤其是获取历史收藏）时两件作品之间歇多久，太快会触发站点的 429 限流';
  pace.onchange = () => apiPost('/api/pixiv/watch/config', { pace: parseInt(pace.value, 10) });
  bar.append(watchTabs(body), sw, checkAll,
    el('span', 'ro', '默认间隔'), fbIntervalSelect(d.interval, v => apiPost('/api/pixiv/watch/config', { interval: v })),
    el('span', 'ro', '入队间隔'), pace, helpButton('12.2'));
  body.appendChild(bar);

  const status = el('div', 'note'); status.id = 'pxWatchStatus';
  status.textContent = d.busy ? '⏳ ' + (d.status || '正在检查…') : (d.summary || '');
  body.appendChild(status);

  if (!d.accounts.length && !d.watches.length) {
    body.appendChild(el('div', 'empty', '还没有登录 Pixiv 账号。到「账号访问」登录后，这里每个账号一张卡，点「开启监控」即可在有新收藏时自动下载。'));
    return;
  }
  // 每个已登录账号一张卡（已监控 / 未监控），再接上账号已退出登录、但监控还留着的
  const byUid = new Map(d.watches.map(w => [w.uid, w]));
  const grid = el('div', 'wgrid');
  d.accounts.forEach(a => grid.appendChild(byUid.has(a.uid) ? pxWatchCard(byUid.get(a.uid), body) : pxIdleCard(a, d, body)));
  d.watches.filter(w => !w.hasAccount).forEach(w => grid.appendChild(pxWatchCard(w, body)));
  body.appendChild(grid);
  if (d.busy) pollPxWatch(body);
}

// 未监控的账号：只有名字、「查看收藏」与「开启监控」
function pxIdleCard(a, d, body) {
  const fav = el('button', 'mini', '查看收藏');
  fav.onclick = () => pxGotoFavorites(a.uid);
  const on = el('button', 'mini', '开启监控');
  on.onclick = () => pxWatchEnable(a, d, body, on);
  return watchCard({ name: a.name, on: false, state: '未监控', meta: '有新收藏时自动下载，点「开启监控」开始', actions: [fav, on] });
}

function pxWatchCard(w, body) {
  const now = el('button', 'mini', '立即检查');
  now.onclick = async () => { await apiPost('/api/pixiv/watch/check', { uid: w.uid }); pollPxWatch(body); };
  const toggle = el('button', 'mini', w.enabled ? '暂停' : '启用');
  toggle.onclick = async () => { await apiPost('/api/pixiv/watch/update', { uid: w.uid, enabled: !w.enabled }); pxWatchRefresh(body); };
  const actions = [now, toggle];
  if (w.hasAccount) {
    const fav = el('button', 'mini', '查看收藏');
    fav.onclick = () => pxGotoFavorites(w.uid);
    actions.push(fav);
  }
  const del = el('button', 'mini danger', '删除');
  del.onclick = async () => {
    if (!await uiConfirm(`不再监控「${w.name}」的收藏？
已经下载的作品不受影响。`, { danger: true })) return;
    await apiPost('/api/pixiv/watch/update', { uid: w.uid, remove: true });
    pxWatchRefresh(body);
  };
  actions.push(del);
  return watchCard({
    name: w.name, on: w.enabled && w.hasAccount,
    state: !w.hasAccount ? '账号已退出' : w.enabled ? '监控中' : '已暂停',
    w, unit: '件', actions,
    onInterval: async v => { await apiPost('/api/pixiv/watch/update', { uid: w.uid, interval: v }); pxWatchRefresh(body); },
  });
}

// 开启监控：先选媒体库（同手动下载），再选要不要连历史收藏一起下
async function pxWatchEnable(a, d, body, btn) {
  let lib = '', folder = '';
  const t = await api('/api/downtargets');
  if (t.libs && t.libs.length) {
    const target = await pickTarget(t.libs, a.name);
    if (!target) return;
    lib = target.lib; folder = target.folder;
  }
  const how = await uiChoice({
    title: '开启收藏监控',
    message: `监控「${a.name}」的收藏，有新收藏时自动下载。已有的收藏要一并下载吗？`,
    choices: [
      { key: 'new', icon: '🆕', name: '只要新收藏', sub: '从现在起，只下载今后新收藏的作品' },
      { key: 'history', icon: '🗂', name: '获取历史收藏', sub: `已有的收藏也分批加入下载队列（每件间隔 ${d.pace} 秒防 429，数量多时要跑很久）` },
    ],
  });
  if (!how) return;
  if (btn) { btn.disabled = true; btn.textContent = '开启中…'; }
  let r;
  try { r = await apiPost('/api/pixiv/watch/add', { uid: a.uid, history: how === 'history', lib, folder }); }
  finally { if (btn) btn.disabled = false; }
  if (!r || !r.ok) { if (btn) btn.textContent = '开启监控'; await uiAlert((r && r.message) || '开启监控失败'); return; }
  await uiAlert(r.message);
  await pxWatchRefresh(body);
  pollPxWatch(body);
}

// 跨分区跳到 作品搜索 →「Pixiv（账号名）」，留空查询即列出该账号的收藏
function pxGotoFavorites(uid) {
  selectSection('search');
  setSearchSource('pixiv:' + uid);
  pxGotoQuery('');
}

function pollPxWatch(body) {
  if (_pxWatchPoll) { clearInterval(_pxWatchPoll); _pxWatchPoll = null; }
  _pxWatchPoll = setInterval(async () => {
    const box = $('pxWatchStatus');
    if (!box || !document.body.contains(box)) { clearInterval(_pxWatchPoll); _pxWatchPoll = null; return; }
    let s;
    try { s = await api('/api/pixiv/watches'); } catch (e) { return; }
    box.textContent = s.busy ? '⏳ ' + (s.status || '正在检查…') : (s.summary || '');
    if (!s.busy) { clearInterval(_pxWatchPoll); _pxWatchPoll = null; pxWatchRefresh(body); }
  }, 1500);
}
