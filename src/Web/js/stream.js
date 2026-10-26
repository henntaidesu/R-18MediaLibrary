// stream.js —— 远程串流：作品里的 Windows 程序在电脑上运行，画面经 WebSocket（/api/stream）传到这里，
// 鼠标/触摸/键盘回传给电脑注入。协议见 Services/GameStream.cs：二进制帧首字节 1=JPEG 画面、2=16 位 PCM 声音。
// 同看图一样算一层历史：返回手势只关掉串流页（core.js popstate），关掉串流不结束程序，结束要点「结束程序」。
// 账号访问的「在服务器上登录」也走这里（openLogin）：电脑上开的是登录浏览器，登录成功它自己关窗、串流随之结束。
// 那一路由服务端 Services/LoginStream 直接用浏览器自己出图、收输入（不依赖服务器桌面），协议相同；
// 键盘事件因此额外带上 key（可打印字符直接插入页面），游戏串流忽略这个字段。

// 触屏按键：RPG Maker 系 Z/Enter/空格=确定、X/Esc=取消、Shift=冲刺，其它引擎也大多沿用这套
const GS_DPAD = [null, ['ArrowUp', '↑'], null, ['ArrowLeft', '←'], null, ['ArrowRight', '→'], null, ['ArrowDown', '↓'], null];
const GS_KEYS = [['KeyZ', 'Z 确定'], ['KeyX', 'X 取消'], ['Enter', 'Enter'], ['Escape', 'Esc'],
                 ['ShiftLeft', 'Shift'], ['ControlLeft', 'Ctrl'], ['Space', '空格'], ['Tab', 'Tab']];

