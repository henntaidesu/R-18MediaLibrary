// viewer.js —— 查看作品文件树 + 视频/音频播放队列 + 灯箱。
// ========== 查看作品（文件树）==========
function fmtSize(b) {
  if (b >= 1073741824) return (b / 1073741824).toFixed(2) + ' GB';
  if (b >= 1048576) return (b / 1048576).toFixed(1) + ' MB';
  if (b >= 1024) return (b / 1024).toFixed(1) + ' KB';
  return (b || 0) + ' B';
}
function fileIcon(ext) {
  if (IMG_EXTS.includes(ext)) return '🖼️';
  if (VIDEO_EXTS.includes(ext)) return '🎬';
  if (AUDIO_EXTS.includes(ext)) return '🎵';
  return '📄';
}
// 查看作品视图模式：thumb=缩略图网格 / list=文件列表（折叠树+文件大小，对齐 WPF）
let fileViewMode = 'thumb';
// 子文件夹页：文件夹进入时携带子节点（ctx.nodes）。作品根目录直接画在详情页里（renderDetail → drawFiles）
async function renderFiles(ctx) {
  $('title').textContent = ctx.name || ctx.id;
  await drawFiles($('content'), ctx);
}
// 把文件视图画进 host：视图切换栏 + 缩略图网格/文件列表。ctx.nodes 无则首次拉取整棵树。
// label 有值即嵌在详情页里：切换栏改为行内的小节标题，不当页顶工具栏吸顶
async function drawFiles(host, ctx, label) {
  let nodes = ctx.nodes;
  if (!nodes) {
    host.innerHTML = '<div class="empty"><span class="spin"></span> 加载中…</div>';
    let d; try { d = await api('/api/files?id=' + enc(ctx.id)); } catch (e) { return; }
    if (d.error) { host.innerHTML = ''; host.appendChild(el('div', 'empty', d.error)); return; }
    nodes = d.nodes || [];
  }
  ctx.nodes = nodes;   // 缓存：切换视图无需重新请求
  host.innerHTML = '';
  // 视图切换栏：缩略图 / 文件列表
  const bar = el('div', label ? 'toolbar inline dfiles-bar' : 'toolbar');
  if (label) bar.appendChild(el('span', 'dfiles-title', label));
  const tb = el('button', 'icon-btn' + (fileViewMode === 'thumb' ? ' primary' : ''), '▦ 缩略图');
  const lb = el('button', 'icon-btn' + (fileViewMode === 'list' ? ' primary' : ''), '☰ 文件列表');
  tb.onclick = () => { if (fileViewMode !== 'thumb') { fileViewMode = 'thumb'; drawFiles(host, ctx, label); } };
  lb.onclick = () => { if (fileViewMode !== 'list') { fileViewMode = 'list'; drawFiles(host, ctx, label); } };
  bar.append(tb, lb); host.appendChild(bar);
  if (!nodes.length) { host.appendChild(el('div', 'empty', '此文件夹为空')); return; }
  if (fileViewMode === 'list') host.appendChild(drawFileTree(nodes, ctx.id));
  else drawFileGrid(nodes, ctx.id, host);   // 缩略图网格自行分批填充到 host
}
// 文件列表视图：递归折叠树，文件夹默认收起，文件显示类型图标+名称+大小（镜像 WPF 文件列表）
function drawFileTree(nodes, id) {
  const wrap = el('div', 'tree');
  buildTreeLevel(wrap, nodes, id, 0);
  return wrap;
}
function buildTreeLevel(host, nodes, id, depth) {
  nodes.forEach(n => {
    const ext = (n.ext || '').toLowerCase();
    if (n.dir) {
      const row = el('div', 'trow'); row.style.paddingLeft = (8 + depth * 18) + 'px';
      const arrow = el('span', 'arrow', '▸');
      row.append(arrow, el('span', 'ticon', '📁'), el('span', 'tname', n.name), el('span', 'tsize', `${(n.children || []).length} 项`));
      const childBox = el('div'); childBox.style.display = 'none';
      let built = false, open = false;
      row.onclick = () => {
        open = !open; arrow.textContent = open ? '▾' : '▸';
        if (open && !built) { buildTreeLevel(childBox, n.children || [], id, depth + 1); built = true; }
        childBox.style.display = open ? '' : 'none';
      };
      host.append(row, childBox);
    } else {
      const row = el('div', 'trow file'); row.style.paddingLeft = (8 + depth * 18 + 18) + 'px';
      row.append(el('span', 'ticon', fileIcon(ext)), el('span', 'tname', n.name), el('span', 'tsize', fmtSize(n.size)));
      const url = fileUrl(id, n.rel);
      if (IMG_EXTS.includes(ext)) {
        const sib = nodes.filter(x => !x.dir && IMG_EXTS.includes((x.ext || '').toLowerCase())).map(x => fileUrl(id, x.rel));
        row.onclick = () => LB.open(sib, Math.max(0, sib.indexOf(url)));
      } else if (VIDEO_EXTS.includes(ext)) {
        const q = nodes.filter(x => !x.dir && VIDEO_EXTS.includes((x.ext || '').toLowerCase())).map(x => ({ url: fileUrl(id, x.rel), name: x.name }));
        row.onclick = () => playVideoQueue(q, Math.max(0, q.findIndex(v => v.url === url)));
      } else if (AUDIO_EXTS.includes(ext)) {
        const q = nodes.filter(x => !x.dir && AUDIO_EXTS.includes((x.ext || '').toLowerCase())).map(x => ({ url: fileUrl(id, x.rel), name: x.name }));
        row.onclick = () => playAudioQueue(q, Math.max(0, q.findIndex(a => a.url === url)));
      } else {
        row.onclick = () => window.open(url, '_blank');
      }
      host.appendChild(row);
    }
  });
}
function drawFileGrid(nodes, id, host) {
  // 本层图片集合：图片缩略图点开后可在它们之间左右滑动（含全部图片，与是否已渲染无关）
  const imgs = nodes.filter(n => !n.dir && IMG_EXTS.includes((n.ext || '').toLowerCase())).map(n => fileUrl(id, n.rel));
  // 本层音频/视频集合：组成播放队列，支持上一/下一与自动续播（nodes 已按名称排序）
  const audios = nodes.filter(n => !n.dir && AUDIO_EXTS.includes((n.ext || '').toLowerCase())).map(n => ({ url: fileUrl(id, n.rel), name: n.name }));
  const videos = nodes.filter(n => !n.dir && VIDEO_EXTS.includes((n.ext || '').toLowerCase())).map(n => ({ url: fileUrl(id, n.rel), name: n.name }));
  const makeCard = (n) => {
    const ext = (n.ext || '').toLowerCase();
    const card = el('div', 'fcard');
    const thumb = el('div', 'fthumb');
    if (n.dir) {
      thumb.appendChild(el('div', 'big', '📁'));
      thumb.appendChild(el('div', 'fcount', `${(n.children || []).length} 项`));
      card.onclick = () => pushView('files', { id, name: n.name, nodes: n.children || [] });
    } else if (IMG_EXTS.includes(ext)) {
      const img = el('img'); img.loading = 'lazy'; img.src = thumbUrl(id, n.rel); thumb.appendChild(img);
      const url = fileUrl(id, n.rel);
      card.onclick = () => LB.open(imgs, Math.max(0, imgs.indexOf(url)));
    } else if (VIDEO_EXTS.includes(ext)) {
      thumb.appendChild(el('div', 'big', '🎬'));
      const url = fileUrl(id, n.rel);
      card.onclick = () => playVideoQueue(videos, Math.max(0, videos.findIndex(v => v.url === url)));
    } else if (AUDIO_EXTS.includes(ext)) {
      thumb.appendChild(el('div', 'big', '🎵'));
      const url = fileUrl(id, n.rel);
      card.onclick = () => playAudioQueue(audios, Math.max(0, audios.findIndex(a => a.url === url)));
    } else {
      thumb.appendChild(el('div', 'big', '📄'));
      card.onclick = () => window.open(fileUrl(id, n.rel), '_blank');
    }
    card.append(thumb, el('div', 'fname', n.name));
    return card;
  };
  const grid = el('div', 'fgrid'); host.appendChild(grid);
  lazyGridFill(grid, nodes, makeCard);   // 分批渲染缩略图卡片（图片多时避免一次性建 DOM/加载）
}
// ---------- 视频播放队列 ----------
let videoQueue = [], videoIndex = -1;
function playVideoQueue(queue, index) {
  videoQueue = queue || []; videoIndex = index;
  if (!videoQueue.length) return;
  $('videoOverlay').classList.add('show');
  playVideoAt(index);
}
function playVideoAt(i) {
  if (i < 0 || i >= videoQueue.length) return;
  videoIndex = i;
  const it = videoQueue[i], v = $('vplayer');
  v.src = it.url; v.play().catch(() => {});
  $('vtitle').textContent = it.name + (videoQueue.length > 1 ? `　${i + 1} / ${videoQueue.length}` : '');
  $('vprev').disabled = videoIndex <= 0;
  $('vnext').disabled = videoIndex >= videoQueue.length - 1;
}
function videoPrev() { if (videoIndex > 0) playVideoAt(videoIndex - 1); }
function videoNext() { if (videoIndex < videoQueue.length - 1) playVideoAt(videoIndex + 1); }   // 到队尾即停
function closeVideo() {
  const v = $('vplayer'); v.pause(); v.removeAttribute('src'); v.load();
  $('videoOverlay').classList.remove('show'); videoQueue = []; videoIndex = -1;
}
$('vplayer').addEventListener('ended', videoNext);   // 自然结束自动续播下一个
$('videoOverlay').addEventListener('click', e => { if (e.target.id === 'videoOverlay') closeVideo(); });

