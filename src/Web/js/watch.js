// watch.js —— 作家监控（一级导航「作家监控」，分区 key watch）。
// 原先是「系统设置 → FANBOX 作家监控」分区，2026-10-26 起独立成页。
// 数据在服务端的 fanbox_watch 表里，后台按各作家自己的间隔轮询 pawchive：
// 发现比"添加监控那一刻"更新的投稿，就按该作家设定的媒体库自动入队下载。
// 判"新"看的是投稿的发布时间而不是"本地有没有这篇"，所以手动删掉的作品不会被一次次下回来。
// 添加监控的入口仍在 作品搜索 → FANBOX 的作家主页（「+ 监控作家」）——加监控本就要先选到作家。

// 轮询间隔档位（分钟 → 文案）；服务端 FanboxWatchService.IntervalChoices 是同一组
const FB_WATCH_INTERVALS = [
  [30, '30 分钟'], [60, '1 小时'], [120, '2 小时'], [360, '6 小时'],
  [720, '12 小时'], [1440, '24 小时'], [4320, '3 天'],
];
let _fbWatchPoll = null;

async function renderWatchPage() {
  $('title').textContent = '';   // 同下载管理：页顶只留工具栏卡片，顶栏无内容时自动收起
  const host = $('content'); host.innerHTML = '<div class="empty"><span class="spin"></span> 加载中…</div>';
  const body = el('div');
  await fbWatchRefresh(body);
  if (section !== 'watch' || host.contains(body)) return;   // 加载期间已切走则不画
  host.innerHTML = ''; host.appendChild(body);
}

async function fbWatchRefresh(body) {
  let d;
  try { d = await api('/api/fanbox/watches'); } catch (e) { return; }
  if (body.isConnected === false && body.childNodes.length) return;   // 已离开本页（首次渲染时 body 尚未挂上，放行）
  body.innerHTML = '';

  // 页顶工具栏卡片：总开关 / 立即检查全部 / 新建监控时的默认间隔
  const bar = el('div', 'toolbar');
  const sw = el('button', 'icon-btn' + (d.enabled ? ' on' : ''), '自动监控：' + (d.enabled ? '开' : '关'));
  sw.title = '关闭后仅停止后台自动轮询，「立即检查」仍可用';
  sw.onclick = async () => { await apiPost('/api/fanbox/watch/config', { enabled: !d.enabled }); fbWatchRefresh(body); };
  const checkAll = el('button', 'icon-btn', '立即检查全部');
  checkAll.disabled = !d.watches.length;
  checkAll.onclick = async () => { await apiPost('/api/fanbox/watch/check', {}); pollFbWatch(body); };
  bar.append(sw, checkAll, el('span', 'ro', '默认间隔'),
    fbIntervalSelect(d.interval, v => apiPost('/api/fanbox/watch/config', { interval: v })));
  body.appendChild(bar);

  const status = el('div', 'note'); status.id = 'fbWatchStatus';
  status.textContent = d.busy ? '⏳ ' + (d.status || '正在检查…') : (d.summary || '');
  body.appendChild(status);

  if (!d.watches.length) {
    body.appendChild(el('div', 'empty', '还没有监控任何作家。到「作品搜索 → FANBOX」搜到作家后进入作家主页，点「+ 监控作家」即可添加。'));
    return;
  }
  d.watches.forEach(w => body.appendChild(fbWatchCard(w, body)));
  if (d.busy) pollFbWatch(body);
}

// 一位作家一张卡（版式同媒体库管理的 .libcard）
function fbWatchCard(w, body) {
  const card = el('div', 'libcard');
  const head = el('div', 'lh');
  head.appendChild(el('span', 'nm', w.name));
  const sub = [];
  if (!w.enabled) sub.push('已暂停');
  sub.push(w.lastCheck ? '上次检查 ' + w.lastCheck : '尚未检查过');
  if (w.next) sub.push('下次 ' + w.next);
  if (w.downloaded) sub.push(`累计自动下载 ${w.downloaded} 篇`);
  head.appendChild(el('span', 'ct', sub.join(' · ')));

  const now = el('button', 'mini', '立即检查');
  now.onclick = async () => { await apiPost('/api/fanbox/watch/check', { id: w.id }); pollFbWatch(body); };
  const toggle = el('button', 'mini', w.enabled ? '暂停' : '启用');
  toggle.onclick = async () => { await apiPost('/api/fanbox/watch/update', { id: w.id, enabled: !w.enabled }); fbWatchRefresh(body); };
  const open = el('button', 'mini', '打开主页');
  open.onclick = () => fbGotoArtist(w.id, w.name);   // 跨分区跳到 作品搜索 → FANBOX 的作家主页
  const del = el('button', 'mini danger', '删除');
  del.onclick = async () => {
    if (!await uiConfirm(`不再监控「${w.name}」？
已经下载的作品不受影响。`, { danger: true })) return;
    await apiPost('/api/fanbox/watch/update', { id: w.id, remove: true });
    if (typeof fbArtist !== 'undefined' && fbArtist && fbArtist.id === w.id) { fbWatched = false; fbUpdateWatchBtn(); }
    fbWatchRefresh(body);
  };
  head.append(now, toggle, open, del);
  card.appendChild(head);

  const row = el('div', 'librow');
  row.appendChild(el('span', null, '轮询间隔'));
  row.appendChild(fbIntervalSelect(w.interval, async v => {
    await apiPost('/api/fanbox/watch/update', { id: w.id, interval: v });
    fbWatchRefresh(body);
  }));
  row.appendChild(el('span', 'fp', w.lib ? `自动下载到媒体库「${w.lib}」` : '自动下载到缓存目录'));
  card.appendChild(row);

  if (w.lastResult) {
    const res = el('div', 'librow');
    res.appendChild(el('span', 'fp', '上次结果：' + w.lastResult));
    card.appendChild(res);
  }
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