const GS = {
  isOpen: false, ws: null, frameW: 0, frameH: 0, login: false, onHide: null, exitText: '', gotAny: false,
  decoding: false, pending: null, held: new Set(), lastMove: 0, exited: false,
  audioOn: false, actx: null, rate: 48000, nextT: 0,

  open(id, n) {
    this.start(`api/stream?id=${enc(id)}&path=${enc(n.rel)}`, n.name, false);
  },
  // 登录 pixiv / FANBOX：电脑上开内嵌浏览器，在这里操作它；关掉串流页时 onHide 刷新账号页
  openLogin(site, title, onHide) {
    this.start(`api/stream?login=${enc(site)}`, `登录 ${title}（电脑上的浏览器）`, true);
    this.onHide = onHide;
  },
  start(path, title, login) {
    this.hide();
    this.isOpen = true; this.exited = false; this.frameW = this.frameH = 0;
    this.login = login; this.onHide = null; this.exitText = ''; this.gotAny = false;
    $('stitle').textContent = title;
    $('saudio').style.display = login ? 'none' : '';
    $('squit').textContent = login ? '✕ 关闭浏览器' : '⏻ 结束程序';
    this.msg('正在连接…');
    $('scanvas').getContext('2d').clearRect(0, 0, $('scanvas').width, $('scanvas').height);
    $('streamOverlay').classList.add('show');
    this.buildPad();
    // 触屏设备默认展开按键：没有实体键盘就没法操作 RPG 类游戏
    $('spad').classList.toggle('show', !login && matchMedia('(pointer: coarse)').matches);
    this.syncBtns();
    history.pushState({ nav: true, gs: true }, '');
    // 地址按页面所在目录解析（相对路径），不能写成 /api/stream：经门户代理打开时页面在 /api/proxy/<卡片>/ 底下，
    // 根绝对地址会落到门户自己的根路径上。普通请求有门户按 Referer 兜底转回来，WebSocket 握手不带 Referer，
    // 直接被门户拒掉（浏览器只看到关闭码 1006）
    const url = new URL(path, location.href);
    url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    url.hash = '';
    const ws = new WebSocket(url.href);
    ws.binaryType = 'arraybuffer';
    ws.onmessage = e => this.onMessage(e.data);
    ws.onclose = (e) => {
      if (this.ws !== ws) return;
      this.ws = null;
      if (this.exited) return;
      // 一条消息都没收到就断了：多半根本没连上本程序（门户代理连不上上游、被拒），把关闭码带上便于排查
      if (!this.gotAny) this.msg(`连接已断开：没有连上电脑上的串流（关闭码 ${e.code}）`);
      else this.msg(this.login ? '连接已断开' : '连接已断开（程序仍在电脑上运行，重新打开即可接着玩）');
    };
    this.ws = ws;
    if (this.audioOn) ws.addEventListener('open', () => this.send({ t: 'audio', on: true }));
  },
  close() {
    if (!this.isOpen) return;
    if (history.state && history.state.gs) history.back();   // 由 popstate 再调 hide()
    else this.hide();
  },
  hide() {
    if (!this.isOpen) return;
    this.isOpen = false;
    this.releaseAll();
    const ws = this.ws; this.ws = null;
    if (ws) ws.close();
    if (this.actx) { this.actx.close().catch(() => {}); this.actx = null; }
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
    $('streamOverlay').classList.remove('show');
    const cb = this.onHide; this.onHide = null;
    if (cb) cb(this.exitText);
  },
  send(o) { if (this.ws && this.ws.readyState === 1) this.ws.send(JSON.stringify(o)); },
  msg(text) { $('smsg').textContent = text || ''; },

  onMessage(data) {
    this.gotAny = true;
    if (typeof data === 'string') {
      let m; try { m = JSON.parse(data); } catch (e) { return; }
      if (m.t === 'status') this.msg(m.text);
      else if (m.t === 'audio') { this.rate = m.rate || 48000; this.nextT = 0; }
      else if (m.t === 'exit') {
        this.exited = true; this.exitText = m.text || '';
        this.msg(m.text || (this.login ? '浏览器已关闭' : '程序已退出'));
        if (this.login) setTimeout(() => { if (this.exited) this.close(); }, 1200);   // 登录窗口已关，串流页也自动收起
      }
      return;
    }
    const kind = new Uint8Array(data, 0, 1)[0];
    if (kind === 1) this.onFrame(new Blob([new Uint8Array(data, 1)], { type: 'image/jpeg' }));
    else if (kind === 2) this.onAudio(data);
  },
  // 画面：只画最新一帧，解码跟不上时中间的帧直接丢掉，不让延迟越积越大
  onFrame(blob) {
    if (this.decoding) { this.pending = blob; return; }
    this.decoding = true;
    createImageBitmap(blob).then(bmp => {
      const c = $('scanvas');
      if (bmp.width !== this.frameW || bmp.height !== this.frameH) {
        this.frameW = c.width = bmp.width; this.frameH = c.height = bmp.height;
        this.fit();
      }
      c.getContext('2d').drawImage(bmp, 0, 0);
      bmp.close();
      if (!this.exited) this.msg('');
    }).catch(() => {}).finally(() => {
      this.decoding = false;
      const next = this.pending; this.pending = null;
      if (next && this.isOpen) this.onFrame(next);
    });
  },
  // 画面等比铺满可用区域（可放大），坐标换算靠画布的实际显示框
  fit() {
    if (!this.frameW) return;
    const st = $('sstage'), c = $('scanvas');
    const s = Math.min(st.clientWidth / this.frameW, st.clientHeight / this.frameH);
    c.style.width = Math.floor(this.frameW * s) + 'px';
    c.style.height = Math.floor(this.frameH * s) + 'px';
  },

  // ---------- 声音 ----------
  toggleAudio() {
    this.audioOn = !this.audioOn;
    if (this.audioOn && !this.actx) { this.actx = new (window.AudioContext || window.webkitAudioContext)(); this.nextT = 0; }
    if (!this.audioOn && this.actx) { this.actx.close().catch(() => {}); this.actx = null; }
    this.send({ t: 'audio', on: this.audioOn });
    this.syncBtns();
  },
  onAudio(data) {
    const ctx = this.actx;
    if (!ctx || !this.audioOn) return;
    const pcm = new Int16Array(data.slice(1));   // 偏移 1 不是 2 字节对齐，先拷一份
    const frames = pcm.length >> 1;
    if (!frames) return;
    const buf = ctx.createBuffer(2, frames, this.rate);
    const l = buf.getChannelData(0), r = buf.getChannelData(1);
    for (let i = 0; i < frames; i++) { l[i] = pcm[i * 2] / 32768; r[i] = pcm[i * 2 + 1] / 32768; }
    const src = ctx.createBufferSource(); src.buffer = buf; src.connect(ctx.destination);
    // 留 80ms 缓冲抗抖动；积压超过半秒说明掉队了，丢掉重来以免声画越差越远
    const now = ctx.currentTime;
    if (this.nextT < now + 0.03 || this.nextT > now + 0.5) this.nextT = now + 0.08;
    src.start(this.nextT);
    this.nextT += buf.duration;
  },

  // ---------- 输入 ----------
  pos(e) {
    const r = $('scanvas').getBoundingClientRect();
    return { x: (e.clientX - r.left) / r.width, y: (e.clientY - r.top) / r.height };
  },
  key(code, down, key) {
    if (down) this.held.add(code); else if (!this.held.delete(code)) return;
    this.send({ t: 'k', a: down ? 'down' : 'up', code, key: key || '' });
  },
  releaseAll() { [...this.held].forEach(c => this.key(c, false)); },
  toggleKeys() { $('spad').classList.toggle('show'); this.syncBtns(); setTimeout(() => this.fit(), 0); },
  fullscreen() {
    const o = $('streamOverlay');
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
    else if (o.requestFullscreen) o.requestFullscreen().catch(() => {});
  },
  // 手机软键盘发不出按键码：整段文字交给电脑按 Unicode 打进当前焦点（账号、密码都靠它输入）
  async typeText() {
    const s = await uiPrompt('输入的文字会打进电脑上程序当前的输入框（先点一下要输入的位置）。', '', '输入文字');
    if (s) this.send({ t: 'text', s });
  },
  async quit() {
    if (this.login) { this.send({ t: 'quit' }); return; }
    if (!await uiConfirm('关闭电脑上的这个程序？未存档的进度会丢失。', { title: '结束程序', okText: '结束', danger: true })) return;
    this.send({ t: 'quit' });
  },
  syncBtns() {
    $('saudio').textContent = this.audioOn ? '🔊 声音' : '🔇 声音';
    $('saudio').classList.toggle('primary', this.audioOn);
    $('skeys').classList.toggle('primary', $('spad').classList.contains('show'));
  },
  buildPad() {
    const pad = $('spad');
    if (pad.childElementCount) return;
    const mk = (code, label) => {
      const b = el('button', null, label);
      const up = () => { b.classList.remove('on'); this.key(code, false); };
      b.addEventListener('pointerdown', e => { e.preventDefault(); b.setPointerCapture(e.pointerId); b.classList.add('on'); this.key(code, true); });
      b.addEventListener('pointerup', up); b.addEventListener('pointercancel', up);
      b.addEventListener('contextmenu', e => e.preventDefault());
      return b;
    };
    const dpad = el('div', 'dpad');
    GS_DPAD.forEach(k => dpad.appendChild(k ? mk(k[0], k[1]) : el('span')));
    const keys = el('div', 'skeys');
    GS_KEYS.forEach(k => keys.appendChild(mk(k[0], k[1])));
    pad.append(dpad, keys);
  },
};

