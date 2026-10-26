// settings.js —— 系统设置页 + 媒体库管理 + FANBOX 作家监控。
// ========== 设置 ==========
async function renderSettings() {
  $('title').textContent = '系统设置';
  const host = $('content'); host.innerHTML = '<div class="empty"><span class="spin"></span> 加载中…</div>';
  let d; try { d = await api('/api/settings'); } catch (e) { return; }
  host.innerHTML = '';
  const write = (section, key, value) => apiPost('/api/settings', { section, key, value });
  const mkInput = (val, on) => { const i = el('input'); i.value = val; i.onchange = () => on(i.value.trim()); return i; };
  const mkSelect = (opts, val, on) => { const s = el('select'); opts.forEach(([v, t]) => { const o = el('option', null, t); o.value = v; s.appendChild(o); }); s.value = val; s.onchange = () => on(s.value); return s; };
  const boolOpts = [['True', '开启'], ['False', '关闭']];
  // 表单行：字符串→<label>，元素→原样放入。一行放多个相关字段，排布以桌面端 SettingsPage.xaml 为准
  const frow = (...kids) => {
    const r = el('div', 'frow');
    kids.forEach(k => r.appendChild(typeof k === 'string' ? el('label', null, k) : k));
    return r;
  };
  const frowTop = (...kids) => { const r = frow(...kids); r.classList.add('top'); return r; };   // 控件占多行时用
  const w = (node, px) => { node.style.width = px + 'px'; return node; };                        // 定宽控件（同桌面端）
  const grow = node => { node.classList.add('grow'); return node; };                             // 撑满剩余宽度

  // 下载（桌面端：缓存路径 / 自动下载+自动解压 / 线程数+最低速度+速度限制 / 解压密码库）
  let s = el('div', 'sec'); s.appendChild(el('h3', null, '下载'));
  s.appendChild(frow('缓存路径', grow(mkInput(d.downpath, v => write('downpath', 'downpath', v)))));
  s.appendChild(frow(
    '自动下载', w(mkSelect(boolOpts, d.autoDownload ? 'True' : 'False', v => write('down_list', 'auto_download', v)), 120),
    '自动解压', w(mkSelect(boolOpts, d.autoUnzip ? 'True' : 'False', v => write('down_list', 'auto_unzip', v)), 120)));
  s.appendChild(frow(
    '单文件线程数', w(mkInput(d.downProc, v => write('down_list', 'download_processes', v)), 80),
    '最低速度 (KB/s)', w(mkInput(d.minSpeed, v => write('down_list', 'min_speed', v)), 80),
    '速度限制 (KB/s)', w(mkInput(d.speedLimit, v => write('down_list', 'speed_limit', v)), 80)));
  // 解压密码库：一个输入框一个密码，末尾「＋」新增一条（同桌面端 SettingsPage 的密码行）。
  // 存库仍是老格式（一行一个），故服务端与解压那边都不用动。
  const pwList = (d.unzipPasswords || '').split('\n').map(v => v.trim()).filter(Boolean);
  const pwWrap = grow(el('div', 'pwlist'));
  const savePw = () => write('unzip', 'passwords', pwList.map(v => v.trim()).filter(Boolean).join('\n'));
  const renderPw = focusLast => {
    pwWrap.innerHTML = '';
    pwList.forEach((v, i) => {
      const row = el('div', 'pwrow');
      const inp = el('input'); inp.value = v; inp.placeholder = '密码';
      inp.oninput = () => { pwList[i] = inp.value; };
      inp.onchange = () => savePw();
      const del = el('button', 'mini danger', '✕');
      del.title = '删除这条密码';
      del.onclick = () => { pwList.splice(i, 1); savePw(); renderPw(); };
      row.append(inp, del);
      pwWrap.appendChild(row);
    });
    const add = el('button', 'mini', '＋');
    add.title = '添加一条密码';
    add.onclick = () => { pwList.push(''); renderPw(true); };
    pwWrap.appendChild(add);
    if (focusLast) {
      const boxes = pwWrap.querySelectorAll('.pwrow input');
      if (boxes.length) boxes[boxes.length - 1].focus();
    }
  };
  renderPw();
  s.appendChild(frowTop('解压密码库', pwWrap));
  host.appendChild(s);

  // 代理（桌面端：开关 / 类型 / 主机 / 端口 同一行，只有第一个有标签）
  s = el('div', 'sec'); s.appendChild(el('h3', null, '代理'));
  const proxyHost = grow(mkInput(d.proxy.host, v => write('proxy', 'host', v)));
  proxyHost.placeholder = '主机';
  const proxyPort = w(mkInput(d.proxy.port, v => write('proxy', 'port', v)), 80);
  proxyPort.placeholder = '端口';
  s.appendChild(frow(
    '开启', w(mkSelect(boolOpts, d.proxy.open ? 'True' : 'False', v => write('proxy', 'openproxy', v)), 120),
    w(mkSelect([['http', 'http'], ['https', 'https'], ['socks5', 'socks5']], d.proxy.type, v => write('proxy', 'type', v)), 100),
    proxyHost, proxyPort));
  host.appendChild(s);

  // Debrid-Link（桌面端：API Key 与「测试」同一行）
  s = el('div', 'sec'); s.appendChild(el('h3', null, 'Debrid-Link 下载中转站'));
  // API Key 不回传明文：留空表示不修改；仅在填入新值时写入
  const keyInput = grow(mkInput('', v => { if (v) write('debrid', 'api_key', v); }));
  keyInput.type = 'password';
  keyInput.placeholder = d.debridKeySet ? '已设置（留空则不修改）' : '未设置';
  const testBtn = el('button', 'icon-btn', '测试');
  const testRes = el('span', 'tres');
  testBtn.onclick = async () => { testRes.textContent = '测试中…'; const r = await apiPost('/api/debridtest', { key: keyInput.value.trim() }); testRes.textContent = r.ok ? '✓ 有效' : '✗ 无效'; testRes.style.color = r.ok ? '#4ade80' : '#f87171'; };
  s.appendChild(frow('API Key', keyInput, testBtn, testRes));
  host.appendChild(s);

  // FANBOX 作家监控（原先在 作品搜索 → FANBOX 的工具栏「⏱ 监控」里，现统一在设置页管）
  await renderFanboxWatchSection(host);

  // 系统（桌面端：语言 + 日志级别 + 解压编码 同一行；「关闭按钮」为桌面独有项）
  s = el('div', 'sec'); s.appendChild(el('h3', null, '系统'));
  s.appendChild(frow(
    '语言', w(mkSelect(d.languages.map(l => [l.code, l.name]), d.language, v => write('language', 'lang', v)), 140),
    '日志级别', w(mkSelect([['info', 'info'], ['error', 'error'], ['debug', 'debug']], d.logLevel, v => write('loglevel', 'level', v)), 120),
    '解压编码', w(mkInput(d.encoding, v => write('encoding', 'encoding', v)), 120)));
  host.appendChild(s);

  // 媒体库（可管理：新建/删除、加/移除文件夹、扫描导入）
  await renderMediaLibSection(host);

  // 图床存储：作品卡封面改由图床直供。媒体库多放在 HDD 上，翻一页卡片要逐张唤醒磁盘随机读，
  // 封面搬到图床后浏览器直接向图床要 ?w= 缩略图，本机硬盘只在真正播放作品时才转。
  // 排布同桌面端：启用+项目 一行 / 服务地址 一行 / API Token 与「测试连接」「迁移封面」同一行
  s = el('div', 'sec'); s.appendChild(el('h3', null, '图床存储'));
  const ihProj = w(mkInput(d.imageHost.project, v => write('image_host', 'project', v)), 180);
  ihProj.placeholder = '项目标识（slug）';
  s.appendChild(frow(
    '启用', w(mkSelect(boolOpts, d.imageHost.enabled ? 'True' : 'False', v => write('image_host', 'enabled', v)), 120),
    '项目', ihProj));
  const ihUrl = grow(mkInput(d.imageHost.baseUrl, v => write('image_host', 'base_url', v)));
  ihUrl.placeholder = 'http://192.168.1.5:9990';
  s.appendChild(frow('服务地址', ihUrl));
  // Token 同 debrid：不回传明文，留空表示不修改
  const ihToken = grow(mkInput('', v => { if (v) write('image_host', 'token', v); }));
  ihToken.type = 'password';
  ihToken.placeholder = d.imageHost.tokenSet ? '已设置（留空则不修改）' : '未设置';
  const ihTest = el('button', 'icon-btn', '测试连接');
  const ihSync = el('button', 'icon-btn', '迁移封面');
  const ihRes = el('span', 'tres');
  // 三个值都从输入框现取：设置页是失焦即存的，刚输入的值可能还没写完库
  ihTest.onclick = async () => {
    ihRes.style.color = ''; ihRes.textContent = '测试中…';
    const r = await apiPost('/api/imagehost/test', { baseUrl: ihUrl.value.trim(), project: ihProj.value.trim(), token: ihToken.value.trim() });
    ihRes.textContent = r.ok ? `✓ 已连接（图床现有 ${r.count} 张）` : `✗ ${r.error || '连接失败'}`;
    ihRes.style.color = r.ok ? '#4ade80' : '#f87171';
  };
  // 重复点不会跑两轮（服务端单实例串行），同一个作品也不会被迁移两次
  ihSync.onclick = async () => { await apiPost('/api/imagehost/migrate', {}); pollImageHost(); };
  s.appendChild(frow('API Token', ihToken, ihTest, ihSync, ihRes));
  const ihStatus = el('div', 'note'); ihStatus.id = 'imgHostStatus';
  s.appendChild(ihStatus);
  host.appendChild(s);
  pollImageHost();

  // 外部访问（只读，三项同一行，同桌面端那一行 外部访问 / 端口 / 访问密码）
  s = el('div', 'sec'); s.appendChild(el('h3', null, '外部访问'));
  s.appendChild(frow(
    '外部访问', el('div', 'ro', d.web.enabled ? '已开启' : '已关闭'),
    '端口', el('div', 'ro', String(d.web.port)),
    '访问密码', el('div', 'ro', d.web.password ? '已设置' : '未设置')));
  host.appendChild(s);
}

