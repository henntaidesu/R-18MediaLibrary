// stream.js —— 远程串流：作品里的 Windows 程序在电脑上运行，画面经 WebSocket（/api/stream）传到这里，
// 鼠标/触摸/键盘回传给电脑注入。协议见 Services/GameStream.cs：二进制帧首字节 1=JPEG 画面、2=16 位 PCM 声音。
// 同看图一样算一层历史：返回手势只关掉串流页（core.js popstate），关掉串流不结束程序，结束要点「结束程序」。

// 触屏按键：RPG Maker 系 Z/Enter/空格=确定、X/Esc=取消、Shift=冲刺，其它引擎也大多沿用这套
const GS_DPAD = [null, ['ArrowUp', '↑'], null, ['ArrowLeft', '←'], null, ['ArrowRight', '→'], null, ['ArrowDown', '↓'], null];
const GS_KEYS = [['KeyZ', 'Z 确定'], ['KeyX', 'X 取消'], ['Enter', 'Enter'], ['Escape', 'Esc'],
                 ['ShiftLeft', 'Shift'], ['ControlLeft', 'Ctrl'], ['Space', '空格'], ['Tab', 'Tab']];

const GS = {
  isOpen: false, ws: null, frameW: 0, frameH: 0,
  decoding: false, pending: null, held: new Set(), lastMove: 0, exited: false,
  audioOn: false, actx: null, rate: 48000, nextT: 0,

  open(id, n) {
    this.hide();
    this.isOpen = true; this.exited = false; this.frameW = this.frameH = 0;
    $('stitle').textContent = n.name;
    this.msg('正在连接…');
    $('scanvas').getContext('2d').clearRect(0, 0, $('scanvas').width, $('scanvas').height);
    $('streamOverlay').classList.add('show');
    this.buildPad();
    // 触屏设备默认展开按键：没有实体键盘就没法操作 RPG 类游戏
    $('spad').classList.toggle('show', matchMedia('(pointer: coarse)').matches);
    this.syncBtns();
    history.pushState({ nav: true, gs: true }, '');
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const ws = new WebSocket(`${proto}//${location.host}/api/stream?id=${enc(id)}&path=${enc(n.rel)}`);
    ws.binaryType = 'arraybuffer';
    ws.onmessage = e => this.onMessage(e.data);
    ws.onclose = () => {
      if (this.ws !== ws) return;
      this.ws = null;
      this.msg(this.exited ? '程序已退出' : '连接已断开（程序仍在电脑上运行，重新打开即可接着玩）');
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
  },
  send(o) { if (this.ws && this.ws.readyState === 1) this.ws.send(JSON.stringify(o)); },
  msg(text) { $('smsg').textContent = text || ''; },

  onMessage(data) {
    if (typeof data === 'string') {
      let m; try { m = JSON.parse(data); } catch (e) { return; }
      if (m.t === 'status') this.msg(m.text);
      else if (m.t === 'audio') { this.rate = m.rate || 48000; this.nextT = 0; }
      else if (m.t === 'exit') { this.exited = true; this.msg('程序已退出'); }
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
  key(code, down) {
    if (down) this.held.add(code); else if (!this.held.delete(code)) return;
    this.send({ t: 'k', a: down ? 'down' : 'up', code });
  },
  releaseAll() { [...this.held].forEach(c => this.key(c, false)); },
  toggleKeys() { $('spad').classList.toggle('show'); this.syncBtns(); setTimeout(() => this.fit(), 0); },
  fullscreen() {
    const o = $('streamOverlay');
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
    else if (o.requestFullscreen) o.requestFullscreen().catch(() => {});
  },
  async quit() {
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
    GS.key(e.code, down);
  };
  document.addEventListener('keydown', onKey(true));
  document.addEventListener('keyup', onKey(false));
  window.addEventListener('blur', () => GS.releaseAll());   // 切走时松开所有键，免得电脑那边一直按着
  window.addEventListener('resize', () => GS.fit());
  document.addEventListener('fullscreenchange', () => setTimeout(() => GS.fit(), 50));
})();