(() => {
  const c = $('scanvas');
  const btn = e => (e.pointerType === 'mouse' ? e.button : 0);
  c.addEventListener('pointerdown', e => {
    e.preventDefault();
    c.setPointerCapture(e.pointerId);
    const p = GS.pos(e);
    GS.send({ t: 'm', a: 'down', b: btn(e), x: p.x, y: p.y });
  });
  c.addEventListener('pointermove', e => {
    // 移动事件很密，限到约 60 次/秒
    const now = performance.now();
    if (now - GS.lastMove < 16) return;
    GS.lastMove = now;
    const p = GS.pos(e);
    GS.send({ t: 'm', a: 'move', x: p.x, y: p.y });
  });
  const up = e => { const p = GS.pos(e); GS.send({ t: 'm', a: 'up', b: btn(e), x: p.x, y: p.y }); };
  c.addEventListener('pointerup', up);
  c.addEventListener('pointercancel', up);
  c.addEventListener('contextmenu', e => e.preventDefault());
  c.addEventListener('wheel', e => { e.preventDefault(); GS.send({ t: 'w', d: e.deltaY > 0 ? -120 : 120 }); }, { passive: false });
  // 实体键盘：串流页打开且没有弹窗时，按键全部转给电脑上的程序
  const onKey = down => e => {
    if (!GS.isOpen || $('modal').classList.contains('show')) return;
    e.preventDefault();
    GS.key(e.code, down, e.key);
  };
  document.addEventListener('keydown', onKey(true));
  document.addEventListener('keyup', onKey(false));
  window.addEventListener('blur', () => GS.releaseAll());   // 切走时松开所有键，免得电脑那边一直按着
  window.addEventListener('resize', () => GS.fit());
  document.addEventListener('fullscreenchange', () => setTimeout(() => GS.fit(), 50));
})();
