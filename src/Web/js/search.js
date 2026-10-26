// search.js ——「作品搜索」分区（独立一级导航）：搜索前用下拉选数据源。
//   dlsite  —— 作品号/社团号/目录页搜索、AS 论坛扫描、加入下载队列（本文件）
//   fanbox  —— pawchive 作家搜索与作品下载（fanbox.js）
//   ehentai —— E-Hentai 画廊搜索与下载（ehentai.js）
//   pixiv   —— pixiv 作品搜索与下载（pixiv.js）
// 各来源共用同一条工具栏（来源下拉 + 输入框 + 查询），各自的结果面板并存、按来源切换显示。

// 当前搜索来源；SEARCH_SOURCES 的顺序即下拉框顺序
const SEARCH_SOURCES = [
  { key: 'dlsite', label: 'DLsite', placeholder: '作品号(RJ/BJ/VJ)、社团号(RG) 或 DLsite 链接' },
  { key: 'fanbox', label: 'FANBOX', placeholder: '作家名 / 作家 ID，或 pawchive 作家链接' },
  { key: 'ehentai', label: 'E-Hentai', placeholder: '关键字 / 标签（如 artist:xxx），或 E-Hentai 画廊链接' },
  { key: 'pixiv', label: 'pixiv', placeholder: '关键字 / 标签，或 pixiv 作品链接 / 作品号' },
];
let searchSrc = 'dlsite';

// 分区入口：搭好共用工具栏 + 两套结果面板
function renderSearchArea() {
  $('title').textContent = '';
  // 重置社团状态：让可能残留的后台扫描循环（makerState !== s）自行停止
  if (makerState && makerState.io) makerState.io.disconnect();
  makerState = null;
  const host = $('content'); host.innerHTML = '';

  const bar = el('div', 'toolbar');
  // 来源下拉：放在输入框之前，切换即换占位文案与结果面板
  const src = el('select'); src.id = 'srcSel'; src.className = 'src-sel';
  SEARCH_SOURCES.forEach(s => { const o = el('option', null, s.label); o.value = s.key; src.appendChild(o); });
  src.value = searchSrc;
  src.onchange = () => setSearchSource(src.value);

  const inp = el('input'); inp.className = 'grow'; inp.id = 'sId';
  const btn = el('button', 'icon-btn primary', '查询');
  inp.addEventListener('keydown', e => { if (e.key === 'Enter') runSearchAny(); });
  btn.onclick = runSearchAny;

  // 以下按钮按来源显示：前两个属 DLsite，后两个分属 FANBOX / E-Hentai
  const autoAll = el('button', 'icon-btn', '自动下载：关'); autoAll.id = 'autoAllBtn'; autoAll.style.display = 'none';
  autoAll.title = '开启后自动检测网盘并加入下载，无需逐个点击';
  autoAll.onclick = () => toggleAutoAll(autoAll);
  const backList = el('button', 'icon-btn', '返回作品列表'); backList.id = 'backListBtn'; backList.style.display = 'none'; backList.onclick = goBackToMaker;
  // FANBOX 详情页的「下载」：常驻工具栏，排在 查询 与 返回 之间（详情页自己不再另起一条操作条）
  const fbDl = el('button', 'icon-btn primary', '下载'); fbDl.id = 'fbDlPostBtn'; fbDl.style.display = 'none';
  fbDl.onclick = fbDownloadDetail;
  const fbBack = el('button', 'icon-btn', '返回'); fbBack.id = 'fbBackBtn'; fbBack.style.display = 'none'; fbBack.onclick = fbGoBackToArtists;
  // FANBOX 作家监控没有工具栏入口：列表与间隔在「系统设置 → FANBOX 作家监控」里管，
  // 这里只保留作家主页选择条上的「+ 监控作家」（加监控本就要先选到作家）
  const ehBack = el('button', 'icon-btn', '返回搜索结果'); ehBack.id = 'ehBackBtn'; ehBack.style.display = 'none'; ehBack.onclick = ehGoBackToList;
  // pixiv 详情页的「下载 / 返回」：与 FANBOX 同样常驻工具栏，详情页自己不另起操作条
  const pxDl = el('button', 'icon-btn primary', '下载'); pxDl.id = 'pxDlPostBtn'; pxDl.style.display = 'none';
  pxDl.onclick = pxDownloadDetail;
  const pxBack = el('button', 'icon-btn', '返回'); pxBack.id = 'pxBackBtn'; pxBack.style.display = 'none'; pxBack.onclick = pxGoBackToList;

  bar.append(src, inp, btn, autoAll, backList, fbDl, fbBack, ehBack, pxDl, pxBack);
  host.appendChild(bar);

  // DLsite：社团网格与帖子列表分两个 pane（进帖子列表时只隐藏社团 pane，保留 DOM 与后台扫描，返回即缓存恢复）
  const wrap = el('div'); wrap.id = 'searchResult';
  const mp = el('div'); mp.id = 'makerPane';
  const wp = el('div'); wp.id = 'workPane';
  wrap.append(mp, wp); host.appendChild(wrap);

  // FANBOX：作家结果 / 作家主页两个 pane
  fbBuildPanes(host);

  // E-Hentai：画廊结果 / 画廊详情两个 pane
  ehBuildPanes(host);

  // pixiv：作品结果 / 作品详情两个 pane
  pxBuildPanes(host);

  applySearchSource();
}

