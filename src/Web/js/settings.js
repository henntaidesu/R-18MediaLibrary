// settings.js —— 系统设置页 + 媒体库管理。
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
  let s = el('div', 'sec'); s.appendChild(helpHeading('下载', '11.3'));
  s.appendChild(frow('缓存路径', grow(mkInput(d.downpath, v => write('downpath', 'downpath', v)))));
  s.appendChild(frow(
    '自动下载', w(mkSelect(boolOpts, d.autoDownload ? 'True' : 'False', v => write('down_list', 'auto_download', v)), 120),
    '自动解压', w(mkSelect(boolOpts, d.autoUnzip ? 'True' : 'False', v => write('down_list', 'auto_unzip', v)), 120)));
  s.appendChild(frow(
    // 1~6 下拉（同桌面端）；库里存着更大的老值时显示 6，与服务端 Math.Clamp 的实际生效值一致
    '单文件线程数', w(mkSelect([1, 2, 3, 4, 5, 6].map(n => [String(n), String(n)]),
      String(Math.min(6, Math.max(1, parseInt(d.downProc, 10) || 1))),
      v => write('down_list', 'download_processes', v)), 80),
    '最低速度 (KB/s)', w(mkInput(d.minSpeed, v => write('down_list', 'min_speed', v)), 80),
    '速度限制 (KB/s)', w(mkInput(d.speedLimit, v => write('down_list', 'speed_limit', v)), 80)));
  // 解压密码库：一个密码一张小卡片（宽度随内容），悬停时右上角出 ✕，删除要二次确认；
  // 末尾圆形「＋」弹框新增一条。存库仍是老格式（一行一个），故服务端与解压那边都不用动。
  const pwList = (d.unzipPasswords || '').split('\n').map(v => v.trim()).filter(Boolean);
  const pwWrap = grow(el('div', 'pwlist'));
  const savePw = () => write('unzip', 'passwords', pwList.join('\n'));
  const renderPw = () => {
    pwWrap.innerHTML = '';
    pwList.forEach((v, i) => {
      const chip = el('div', 'pwchip', v);
      const del = el('button', 'pwdel', '✕');
      del.title = '删除这条密码';
      del.onclick = async () => {
        if (!await uiConfirm(`删除密码「${v}」？`, { okText: '删除', danger: true })) return;
        pwList.splice(i, 1); savePw(); renderPw();
      };
      chip.appendChild(del);
      pwWrap.appendChild(chip);
    });
    const add = el('button', 'pwadd', '＋');
    add.title = '添加一条密码';
    add.onclick = async () => {
      const v = ((await uiPrompt('输入解压密码', '', '添加密码')) || '').trim();
      if (!v) return;
      if (pwList.includes(v)) { uiAlert('这个密码已经在密码库里了'); return; }
      pwList.push(v); savePw(); renderPw();
    };
    pwWrap.appendChild(add);
  };
  renderPw();
  s.appendChild(frowTop('解压密码库', pwWrap));
  host.appendChild(s);

  // 代理（桌面端：开关 / 类型 / 主机 / 端口 同一行，只有第一个有标签）
  s = el('div', 'sec'); s.appendChild(helpHeading('代理', '14.1'));
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
  s = el('div', 'sec'); s.appendChild(helpHeading('Debrid-Link 下载中转站', '7.2'));
  // API Key 不回传明文：留空表示不修改；仅在填入新值时写入
  // 明文仍不随设置一起回传：已设置时框里先放一串占位圆点；点 👁 输入访问密码后才向服务端取明文显示。
  // 占位值原样失焦不算修改，清空也不写库（留空 = 不修改）
  const KEY_MASK = '••••••••••••';
  const keyInput = grow(mkInput(d.debridKeySet ? KEY_MASK : '', v => { if (v && v !== KEY_MASK) write('debrid', 'api_key', v); }));
  keyInput.type = 'password';
  keyInput.placeholder = '未设置';
  keyInput.onfocus = () => { if (keyInput.value === KEY_MASK) keyInput.select(); };   // 直接输入即覆盖占位
  const eyeBtn = el('button', 'icon-btn', '👁');
  eyeBtn.title = '查看 API Key（需要访问密码）';
  eyeBtn.hidden = !d.debridKeySet;
  eyeBtn.onclick = async () => {
    if (keyInput.type === 'text') { keyInput.type = 'password'; eyeBtn.textContent = '👁'; return; }   // 再点一次收起
    if (keyInput.value === KEY_MASK) {
      const ask = uiModal({ title: '查看 API Key', message: '请输入访问密码', input: true, cancelText: '取消' });
      const f = document.querySelector('#modalBox .modal-input'); if (f) f.type = 'password';   // 弹框已同步建好，输入时不显示明文
      const pw = await ask;
      if (pw == null) return;
      let r; try { r = await apiPost('/api/debridkey', { password: pw }); } catch (e) { return; }
      if (!r.ok) { uiAlert(r.error || '密码错误'); return; }
      keyInput.value = r.key;
    }
    keyInput.type = 'text'; eyeBtn.textContent = '🙈';
  };
  const testBtn = el('button', 'icon-btn', '测试');
  const testRes = el('span', 'tres');
  testBtn.onclick = async () => {
    testRes.textContent = '测试中…';
    const key = keyInput.value.trim();
    const r = await apiPost('/api/debridtest', { key: key === KEY_MASK ? '' : key });   // 占位值 → 服务端用已存的 key 测
    testRes.textContent = r.ok ? '✓ 有效' : '✗ 无效'; testRes.style.color = r.ok ? '#4ade80' : '#f87171';
  };
  s.appendChild(frow('API Key', keyInput, eyeBtn, testBtn, testRes));
  host.appendChild(s);

  // 系统（桌面端：语言 + 日志级别 + 解压编码 同一行；「关闭按钮」为桌面独有项）
  s = el('div', 'sec'); s.appendChild(helpHeading('系统', '14.1'));
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
  s = el('div', 'sec'); s.appendChild(helpHeading('图床存储', '14.2'));
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
  s = el('div', 'sec'); s.appendChild(helpHeading('外部访问', '15.1'));
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