// 图床迁移状态轮询：迁移中每 1.5s 刷新，结束或离开设置页即停（同扫描状态的做法）
let _imgHostPoll = null;
function pollImageHost() {
  const tick = async () => {
    const box = $('imgHostStatus');
    if (!box || !document.body.contains(box)) { if (_imgHostPoll) { clearInterval(_imgHostPoll); _imgHostPoll = null; } return; }
    let s; try { s = await api('/api/imagehost/status'); } catch (e) { return; }
    // 常驻提示（已迁移/待迁移）由服务端给，桌面端共用同一句文案
    box.textContent = s.running ? '⏳ 迁移中 ' + s.status : (s.status ? '✓ ' + s.status : s.idle);
    if (!s.running && _imgHostPoll) { clearInterval(_imgHostPoll); _imgHostPoll = null; }
  };
  if (_imgHostPoll) clearInterval(_imgHostPoll);
  tick();
  _imgHostPoll = setInterval(tick, 1500);
}

// ---------- FANBOX 作家监控（设置页内）----------
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

async function renderFanboxWatchSection(host) {
  const sec = el('div', 'sec'); sec.appendChild(el('h3', null, 'FANBOX 作家监控'));
  const body = el('div'); sec.appendChild(body);
  host.appendChild(sec);
  await fbWatchRefresh(body);
}