// 切来源：换占位文案、显示对应结果面板、隐藏另一来源的工具栏按钮
function setSearchSource(key) {
  if (!SEARCH_SOURCES.some(s => s.key === key)) key = 'dlsite';
  searchSrc = key;
  applySearchSource();
  syncSearchHash();
}

function applySearchSource() {
  const meta = SEARCH_SOURCES.find(s => s.key === searchSrc) || SEARCH_SOURCES[0];
  const inp = $('sId'); if (inp) inp.placeholder = meta.placeholder;
  const dl = searchSrc === 'dlsite', fbOn = searchSrc === 'fanbox', ehOn = searchSrc === 'ehentai';
  const pxOn = searchSrc === 'pixiv';
  // 四套结果面板互斥显示
  const res = $('searchResult'); if (res) res.style.display = dl ? '' : 'none';
  const fb = $('fbResult'); if (fb) fb.style.display = fbOn ? '' : 'none';
  const eh = $('ehResult'); if (eh) eh.style.display = ehOn ? '' : 'none';
  const px = $('pxResult'); if (px) px.style.display = pxOn ? '' : 'none';
  $('count').textContent = '';
  // 工具栏按钮按来源归位：先全部收起，再只恢复当前来源该有的
  const autoAll = $('autoAllBtn'); if (autoAll) autoAll.style.display = (dl && makerState) ? '' : 'none';
  const backList = $('backListBtn'); if (backList) backList.style.display = 'none';
  const fbBack = $('fbBackBtn'); if (fbBack) fbBack.style.display = 'none';
  const fbDl = $('fbDlPostBtn'); if (fbDl) fbDl.style.display = 'none';
  const ehBack = $('ehBackBtn'); if (ehBack) ehBack.style.display = 'none';
  const pxBack = $('pxBackBtn'); if (pxBack) pxBack.style.display = 'none';
  const pxDl = $('pxDlPostBtn'); if (pxDl) pxDl.style.display = 'none';
  if (dl) updateBackListBtn();
  else if (fbOn) fbUpdateToolbarBtns();
  else if (ehOn) ehUpdateBackBtn();
  else pxUpdateToolbarBtns();
}