// ---------- 音频播放队列 ----------
let audioQueue = [], audioIndex = -1;
function playAudioQueue(queue, index) {
  audioQueue = queue || []; audioIndex = index;
  if (!audioQueue.length) return;
  $('audioBar').classList.add('show');
  playAudioAt(index);
}
function playAudioAt(i) {
  if (i < 0 || i >= audioQueue.length) return;
  audioIndex = i;
  const it = audioQueue[i], a = $('aplayer');
  $('aname').textContent = it.name;
  a.src = it.url; a.play().catch(() => {});
  $('aprev').disabled = audioQueue.length <= 1;
  $('anext').disabled = audioIndex >= audioQueue.length - 1;
  if ($('audioPlaylist').classList.contains('show')) renderAudioPlaylist();
}
function audioPrev() {
  const a = $('aplayer');
  // 已播放超过 3 秒或已是第一首：回到本曲开头；否则上一曲
  if (a.currentTime > 3 || audioIndex <= 0) { a.currentTime = 0; a.play().catch(() => {}); return; }
  playAudioAt(audioIndex - 1);
}
function audioNext() { if (audioIndex < audioQueue.length - 1) playAudioAt(audioIndex + 1); }   // 到队尾即停
function closeAudio() {
  const a = $('aplayer'); a.pause(); a.removeAttribute('src'); a.load();
  $('audioBar').classList.remove('show');
  $('audioPlaylist').classList.remove('show'); $('sleepMenu').classList.remove('show');
  audioQueue = []; audioIndex = -1; clearSleep();
}
$('aplayer').addEventListener('ended', () => {
  if (stopAfterCurrent) { stopAfterCurrent = false; clearSleep(); return; }   // 定时到点：本曲放完即停，不续播
  audioNext();
});   // 自然结束自动续播下一首