async function fbWatchRefresh(body) {
  let d;
  try { d = await api('/api/fanbox/watches'); } catch (e) { return; }
  if (!document.body.contains(body)) return;
  body.innerHTML = '';

  // 操作条：总开关 / 立即检查全部 / 新建监控时的默认间隔（版式同本页的媒体库分区）
  const bar = el('div', 'toolbar inline');
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
    body.appendChild(el('div', 'ro', '还没有监控任何作家。到「作品搜索 → FANBOX」搜到作家后进入作家主页，点「+ 监控作家」即可添加。'));
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

// ---------- 媒体库管理（设置页内）----------
let _libScanPoll = null;
async function renderMediaLibSection(host) {
  const sec = el('div', 'sec'); sec.appendChild(el('h3', null, '媒体库'));
  const bar = el('div', 'toolbar inline');
  // 按钮文案同桌面端 MediaLibSettingDialog.BuildSection
  const addLibBtn = el('button', 'icon-btn', '新建媒体库');
  const scanAllBtn = el('button', 'icon-btn', '扫描全部数据源');
  bar.append(addLibBtn, scanAllBtn); sec.appendChild(bar);
  const status = el('div', 'note'); status.id = 'libScanStatus'; status.style.display = 'none'; sec.appendChild(status);
  const list = el('div'); sec.appendChild(list);
  host.appendChild(sec);

  const refresh = async () => {
    let d; try { d = await api('/api/medialibs'); } catch (e) { return; }
    list.innerHTML = '';
    if (!d.libs.length) { list.appendChild(el('div', 'ro', '还没有媒体库，点击"新建媒体库"创建。')); return; }
    d.libs.forEach(l => list.appendChild(libCard(l, refresh)));
  };
  addLibBtn.onclick = async () => {
    const name = ((await uiPrompt('媒体库名称：', '', '新建媒体库')) || '').trim();
    if (!name) return;
    const r = await apiPost('/api/lib/create', { name });
    if (r.error) await uiAlert(r.error); else refresh();
  };
  scanAllBtn.onclick = async () => { await apiPost('/api/lib/scanall', {}); pollScan(); };
  await refresh();
  pollScan();   // 进入设置页即拉一次扫描状态（可能有后台扫描在进行）
}
function libCard(l, refresh) {
  const card = el('div', 'libcard');
  const head = el('div', 'lh');
  head.append(el('span', 'nm', l.name), el('span', 'ct', `${l.folders.length} 个文件夹`));
  const addF = el('button', 'mini', '添加文件夹');
  const scan = el('button', 'mini', '扫描元数据');
  const rescan = el('button', 'mini', '重新扫描元数据');
  const del = el('button', 'mini danger', '删除');
  head.append(addF, scan, rescan, del); card.appendChild(head);
  (l.folders || []).forEach(f => {
    const row = el('div', 'librow');
    const rm = el('button', 'mini danger', '移除');
    row.append(el('span', 'fp', f), rm); card.appendChild(row);
    rm.onclick = async () => { await apiPost('/api/lib/removefolder', { name: l.name, folder: f }); refresh(); };
  });
  addF.onclick = async () => {
    const folder = ((await uiPrompt('文件夹路径（本机绝对路径，如 D:\\ASMR）：', '', '添加文件夹')) || '').trim();
    if (!folder) return;
    const r = await apiPost('/api/lib/addfolder', { name: l.name, folder });
    if (r.error) await uiAlert(r.error); else refresh();
  };
  scan.onclick = async () => { await apiPost('/api/lib/scan', { name: l.name, force: false }); pollScan(); };
  rescan.onclick = async () => { if (await uiConfirm(`重新扫描"${l.name}"会强制重新抓取全部元数据，较慢。继续？`)) { await apiPost('/api/lib/scan', { name: l.name, force: true }); pollScan(); } };
  del.onclick = async () => { if (await uiConfirm(`确定删除媒体库"${l.name}"吗？\n不会删除本地文件，已导入的作品记录保留。`, { danger: true })) { await apiPost('/api/lib/delete', { name: l.name }); refresh(); } };
  return card;
}
// 扫描状态轮询：进行中时每 1.5s 刷新状态文本，结束或离开设置页即停
function pollScan() {
  const tick = async () => {
    const box = $('libScanStatus');
    if (!box || !document.body.contains(box)) { if (_libScanPoll) { clearInterval(_libScanPoll); _libScanPoll = null; } return; }
    let s; try { s = await api('/api/lib/scanstatus'); } catch (e) { return; }
    if (s.status) { box.style.display = ''; box.textContent = (s.scanning ? '⏳ ' : '✓ ') + s.status; }
    if (!s.scanning && _libScanPoll) { clearInterval(_libScanPoll); _libScanPoll = null; }
  };
  if (_libScanPoll) clearInterval(_libScanPoll);
  tick();
  _libScanPoll = setInterval(tick, 1500);
}