// 查询入口：按当前来源分流
function runSearchAny() {
  syncSearchHash();   // 把来源与查询词写入地址栏（只 replace 不累积历史）
  if (searchSrc === 'fanbox') return fbRunSearch();
  if (searchSrc === 'ehentai') return ehRunSearch();
  if (searchSrc === 'pixiv') return pxRunSearch();
  return runSearch();
}
function showMakerPane() { $('makerPane').style.display = ''; $('workPane').style.display = 'none'; const b = $('backListBtn'); if (b) b.style.display = 'none'; const a = $('autoAllBtn'); if (a) a.style.display = makerState ? '' : 'none'; }
function showWorkPane() { $('makerPane').style.display = 'none'; $('workPane').style.display = ''; const a = $('autoAllBtn'); if (a) a.style.display = 'none'; }
// 工具栏"返回作品列表/社团作品"按钮：仅从社团/目录列表点进作品时显示（无箭头）
function updateBackListBtn() {
  const b = $('backListBtn'); if (!b) return;
  if (makerReturnId) { b.textContent = (makerState && makerState.catalogUrl) ? '返回作品列表' : '返回社团作品'; b.style.display = ''; }
  else b.style.display = 'none';
}
function goBackToMaker() {
  showMakerPane();
  if (makerState) {
    $('count').textContent = makerState.countText || '';
    // 恢复到点击进入作品前的浏览位置（社团网格 DOM 一直保留，仅切换过 display）
    requestAnimationFrame(() => window.scrollTo(0, makerState.scrollY || 0));
  }
}
// 识别输入：作品号 / 社团号 / DLsite 链接（任意 dlsite.com 链接均可用）
function parseInput(raw) {
  raw = (raw || '').trim();
  if (/dlsite\.com/i.test(raw)) {
    // fsr 搜索/筛选/分类列表页：整页作为目录列表；须先于 maker_id 判定，否则带 maker_id 筛选的 fsr 链接会被误判为社团
    if (/dlsite\.com\/[^/]+\/fsr\//i.test(raw)) return { kind: 'catalog', id: raw };
    let m = raw.match(/product_id\/((?:RJ|BJ|VJ)\d+)/i); if (m) return { kind: 'work', id: m[1].toUpperCase() };
    m = raw.match(/maker_id\/(RG\d+)/i); if (m) return { kind: 'maker', id: m[1].toUpperCase() };
    // 其他任意 DLsite 列表页（分类/排行/搜索结果等）
    return { kind: 'catalog', id: raw };
  }
  const up = raw.toUpperCase();
  if (/^(?:RJ|BJ|VJ)\d+$/.test(up)) return { kind: 'work', id: up };
  if (/^RG\d+$/.test(up)) return { kind: 'maker', id: up };
  return { kind: 'invalid', id: '' };
}
// 跨分区跳转：下载列表「重新搜索」等处调用，切到「DLsite 搜索」分区并立即以该番号查询
function gotoSearch(id) {
  selectSection('dlsearch');   // 同步渲染搜索视图，返回后 #sId 已就绪
  const inp = $('sId');
  if (!inp) return;
  inp.value = id;
  runSearch();
}
async function runSearch() {
  const p = parseInput($('sId').value);
  if (p.kind === 'catalog') { await runGridSearch({ catalogUrl: p.id }); return; }
  if (p.kind === 'maker') { await runGridSearch({ id: p.id }); return; }
  if (p.kind !== 'work') { showWorkPane(); $('workPane').innerHTML = '<div class="empty">请输入作品号(RJ/BJ/VJ)、社团号(RG) 或 DLsite 链接</div>'; return; }
  makerReturnId = null;
  $('sId').value = p.id;
  await runWorkSearch(p.id);
}
let workGen = 0;
async function runWorkSearch(id) {
  const gen = ++workGen;
  showWorkPane();
  updateBackListBtn();   // 工具栏"返回作品列表"按钮据 makerReturnId 显示/隐藏
  const box = $('workPane'); box.innerHTML = '<div class="empty"><span class="spin"></span> 正在查询…</div>';
  let d;
  try { d = await api('/api/search?id=' + enc(id)); } catch (e) { return; }
  if (gen !== workGen) return;
  const showEmpty = (msg) => { box.innerHTML = ''; box.appendChild(el('div', 'empty', msg)); };
  if (d.error) { showEmpty(d.error); return; }
  if (d.existed && !await uiConfirm(`${d.id} ${d.existedName || ''}\n该作品已于 ${d.existedTime || ''} 加入过下载，是否继续？`)) {
    if (makerReturnId) goBackToMaker(); else box.innerHTML = '';
    return;
  }
  if (!d.results.length) { showEmpty('无匹配数据'); return; }
  box.innerHTML = '';
  $('count').textContent = `${d.work.name || d.id} · ${d.results.length} 个帖子`;
  const posts = [];
  d.results.forEach(r => {
    const c = el('div', 'post');
    const head = el('div', 'phead');
    if (r.thumb) { const img = el('img'); img.loading = 'lazy'; img.src = '/api/thumb?url=' + enc(r.thumb); head.appendChild(img); }
    const info = el('div', 'pinfo');
    info.appendChild(el('div', 'rt', r.title));
    if (r.minor) info.appendChild(el('div', 'rm', r.minor));
    if (r.snippet) info.appendChild(el('div', 'rs', r.snippet));
    head.appendChild(info);
    const statusEl = el('div', 'rstatus', '待扫描'); statusEl.style.color = 'var(--muted)'; head.appendChild(statusEl);
    const hostsEl = el('div', 'rhosts');
    c.append(head, hostsEl);
    const post = { url: r.url, statusEl, hostsEl, scanned: false, scanning: false };
    // 点击未扫描的帖子可手动补扫（自动扫描命中并停止后，仍可点击后续帖子）
    head.onclick = () => { if (!post.scanning && !post.scanned) scanPost(id, post, workGen); };
    // 手动组合下载：优先 rapidgator，失效分卷用其它网盘同名分卷补齐后一并下载
    const combineBtn = el('button', 'icon-btn combine-btn', '组合下载');
    combineBtn.title = '优先 rapidgator，失效分卷用其它网盘同名分卷补齐后下载';
    combineBtn.onclick = (ev) => { ev.stopPropagation(); combineDownload(id, post, combineBtn); };
    head.appendChild(combineBtn);
    box.appendChild(c);
    posts.push(post);
  });
  autoScanPosts(id, gen, posts);
}
// 从上到下自动扫描帖子，命中含有效下载的帖子即停止
async function autoScanPosts(id, gen, posts) {
  for (const post of posts) {
    if (gen !== workGen) return;
    if (post.scanned || post.scanning) continue;
    const valid = await scanPost(id, post, gen);
    if (gen !== workGen) return;
    if (valid) return;
  }
}
// 扫描单个帖子：抓网盘链接 → 内联展示 → 检测；存在有效网盘则返回 true 并可直接下载
async function scanPost(id, post, gen) {
  post.scanning = true;
  post.statusEl.textContent = '扫描中…'; post.statusEl.style.color = 'var(--muted)';
  let d;
  try { d = await api('/api/posturls?url=' + enc(post.url)); } catch (e) { post.scanning = false; return false; }
  if (gen !== workGen) { post.scanning = false; return false; }
  post.hostsEl.innerHTML = '';
  if (d.error || !d.hosts || !d.hosts.length) {
    post.statusEl.textContent = '无下载链接'; post.statusEl.style.color = 'var(--muted)';
    post.scanned = true; post.scanning = false; return false;
  }
  post.statusEl.textContent = '检测中…';
  const checks = d.hosts.map(h => {
    const c = el('div', 'host');
    c.appendChild(el('div')).append(el('div', 'hn', h.host), el('div', 'hc', `${h.count} 个文件`));
    const st = el('div', 'hs'); st.innerHTML = '<span class="spin"></span> 检测中…'; c.appendChild(st);
    post.hostsEl.appendChild(c);
    return apiPost('/api/checkhost', { urls: h.urls }).then(res => {
      if (gen !== workGen) return false;
      if (res.status === 'valid') { st.textContent = '点击下载'; st.style.color = '#4ade80'; c.classList.add('ok'); c.onclick = (ev) => { ev.stopPropagation(); enqueue(id, h.urls, c, st); }; return true; }
      if (res.status === 'invalid') { st.textContent = '失效'; st.style.color = '#f87171'; return false; }
      st.textContent = `部分有效 ${res.valid}/${res.total}`; st.style.color = '#facc15'; return false;
    }).catch(() => { st.textContent = '检测失败'; st.style.color = '#f87171'; return false; });
  });
  const results = await Promise.all(checks);
  if (gen !== workGen) { post.scanning = false; return false; }
  const anyValid = results.some(Boolean);
  post.statusEl.textContent = anyValid ? '有有效下载' : '无有效下载';
  post.statusEl.style.color = anyValid ? '#4ade80' : '#facc15';
  post.scanned = true; post.scanning = false;
  return anyValid;
}
// 手动组合下载：服务端对该帖按 rapidgator 优先、跨网盘同名分卷补齐失效者，组合出完整分卷集入队。
// 组不齐完整档案（某分卷在所有网盘都失效）则不入队，弹窗提示缺口。
async function combineDownload(id, post, btn) {
  if (btn.disabled) return;
  btn.disabled = true; const orig = btn.textContent;
  let lib = '', folder = '';
  const t = await api('/api/downtargets');
  if (t.libs && t.libs.length) {
    const target = await pickTarget(t.libs, id);
    if (!target) { btn.disabled = false; return; }   // 取消选库
    lib = target.lib; folder = target.folder;
  }
  btn.textContent = '组合中…';
  let r;
  try { r = await apiPost('/api/combine', { id, url: post.url, lib, folder }); }
  catch (e) { btn.disabled = false; btn.textContent = orig; return; }
  if (r.ok) {
    btn.textContent = '已加入下载';   // 保持禁用，避免重复入队
    // 若从社团网格进入：同步该作品卡片角标为"待下载"并启动与下载列表的状态同步
    if (makerState && makerState.cards[id]) {
      const b = makerState.cards[id];
      b.dataset.dl = '1'; b.textContent = '待下载'; b.style.background = 'rgba(0,0,0,.72)'; b.style.color = '#facc15';
      const mc = b.closest('.card'); if (mc) mc.classList.add('dim');
      startMakerDownPoll();
    }
  } else {
    btn.disabled = false; btn.textContent = orig;
    await uiAlert(r.error || '组合下载失败');
  }
}

// 社团（RG）/ 目录列表（DLsite 链接）搜索：作品缩略图网格 + 下拉到底自动加载下一页
let makerState = null, makerReturnId = null;
// src: { id } 走社团接口，{ catalogUrl } 走目录列表接口；两者共用同一作品网格与 AS 扫描
async function runGridSearch(src) {
  showMakerPane();
  $('count').textContent = '';
  const box = $('makerPane');
  box.innerHTML = '<div class="empty"><span class="spin"></span> 正在获取作品…</div>';
  if (makerState && makerState.io) makerState.io.disconnect();
  makerState = { id: src.id || src.catalogUrl, catalogUrl: src.catalogUrl || null, page: 0, hasMore: true, loading: false, scanQueue: [], scanning: false, cards: {}, countText: '', autoAll: false, autoLib: '', autoFolder: '' };
  box.innerHTML = '';
  // 复位并显示工具栏内的"自动下载"总开关（本次网格默认关；开关本体在 renderSearch 的工具栏里）
  const autoAllBtn = $('autoAllBtn');
  if (autoAllBtn) { autoAllBtn.textContent = '自动下载：关'; autoAllBtn.classList.remove('on'); autoAllBtn.style.display = ''; makerState.autoAllBtn = autoAllBtn; }
  const grid = el('div', 'grid cards'); box.appendChild(grid); makerState.grid = grid;
  const sentinel = el('div'); sentinel.style.height = '1px'; box.appendChild(sentinel);
  await loadMakerPage();
  const io = new IntersectionObserver(es => { if (es[0].isIntersecting) loadMakerPage(); });
  io.observe(sentinel); makerState.io = io;
}
async function loadMakerPage() {
  const s = makerState; if (!s || s.loading || !s.hasMore) return;
  s.loading = true;
  let d;
  try {
    d = s.catalogUrl
      ? await api(`/api/catalog?url=${enc(s.catalogUrl)}&page=${s.page + 1}`)
      : await api(`/api/maker?id=${enc(s.id)}&page=${s.page + 1}`);
  } catch (e) { s.loading = false; return; }
  if (d.error || !d.works || !d.works.length) {
    if (s.page === 0) { const mp = $('makerPane'); mp.innerHTML = ''; mp.appendChild(el('div', 'empty', d.error || (s.catalogUrl ? '未找到作品' : '未找到该社团的作品'))); const a = $('autoAllBtn'); if (a) a.style.display = 'none'; }
    s.hasMore = false; s.loading = false; if (s.io) s.io.disconnect(); return;
  }
  s.page++; s.hasMore = d.hasMore;
  let hasDownloading = false;
  d.works.forEach(w => {
    // 下载中/等待下载 → 下载状态角标（同步下载页）；已下载/已品悦 → 在库角标；不喜欢 → 不喜欢角标；
    // 以上均无需 AS 搜索 → 一律置灰。仅无记录且未被标记不喜欢者排入 AS 扫描
    const inLib = w.state === '已品悦' || w.state === '已下载';
    const downloading = w.state === '下载中';
    let disliked = !!w.disliked;
    const c = el('div', 'card mk' + ((inLib || downloading || disliked) ? ' dim' : ''));
    const cov = el('div', 'cover');
    if (w.thumb) { const img = el('img'); img.loading = 'lazy'; img.src = '/api/thumb?url=' + enc(w.thumb); cov.appendChild(img); }
    cov.appendChild(el('div', 'badge rj', w.id));
    let badge;
    if (inLib) { badge = el('div', 'badge lib', w.state); }
    else if (downloading) { badge = el('div', 'badge as', '下载中'); badge.style.color = '#60a5fa'; badge.dataset.dl = '1'; hasDownloading = true; }
    else if (disliked) { badge = el('div', 'badge as', '不喜欢'); }
    else { badge = el('div', 'badge as', '待扫描'); s.scanQueue.push(w.id); }
    cov.appendChild(badge);
    s.cards[w.id] = badge;
    // 不喜欢按钮（图片右下角）：仅对需要 / 已跳过 AS 搜索的作品显示（在库/下载中的作品本就无需搜索，不再叠加）
    if (!inLib && !downloading) {
      const dbtn = el('button', 'dislike' + (disliked ? ' on' : '')); dbtn.textContent = '👎';
      dbtn.title = disliked ? '取消不喜欢' : '不喜欢（以后跳过 AS 搜索）';
      dbtn.onclick = async (ev) => {
        ev.stopPropagation();
        const next = !disliked;
        dbtn.disabled = true;
        let r; try { r = await apiPost('/api/dislike', { id: w.id, value: next }); } catch (e) { dbtn.disabled = false; return; }
        dbtn.disabled = false;
        if (!r || !r.ok) return;
        disliked = next;
        dbtn.classList.toggle('on', disliked);
        dbtn.title = disliked ? '取消不喜欢' : '不喜欢（以后跳过 AS 搜索）';
        if (disliked) {
          c.classList.add('dim');
          badge.textContent = '不喜欢'; badge.style.color = ''; badge.style.background = '';
          const qi = s.scanQueue.indexOf(w.id); if (qi >= 0) s.scanQueue.splice(qi, 1);   // 尚未扫描则移出队列
        } else {
          c.classList.remove('dim');
          badge.textContent = '待扫描'; badge.style.color = ''; badge.style.background = '';
          if (!s.scanQueue.includes(w.id)) s.scanQueue.push(w.id);
          runMakerScans();   // 取消不喜欢 → 立即排入 AS 扫描
        }
      };
      cov.appendChild(dbtn);
    }
    c.appendChild(cov);
    // 底部区（占卡片高度 1/3）：标题 + 卡内常驻操作按钮（默认禁用/屏蔽，AS 命中帖子后由 runMakerScans 启用）
    const foot = el('div', 'mk-foot');
    foot.appendChild(el('div', 'wt', w.title || w.id));
    const acts = el('div', 'mcard-actions');
    const goList = () => { makerReturnId = s.id; s.scrollY = window.scrollY; $('sId').value = w.id; runWorkSearch(w.id); };
    const listBtn = el('button', null, '结果列表'); listBtn.disabled = true;
    listBtn.onclick = (ev) => { ev.stopPropagation(); goList(); };
    const autoBtn = el('button', 'primary', '自动下载'); autoBtn.disabled = true;
    autoBtn.onclick = (ev) => { ev.stopPropagation(); autoDownload(w.id, s.cards[w.id], autoBtn); };
    acts.append(listBtn, autoBtn);
    foot.appendChild(acts);
    c.appendChild(foot);
    // 封面/标题区仍可点击进入帖子列表（按钮已 stopPropagation），保留在库/下载中卡片的查看入口
    c.onclick = goList;
    s.grid.appendChild(c);
  });
  s.countText = `${s.grid.children.length} 个作品`;
  $('count').textContent = s.countText;
  s.loading = false;
  if (!s.hasMore && s.io) s.io.disconnect();
  runMakerScans();
  if (hasDownloading) startMakerDownPoll();
}
// 把下载列表聚合状态写回社团卡片角标（每秒，镜像 /api/downloads 的分组状态），无活动下载即止
async function startMakerDownPoll() {
  const s = makerState;
  if (!s || s.dlPolling) return;
  s.dlPolling = true;
  while (true) {
    if (makerState !== s || section !== 'dlsearch') break;
    let d;
    try { d = await api('/api/downloads'); } catch (e) { break; }
    if (makerState !== s) break;
    const map = {}; (d.groups || []).forEach(g => { map[g.id] = g; });
    Object.keys(s.cards).forEach(id => {
      const g = map[id]; if (!g) return;
      const b = s.cards[id];
      b.dataset.dl = '1';
      b.textContent = g.statusText;
      b.style.background = 'rgba(0,0,0,.72)';
      b.style.color = g.color;
      const card = b.closest('.card'); if (card) card.classList.add('dim');   // 下载中/等待下载：无需 AS 搜索 → 置灰
    });
    if (!(d.groups || []).length) break;   // 无活动下载则停止轮询
    await new Promise(r => setTimeout(r, 1000));
  }
  s.dlPolling = false;
}
// AS·无 倒计时：命中 7 天缓存的作品在到期（可再扫）前显示剩余时间，卡片保持灰色
let asCd = [];   // { badge, until }（until 为本地 epoch ms）
let asCdTimer = null;
function fmtRemain(ms) {
  if (ms <= 0) return '可重扫';
  let s = Math.floor(ms / 1000);
  const d = Math.floor(s / 86400); s %= 86400;
  const h = Math.floor(s / 3600); s %= 3600;
  const m = Math.floor(s / 60), sec = s % 60;
  const p = n => n.toString().padStart(2, '0');
  return d > 0 ? `${d}天${h}时` : `${p(h)}:${p(m)}:${p(sec)}`;
}
function tickAsCd() {
  const now = Date.now();
  asCd = asCd.filter(it => document.body.contains(it.badge));   // 卡片随视图切换被移除即自动淘汰
  asCd.forEach(it => { it.badge.textContent = `AS·无 ${fmtRemain(it.until - now)}`; });
  if (!asCd.length && asCdTimer) { clearInterval(asCdTimer); asCdTimer = null; }
}
function addAsCd(badge, remainSec) {
  asCd.push({ badge, until: Date.now() + remainSec * 1000 });
  if (!asCdTimer) asCdTimer = setInterval(tickAsCd, 1000);
  tickAsCd();
}
// 对未在库作品按 3 秒间隔逐个扫描 AS 论坛，把结果写回卡片角标
async function runMakerScans() {
  const s = makerState;
  if (!s || s.scanning) return;
  s.scanning = true;
  while (s.scanQueue.length) {
    if (makerState !== s || section !== 'dlsearch') break;   // 已开始新搜索或离开搜索分区则停止
    const id = s.scanQueue.shift();
    const st = s.cards[id];
    if (!st) continue;
    st.innerHTML = '<span class="spin"></span>';
    let d;
    try { d = await api('/api/asscan?id=' + enc(id)); } catch (e) { break; }
    if (makerState !== s || section !== 'dlsearch') break;
    if (d.count > 0) {
      st.textContent = `AS · ${d.count} 帖`; st.style.background = 'rgba(34,160,80,.88)';
      const card = st.closest('.card');
      if (card) {
        card.querySelectorAll('.mcard-actions button').forEach(b => b.disabled = false);   // 有帖子 → 解除按钮屏蔽
        // 全局自动下载已开启：命中即自动加入下载（用预选媒体库，不再逐个弹窗）
        if (s.autoAll) {
          const autoBtn = card.querySelector('.mcard-actions button.primary');
          if (autoBtn && !autoBtn.disabled) autoDownload(id, st, autoBtn, { lib: s.autoLib, folder: s.autoFolder });
        }
      }
    }
    else if (d.count === 0) {
      st.style.background = 'rgba(0,0,0,.62)';
      const card = st.closest('.card'); if (card) card.classList.add('dim');   // AS·无 → 卡片置灰
      if (d.remainSec > 0) addAsCd(st, d.remainSec); else st.textContent = 'AS · 无';   // 显示 7 天倒计时
    }
    else { st.textContent = 'AS · 失败'; st.style.background = 'rgba(180,60,60,.88)'; }
    // 仅在实际请求了 AS（非 7 天内缓存命中）且还有待扫描项时才限速等待
    if (!d.cached && s.scanQueue.length) await new Promise(r => setTimeout(r, 3000));
  }
  s.scanning = false;
}
// 选库对话框串行队列：pickTarget 共用同一个 #picker 覆盖层，连点多个自动下载时必须逐个弹出，
// 否则多个对话框会互相覆盖。每个作品各自选库，用队列保证一次只弹一个。
let pickChain = Promise.resolve();
function pickTargetQueued(libs, label) {
  const run = pickChain.then(() => pickTarget(libs, label));
  pickChain = run.catch(() => {});   // 链不因取消/异常中断，后续作品仍能继续选库
  return run;
}
// 自动下载：每个作品各自选择媒体库（连点多个时选库框逐个弹出），随即以「搜索可用下载连接」状态加入下载列表；
// 服务端串行排队、逐帖检测并挑选最优源——命中则转正常下载，全无则置「无可用下载连接」。
// 全局自动下载开关：关→直接关；开→先选一次媒体库（取消则保持关闭），随后对网格内所有
// 可下载作品自动加入下载（已命中 AS 的立刻处理，未扫描的由 runMakerScans 命中后自动触发）。
async function toggleAutoAll(btn) {
  const s = makerState; if (!s) return;
  if (s.autoAll) {
    s.autoAll = false;
    btn.textContent = '自动下载：关'; btn.classList.remove('on');
    return;
  }
  const t = await api('/api/downtargets');
  if (t.libs && t.libs.length) {
    const target = await pickTarget(t.libs);
    if (!target) return;   // 用户取消选库 → 保持关闭
    s.autoLib = target.lib; s.autoFolder = target.folder;
  } else { s.autoLib = ''; s.autoFolder = ''; }
  s.autoAll = true;
  btn.textContent = '自动下载：开'; btn.classList.add('on');
  autoDownloadAll();
  runMakerScans();   // 确保 AS 扫描在跑：未扫描的作品命中后会自动下载
  autoLoadPages();   // 自主下拉加载后续页，无需用户手动滚动
}
// 全局自动下载开启期间自主翻页：逐页推进而非一次性全部加载。
// 必须等当前页 AS 扫描队列清空（当前页作品全部扫描/自动下载完）再加载下一页，
// 否则页加载（无节流）会远快于 AS 扫描（3s/作品），一开始就把所有页拉完。
async function autoLoadPages() {
  const s = makerState; if (!s || s.autoLoading) return;
  s.autoLoading = true;
  while (makerState === s && s.autoAll && s.hasMore) {
    if (s.loading) { await new Promise(r => setTimeout(r, 300)); continue; }
    // 当前页仍有待扫描项或正在扫描 → 等其扫完再翻页
    if (s.scanQueue.length || s.scanning) { await new Promise(r => setTimeout(r, 500)); continue; }
    await loadMakerPage();
    await new Promise(r => setTimeout(r, 500));   // 页间稍作停顿，避免连发请求
  }
  s.autoLoading = false;
}
// 对网格内所有"已命中 AS、按钮已启用且尚未入队"的作品，用预选媒体库自动加入下载
function autoDownloadAll() {
  const s = makerState; if (!s || !s.autoAll) return;
  Object.keys(s.cards).forEach(id => {
    const badge = s.cards[id];
    const card = badge && badge.closest('.card'); if (!card) return;
    const autoBtn = card.querySelector('.mcard-actions button.primary');
    if (autoBtn && !autoBtn.disabled) autoDownload(id, badge, autoBtn, { lib: s.autoLib, folder: s.autoFolder });
  });
}
// preTarget 有值时用预选的媒体库直接入队（全局自动下载用），不再弹出选库框
async function autoDownload(id, badge, btn, preTarget) {
  if (btn.disabled) return;
  btn.disabled = true; const orig = btn.textContent;
  let lib = '', folder = '';
  if (preTarget) {
    lib = preTarget.lib || ''; folder = preTarget.folder || '';
  } else {
    const t = await api('/api/downtargets');
    if (t.libs && t.libs.length) {
      const target = await pickTargetQueued(t.libs, id);
      if (!target) { btn.disabled = false; return; }   // 用户取消选库
      lib = target.lib; folder = target.folder;
    }
  }
  btn.textContent = '入队中…';
  const r = await apiPost('/api/autodownload', { id, lib, folder });
  if (r.ok) {
    btn.textContent = '已入队';   // 保持禁用，避免重复入队
    // 角标即刻转「搜索连接中」，随后由 startMakerDownPoll 按 /api/downloads 聚合状态刷新
    if (badge) { badge.dataset.dl = '1'; badge.textContent = '搜索连接中'; badge.style.background = 'rgba(0,0,0,.72)'; badge.style.color = '#facc15'; }
    const mc = badge && badge.closest('.card'); if (mc) mc.classList.add('dim');   // 已入队、无需 AS 搜索 → 置灰
    startMakerDownPoll();
  } else { btn.disabled = false; btn.textContent = orig; await uiAlert('自动下载失败：' + (r.error || r.message || '')); }
}
async function enqueue(id, urls, card, st) {
  const d = await api('/api/downtargets');
  let lib = '', folder = '';
  if (d.libs.length) {
    const target = await pickTarget(d.libs);
    if (!target) return;
    lib = target.lib; folder = target.folder;
  }
  st.innerHTML = '<span class="spin"></span>';
  const r = await apiPost('/api/enqueue', { id, urls, lib, folder });
  if (r.ok) {
    st.textContent = '已加入下载'; st.style.color = '#6aa3ff'; card.classList.remove('ok'); card.onclick = null;
    // 同步社团卡片：该作品立即转为"待下载"，并启动与下载页的状态同步
    if (makerState && makerState.cards[id]) {
      const b = makerState.cards[id];
      b.dataset.dl = '1'; b.textContent = '待下载'; b.style.background = 'rgba(0,0,0,.72)'; b.style.color = '#facc15';
      const mc = b.closest('.card'); if (mc) mc.classList.add('dim');   // 已加入下载、无需 AS 搜索 → 置灰
      startMakerDownPoll();
    }
  } else { st.textContent = '加入失败'; st.style.color = '#f87171'; }
}
function pickTarget(libs, label) {
  return new Promise(resolve => {
    const ov = $('picker'), box = $('pickerBox');
    const close = (v) => { ov.classList.remove('show'); box.classList.remove('pk-wide'); resolve(v); };
    function libPage() {
      box.classList.add('pk-wide');   // 卡片网格需要更宽的对话框
      box.innerHTML = ''; box.appendChild(el('h3', null, label ? `将「${label}」下载到哪个媒体库` : '选择下载到哪个媒体库'));
      const grid = el('div', 'pk-grid');
      grid.style.gridTemplateColumns = `repeat(${Math.min(5, libs.length)}, minmax(0, 1fr))`;   // 一行最多 5 个
      libs.forEach(l => {
        const card = el('div', 'pk-card');
        card.appendChild(el('div', 'pk-name', l.name));
        if (l.folders.length > 1) card.appendChild(el('div', 'pk-sub', `${l.folders.length} 个文件夹`));
        card.onclick = () => { if (l.folders.length === 1) close({ lib: l.name, folder: l.folders[0] }); else folderPage(l); };
        grid.appendChild(card);
      });
      box.appendChild(grid);
      const c = el('button', 'icon-btn', '取消'); c.style.cssText = 'display:block;width:100%;margin-top:16px'; c.onclick = () => close(null); box.appendChild(c);
    }
    function folderPage(l) {
      box.classList.remove('pk-wide');   // 文件夹路径较长，用窄对话框竖排
      box.innerHTML = ''; box.appendChild(el('h3', null, '选择下载文件夹'));
      l.folders.forEach(f => { const b = el('button', 'icon-btn', f); b.style.cssText = 'display:block;width:100%;text-align:left;margin-top:8px;word-break:break-all'; b.onclick = () => close({ lib: l.name, folder: f }); box.appendChild(b); });
      const bk = el('button', 'icon-btn', '← 返回'); bk.style.cssText = 'display:block;width:100%;margin-top:16px'; bk.onclick = libPage; box.appendChild(bk);
    }
    libPage(); ov.classList.add('show');
  });
}