// 播放列表弹窗
function toggleAudioPlaylist() {
  const p = $('audioPlaylist');
  $('sleepMenu').classList.remove('show');
  if (p.classList.contains('show')) { p.classList.remove('show'); return; }
  renderAudioPlaylist(); p.classList.add('show');
}
function renderAudioPlaylist() {
  const p = $('audioPlaylist'); p.innerHTML = '';
  if (!audioQueue.length) { p.appendChild(el('div', 'apempty', '播放列表为空')); return; }
  audioQueue.forEach((it, i) => {
    const cur = i === audioIndex;
    const r = el('div', 'aprow' + (cur ? ' cur' : ''));
    r.appendChild(el('span', 'apn', cur ? '▶' : String(i + 1)));
    r.appendChild(el('span', 'apt', it.name));
    r.onclick = () => playAudioAt(i);
    p.appendChild(r);
  });
}

// 睡眠定时（到点后不打断当前曲：本曲放完即停，不再续播下一条）
const SLEEP_PRESETS = [['15 分钟', 15], ['30 分钟', 30], ['45 分钟', 45], ['60 分钟', 60], ['90 分钟', 90]];
let sleepTimer = null, sleepUntil = 0, stopAfterCurrent = false;
function toggleSleepMenu() {
  const m = $('sleepMenu');
  $('audioPlaylist').classList.remove('show');
  if (m.classList.contains('show')) { m.classList.remove('show'); return; }
  m.innerHTML = '';
  // 手动输入分钟数
  const ic = el('div', 'sleep-input');
  const inp = el('input'); inp.type = 'number'; inp.min = '1'; inp.placeholder = '自定义分钟';
  inp.onkeydown = e => { if (e.key === 'Enter') confirmManual(); };
  const ok = el('button', null, '确定'); ok.onclick = confirmManual;
  function confirmManual() {
    const v = parseInt(inp.value, 10);
    if (v > 0) { m.classList.remove('show'); setSleep(v); }
  }
  ic.appendChild(inp); ic.appendChild(ok); m.appendChild(ic);
  // 关闭定时
  addSleepRow(m, '关闭定时', () => clearSleep());
  // 播放完当前（本曲放完即停）
  addSleepRow(m, '播放完当前', () => stopAtCurrentEnd());
  // 预设档位
  SLEEP_PRESETS.forEach(([label, mins]) => addSleepRow(m, label, () => setSleep(mins)));
  m.classList.add('show');
  inp.focus();
}
function addSleepRow(m, label, fn) {
  const r = el('div', 'aprow'); r.appendChild(el('span', 'apt', label));
  r.onclick = () => { m.classList.remove('show'); fn(); };
  m.appendChild(r);
}
function setSleep(mins) {
  clearSleep();
  if (mins <= 0) return;
  sleepUntil = Date.now() + mins * 60000;
  sleepTimer = setInterval(tickSleep, 1000);
  updateSleepBtn();
}
function tickSleep() {
  if (sleepUntil - Date.now() <= 0) {        // 到点：不打断当前曲，待其播完即停
    if (sleepTimer) clearInterval(sleepTimer);
    sleepTimer = null; sleepUntil = 0;
    stopAtCurrentEnd();
    return;
  }
  updateSleepBtn();
}
function stopAtCurrentEnd() {
  stopAfterCurrent = true;
  const b = $('asleepBtn'); b.textContent = '⏹'; b.title = '本曲放完即停'; b.classList.add('on');
}
function updateSleepBtn() {
  const remain = Math.max(0, sleepUntil - Date.now());
  const mm = Math.floor(remain / 60000), ss = Math.floor((remain % 60000) / 1000);
  const b = $('asleepBtn'); b.textContent = `${mm}:${String(ss).padStart(2, '0')}`; b.classList.add('on');
}
function clearSleep() {
  if (sleepTimer) clearInterval(sleepTimer);
  sleepTimer = null; sleepUntil = 0; stopAfterCurrent = false;
  const b = $('asleepBtn'); b.textContent = '⏱'; b.title = '睡眠定时'; b.classList.remove('on');
}