// ---------- 媒体库管理（设置页内）----------
let _libScanPoll = null;
async function renderMediaLibSection(host) {
  const sec = el('div', 'sec'); sec.appendChild(helpHeading('媒体库', '3'));
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
// 媒体库卡片右上角 ⚙：该库的全部设置（名称 / 默认显示 / 文件夹 / 扫描元数据 / 删除）集中在一个弹框里。
// 二次确认与输入框借用同一个 #modal，所以每问一次都整框重画（reopen）；关掉后回调 onClose 刷新媒体库卡片。
async function openLibSettings(name, onClose) {
  let d; try { d = await api('/api/medialibs'); } catch (e) { return; }
  const l = d.libs.find(x => x.name === name);
  if (!l) { if (onClose) onClose(); return; }
  const ov = $('modal'), box = $('modalBox');
  box.innerHTML = ''; box.classList.add('pk-wide');
  function onKey(e) { if (e.key === 'Escape') { e.preventDefault(); close(); } }
  const detach = () => { _modalCancel = null; box.classList.remove('pk-wide'); document.removeEventListener('keydown', onKey); };
  const close = () => { detach(); ov.classList.remove('show'); if (onClose) onClose(); };
  const reopen = n => openLibSettings(n, onClose);
  // 弹二次确认/输入框前先让出 #modal，问完再回到本框
  const sub = fn => { detach(); return fn(); };
  const row = (label, ...kids) => { const r = el('div', 'frow'); r.appendChild(el('label', null, label)); kids.forEach(k => r.appendChild(k)); return r; };

  box.appendChild(el('h3', null, '媒体库设置'));

  const nameIn = el('input'); nameIn.value = l.name; nameIn.classList.add('grow');
  nameIn.onchange = async () => {
    const v = nameIn.value.trim();
    if (!v || v === l.name) { nameIn.value = l.name; return; }
    const r = await apiPost('/api/lib/update', { name: l.name, rename: v });
    if (r.error) { await sub(() => uiAlert(r.error)); reopen(l.name); return; }
    reopen(r.name);
  };
  box.appendChild(row('名称', nameIn));

  const viewSel = el('select'); viewSel.style.width = '120px';
  [['works', '作品'], ['makers', '社团']].forEach(([v, t]) => { const o = el('option', null, t); o.value = v; viewSel.appendChild(o); });
  viewSel.value = l.view === 'makers' ? 'makers' : 'works';
  viewSel.onchange = () => apiPost('/api/lib/update', { name: l.name, view: viewSel.value });
  box.appendChild(row('默认显示', viewSel));

  // 文件夹：一张卡片一个（编辑 / 移除），末尾同尺寸的「+」卡片添加（Web 只能手填本机路径，桌面端走系统选择框）
  const folders = el('div', 'grow foldcards');
  (l.folders || []).forEach(f => {
    const fc = el('div', 'foldcard');
    const ed = el('button', 'mini', '编辑');
    const rm = el('button', 'mini danger', '移除');
    ed.onclick = async () => {
      const to = ((await sub(() => uiPrompt('', f, '编辑文件夹'))) || '').trim();
      if (to && to !== f) {
        const r = await apiPost('/api/lib/editfolder', { name: l.name, folder: f, to });
        if (r.error) await uiAlert(r.error);
      }
      reopen(l.name);
    };
    rm.onclick = async () => { await apiPost('/api/lib/removefolder', { name: l.name, folder: f }); reopen(l.name); };
    const acts = el('div', 'fc-acts'); acts.append(ed, rm);
    const fp = el('div', 'fc-path', f); fp.title = f;
    fc.append(fp, acts); folders.appendChild(fc);
  });
  const addF = el('div', 'foldcard add');
  addF.append(el('div', 'fc-plus', '+'), el('div', 'fc-label', '添加文件夹'));
  addF.onclick = async () => {
    const folder = ((await sub(() => uiPrompt('文件夹路径（本机绝对路径，如 D:\\ASMR）：', '', '添加文件夹')))|| '').trim();
    if (folder) {
      const r = await apiPost('/api/lib/addfolder', { name: l.name, folder });
      if (r.error) await uiAlert(r.error);
    }
    reopen(l.name);
  };
  folders.appendChild(addF);
  const fRow = row('文件夹', folders); fRow.classList.add('top');
  box.appendChild(fRow);

  const scan = el('button', 'icon-btn', '扫描元数据');
  const rescan = el('button', 'icon-btn', '重新扫描元数据');
  scan.onclick = async () => { await apiPost('/api/lib/scan', { name: l.name, force: false }); pollScan(); };
  rescan.onclick = async () => {
    const ok = await sub(() => uiConfirm(`重新扫描"${l.name}"会强制重新抓取全部元数据，较慢。继续？`));
    if (ok) await apiPost('/api/lib/scan', { name: l.name, force: true });
    await reopen(l.name);
    if (ok) pollScan();
  };
  box.appendChild(row('元数据', scan, rescan));
  const status = el('div', 'note'); status.id = 'libScanStatus'; status.style.display = 'none';
  box.appendChild(status);

  const btns = el('div', 'modal-btns');
  const del = el('button', 'icon-btn danger', '删除媒体库');
  del.style.marginRight = 'auto';
  del.onclick = async () => {
    const ok = await sub(() => uiConfirm(`确定删除媒体库"${l.name}"吗？
不会删除本地文件，已导入的作品记录保留。`, { danger: true }));
    if (!ok) { reopen(l.name); return; }
    await apiPost('/api/lib/delete', { name: l.name });
    if (onClose) onClose();
  };
  const done = el('button', 'icon-btn primary', '关闭');
  done.onclick = close;
  btns.append(del, done); box.appendChild(btns);

  _modalCancel = close;
  document.addEventListener('keydown', onKey);
  ov.classList.add('show');
  pollScan();   // 可能有扫描正在进行
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