// ---------- 灯箱 ----------
// 看图也算一层历史：打开时压一个历史项，系统返回手势/返回键（popstate，见 core.js）只关掉看图、不离开当前页，
// 与逐级返回同一套逻辑。✕、点空白、Esc、上下滑也都经 history.back() 关闭，历史栈里不会残留多出的一层。
const LB = {
  list: [], idx: 0, isOpen: false,
  open(list, idx) {
    this.list = list; this.idx = idx; $('lbimg').src = list[idx]; $('lightbox').classList.add('show');
    if (!this.isOpen) { this.isOpen = true; history.pushState({ nav: true, lb: true }, ''); }
  },
  close() {
    if (!this.isOpen) return;
    if (history.state && history.state.lb) history.back();   // 由 popstate 再调 hide()
    else this.hide();
  },
  hide() { this.isOpen = false; $('lightbox').classList.remove('show'); $('lbimg').src = ''; },
  step(d) { if (!this.list.length) return; this.idx = (this.idx + d + this.list.length) % this.list.length; $('lbimg').src = this.list[this.idx]; },
};
$('lightbox').addEventListener('click', e => { if (e.target.id === 'lightbox') LB.close(); });
// 触屏滑动：左右滑切换图片（右滑上一张、左滑下一张），上下滑退出看图（同返回）
let _lbTouchX = null, _lbTouchY = null;
$('lightbox').addEventListener('touchstart', e => {
  if (e.touches.length === 1) { _lbTouchX = e.touches[0].clientX; _lbTouchY = e.touches[0].clientY; }
  else _lbTouchX = _lbTouchY = null;   // 双指缩放不算滑动
}, { passive: true });
$('lightbox').addEventListener('touchend', e => {
  if (_lbTouchX == null) return;
  const dx = e.changedTouches[0].clientX - _lbTouchX, dy = e.changedTouches[0].clientY - _lbTouchY;
  _lbTouchX = _lbTouchY = null;
  if (Math.abs(dy) > 80 && Math.abs(dy) > Math.abs(dx)) LB.close();
  else if (Math.abs(dx) > 40) LB.step(dx > 0 ? -1 : 1);
}, { passive: true });
document.addEventListener('keydown', e => { if (!$('lightbox').classList.contains('show')) return; if (e.key === 'Escape') LB.close(); else if (e.key === 'ArrowLeft') LB.step(-1); else if (e.key === 'ArrowRight') LB.step(1); });
