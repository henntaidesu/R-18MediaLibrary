// ehentai.js ——「作品搜索」分区里的 E-Hentai 数据源：搜画廊 → 选本子 → 批量/单本下载。
// 与 FANBOX 一样只做搜索与下载，不含媒体库浏览：下好的画廊落在磁盘的媒体库目录里。
// 工具栏（来源下拉 + 输入框 + 查询）由 search.js 统一提供，本模块只负责两个结果面板：
//   ehListPane   搜索结果：画廊网格 + 卡内按钮选择 + 批量下载（下拉到底翻页）
//   ehDetailPane 单本画廊内容：缩略图预览 + 标签字段 + 下载本篇
// 层级只有两级（搜索即出画廊，没有 FANBOX 那种「作家」中间层）。
let ehItems = [];          // 已加载的画廊（跨分页累积）
let ehSel = new Set();     // 选中待下载的画廊，键为 "gid:token"
let ehPaging = null;       // { next, hasMore, loading, io }
let ehGen = 0;             // 请求代际：新一轮搜索即作废在途请求
let ehDetailGen = 0;       // 详情单独一套代际，免得作废列表的下拉翻页监听
let ehLevel = 'results';   // 当前层级：results / detail
let ehKeyword = '';        // 当前搜索词（翻页要带着）
let ehGridScroll = 0;      // 进详情前网格的滚动位置，返回时恢复

// 站上的图统一走服务端代理：浏览器直连受跨域/代理设置影响，里站的图还要带登录 cookie
const ehImg = (url) => '/api/eh/image?url=' + enc(url);
const ehKey = (g) => `${g.gid}:${g.token}`;

// ---------- 面板骨架 ----------

// 由 search.js 搭建搜索区时调用一次
function ehBuildPanes(host) {
  ehResetState();
  const wrap = el('div'); wrap.id = 'ehResult';
  const lp = el('div'); lp.id = 'ehListPane';
  const dp = el('div'); dp.id = 'ehDetailPane'; dp.style.display = 'none';
  wrap.append(lp, dp);
  host.appendChild(wrap);
}

function ehResetState() {
  if (ehPaging && ehPaging.io) ehPaging.io.disconnect();
  if (ehDetail && ehDetail.io) ehDetail.io.disconnect();
  if (ehDetail && ehDetail.thumbIo) ehDetail.thumbIo.disconnect();
  ehDetail = null;
  ehPaging = null;
  ehItems = []; ehSel = new Set();
  ehLevel = 'results'; ehKeyword = ''; ehGridScroll = 0;
  ehGen++; ehDetailGen++;
}

function ehShowPane(level) {
  ehLevel = level;
  $('ehListPane').style.display = level === 'results' ? '' : 'none';
  $('ehDetailPane').style.display = level === 'detail' ? '' : 'none';
  ehUpdateBackBtn();
}

const ehShowListPane = () => ehShowPane('results');
const ehShowDetailPane = () => ehShowPane('detail');

// 工具栏返回按钮：只有详情层需要（搜索结果已是最外层）
function ehUpdateBackBtn() {
  const b = $('ehBackBtn');
  if (!b) return;
  b.textContent = '返回搜索结果';
  b.onclick = ehGoBackToList;
  b.style.display = ehLevel === 'detail' ? '' : 'none';
}

// 从详情返回：网格与已翻页数据都还在 DOM 里，恢复计数行与滚动位置即可
function ehGoBackToList() {
  ehShowListPane();
  ehUpdateCount();
  window.scrollTo(0, ehGridScroll);
}

function ehUpdateCount() {
  const more = ehPaging && ehPaging.hasMore ? '（下拉加载更多）' : '';
  $('count').textContent = ehItems.length ? `已加载 ${ehItems.length} 本画廊${more}` : '';
}

// ---------- 搜索 ----------

// 由 search.js 的「查询」按钮 / 回车触发
async function ehRunSearch() {
  const raw = ($('sId') && $('sId').value.trim()) || '';
  if (!raw) return;
  const gen = ++ehGen;
  ehKeyword = raw;
  ehItems = []; ehSel = new Set();
  if (ehPaging && ehPaging.io) ehPaging.io.disconnect();
  ehPaging = { next: '', hasMore: true, loading: false, io: null };

  ehShowListPane();
  const box = $('ehListPane');
  box.innerHTML = '<div class="empty"><span class="spin"></span> 正在搜索画廊…</div>';
  $('count').textContent = '';

  const first = await ehFetch(gen, '');
  if (gen !== ehGen || !first) return;
  box.innerHTML = '';
  if (!first.length) { box.appendChild(el('div', 'empty', '没有匹配的画廊')); return; }

  // 选择操作条：已选计数 + 全选 / 清空 / 下载选中（同 FANBOX 作家主页）
  const selbar = el('div', 'toolbar fb-selbar');
  const info = el('div', 'fb-selinfo'); info.id = 'ehSelInfo';
  const all = el('button', 'icon-btn', '全选'); all.onclick = () => ehSelectAll(true);
  const none = el('button', 'icon-btn', '清空'); none.onclick = () => ehSelectAll(false);
  const dl = el('button', 'icon-btn primary', '下载选中'); dl.id = 'ehDlBtn'; dl.onclick = ehDownloadSelected;
  selbar.append(info, all, none, dl);
  box.appendChild(selbar);

  const grid = el('div', 'grid cards'); grid.id = 'ehGrid'; box.appendChild(grid);
  ehAppend(grid, first);
  ehSetupPaging(gen, grid, box);
  ehUpdateSelInfo();
}

async function ehFetch(gen, next) {
  const q = new URLSearchParams();
  q.set('q', ehKeyword);
  if (next) q.set('next', next);
  let d;
  try { d = await api('/api/eh/search?' + q); } catch (e) { return null; }
  if (gen !== ehGen) return null;
  if (d.error) {
    const pane = $('ehListPane');
    pane.innerHTML = ''; pane.appendChild(el('div', 'empty', d.error));
    return null;
  }
  const items = d.items || [];
  if (ehPaging) { ehPaging.next = d.next || ''; ehPaging.hasMore = !!d.next; }
  ehItems = ehItems.concat(items);
  ehUpdateCount();
  return items;
}

// 下拉到底自动加载下一页（站点是游标翻页，带上一页返回的 next）
function ehSetupPaging(gen, grid, box) {
  if (!ehPaging || !ehPaging.hasMore) return;
  const sentinel = el('div'); sentinel.style.height = '1px'; box.appendChild(sentinel);
  ehPaging.io = new IntersectionObserver(async es => {
    if (!es[0].isIntersecting || ehPaging.loading || !ehPaging.hasMore) return;
    ehPaging.loading = true;
    const more = await ehFetch(gen, ehPaging.next);
    if (gen !== ehGen) return;
    if (more && more.length) ehAppend(grid, more);
    ehPaging.loading = false;
    if (!ehPaging.hasMore) { ehPaging.io.disconnect(); sentinel.remove(); }
  }, { rootMargin: '0px' });
  ehPaging.io.observe(sentinel);
}

function ehAppend(grid, items) {
  const frag = document.createDocumentFragment();
  items.forEach(g => frag.appendChild(ehMakeCard(g)));
  grid.appendChild(frag);
}

// 画廊卡：结构与 FANBOX 作品卡一致——封面占 2/3，底部标题 + 常驻操作按钮行。
// 未下载：[选择/已选] [下载]；已入库/下载中：禁用的状态按钮。点卡片本体进详情。
function ehMakeCard(g) {
  const done = g.state === '已品悦';
  const busy = g.state === '下载中' || g.state === '已下载';
  const key = ehKey(g);
  const picked = ehSel.has(key);
  const c = el('div', 'card mk' + (done || busy ? ' dim' : '') + (picked ? ' picked' : ''));

  const cov = el('div', 'cover');
  if (g.cover) { const img = el('img'); img.loading = 'lazy'; img.src = ehImg(g.cover); cov.appendChild(img); }
  if (g.category) cov.appendChild(el('div', 'badge type', g.category));
  if (g.files) cov.appendChild(el('div', 'badge rj', g.files + ' 页'));
  if (done || busy) cov.appendChild(el('div', 'badge lib', done ? '已下载' : g.state));
  c.appendChild(cov);

  const foot = el('div', 'mk-foot');
  foot.appendChild(el('div', 'wt', g.title || String(g.gid)));
  const acts = el('div', 'mcard-actions');
  if (done || busy) {
    const st = el('button', null, done ? '已下载' : g.state); st.disabled = true;
    acts.appendChild(st);
  } else {
    const selBtn = el('button', picked ? 'primary' : null, picked ? '已选' : '选择');
    selBtn.onclick = (ev) => { ev.stopPropagation(); ehTogglePick(key, c, selBtn); };
    const dlBtn = el('button', 'primary', '下载');
    dlBtn.title = '只下载这一本';
    dlBtn.onclick = (ev) => { ev.stopPropagation(); ehDownloadOne(key, dlBtn); };
    acts.append(selBtn, dlBtn);
  }
  foot.appendChild(acts);
  c.appendChild(foot);

  c.onclick = () => ehOpenGallery(g);
  return c;
}

function ehTogglePick(key, card, btn) {
  const on = !ehSel.has(key);
  if (on) ehSel.add(key); else ehSel.delete(key);
  if (card) card.classList.toggle('picked', on);
  if (btn) { btn.textContent = on ? '已选' : '选择'; btn.classList.toggle('primary', on); }
  ehUpdateSelInfo();
}

const ehSelectable = (g) => g.state !== '已品悦' && g.state !== '下载中' && g.state !== '已下载';

function ehSelectAll(on) {
  ehSel = new Set();
  if (on) ehItems.forEach(g => { if (ehSelectable(g)) ehSel.add(ehKey(g)); });
  ehRedrawGrid();
  ehUpdateSelInfo();
}

function ehRedrawGrid() {
  const grid = $('ehGrid');
  if (!grid) return;
  grid.innerHTML = '';
  ehAppend(grid, ehItems);
}

function ehUpdateSelInfo() {
  const info = $('ehSelInfo'); if (info) info.textContent = `已选 ${ehSel.size} 本`;
  const btn = $('ehDlBtn'); if (btn) btn.disabled = ehSel.size === 0;
}

// ---------- 画廊详情 ----------

// 详情页当前这本的分页状态。缩略图来自画廊列表页（匿名访问是 20 张一拼的雪碧图），
// 取它不消耗看图额度，所以整本都能列出来——但仍按列表页分批要，下拉到哪儿才要哪一页。
let ehDetail = null;   // { gid, token, total, p, hasMore, loading, io, urls, thumbIo }

// 灯箱用的地址数组：每个元素是「按页码现解析现取大图」的服务端地址。
// 故意传同一个数组对象给 LB——数组随翻页增长，灯箱里按方向键也就能翻到后来加载的页。
const ehPageUrl = (im) =>
  `/api/eh/page?gid=${ehDetail.gid}&token=${enc(ehDetail.token)}&page=${im.index}&key=${enc(im.key)}`;

// 点画廊卡进入：先要第一页（元数据 + 前 20 张缩略图），其余随下拉补齐
async function ehOpenGallery(g) {
  const gen = ++ehDetailGen;
  ehGridScroll = window.scrollY || 0;
  ehShowDetailPane();
  const box = $('ehDetailPane');
  box.innerHTML = '<div class="empty"><span class="spin"></span> 正在获取画廊内容…</div>';
  $('count').textContent = g.title || String(g.gid);
  window.scrollTo(0, 0);

  if (ehDetail && ehDetail.io) ehDetail.io.disconnect();
  if (ehDetail && ehDetail.thumbIo) ehDetail.thumbIo.disconnect();
  ehDetail = { gid: g.gid, token: g.token, total: g.files || 0, p: 0, hasMore: true, loading: false,
               io: null, thumbIo: null, urls: [] };

  let d;
  try { d = await api(`/api/eh/gallery?gid=${g.gid}&token=${enc(g.token)}&p=0`); } catch (e) { return; }
  if (gen !== ehDetailGen || ehLevel !== 'detail') return;
  box.innerHTML = '';
  if (d.error) { box.appendChild(el('div', 'empty', d.error)); return; }
  ehDetail.total = d.total || ehDetail.total;
  ehRenderGallery(box, d.gallery || g, d.images || [], !!d.hasMore, gen);
}

// 详情结构对齐媒体库详情页（.detail > .dtop > .gallery + .fields），下面再接整本图片网格
function ehRenderGallery(box, g, images, hasMore, gen) {
  const done = g.state === '已品悦';
  const busy = g.state === '下载中' || g.state === '已下载';
  const key = ehKey(g);

  const bar = el('div', 'toolbar');
  if (done || busy) {
    const b = el('button', 'icon-btn', done ? '已下载' : g.state); b.disabled = true;
    bar.appendChild(b);
  } else {
    const b = el('button', 'icon-btn primary', '下载本篇');
    b.onclick = async () => {
      await ehDownloadOne(key, b);
      const it = ehItems.find(x => ehKey(x) === key);
      if (it && it.state) { b.textContent = it.state; b.disabled = true; b.classList.remove('primary'); }
    };
    bar.appendChild(b);
  }
  bar.appendChild(el('div', 'grow'));
  const open = el('a', 'icon-btn', '在站点打开');
  open.href = `https://e-hentai.org/g/${g.gid}/${g.token}/`;
  open.target = '_blank'; open.rel = 'noreferrer';
  bar.appendChild(open);
  box.appendChild(bar);

  const root = el('div', 'detail');
  root.appendChild(el('h1', null, g.title || String(g.gid)));

  // 上半：封面 + 字段
  const top = el('div', 'dtop');
  const gallery = el('div', 'gallery');
  if (g.cover) {
    const main = el('img', 'main');
    main.src = ehImg(g.cover);
    gallery.appendChild(main);
  } else {
    gallery.appendChild(el('div', 'empty', '取不到封面'));
  }
  top.appendChild(gallery);

  const fields = el('div', 'fields');
  const addF = (k, node) => {
    if (node == null || node === '') return;
    fields.appendChild(el('div', 'k', k));
    const v = el('div', 'v');
    if (typeof node === 'string') v.textContent = node; else v.appendChild(node);
    fields.appendChild(v);
  };
  if (g.titleEn && g.titleEn !== g.title) addF('原名', g.titleEn);
  addF('社团/作者', g.maker || '');
  addF('分类', g.category || '');
  addF('投稿者', g.uploader || '');
  addF('投稿', g.posted || '');
  addF('评分', g.rating || '');
  addF('页数', g.files ? `${g.files} 页` : '');
  if (g.tags && g.tags.length) {
    const wrap = el('span');
    g.tags.forEach(t => wrap.appendChild(el('a', 'tag', t)));
    addF('标签', wrap);
  }
  addF('状态', done ? '已下载' : (g.state || '未下载'));
  if (g.expunged) addF('提示', '该画廊已在站点被删除，内容可能不全');
  top.appendChild(fields);
  root.appendChild(top);

  // 下半：整本图片
  root.appendChild(el('h1', 'fb-subtitle', `全部图片（${ehDetail.total || g.files || '?'} 页）`));
  const grid = el('div', 'ehgrid'); grid.id = 'ehPageGrid';
  root.appendChild(grid);
  const more = el('div', 'ehmore'); more.id = 'ehPageMore';
  root.appendChild(more);
  box.appendChild(root);

  // 缩略图本身也懒加载：滚到跟前才给它贴底图。
  // 20 张共用一张雪碧图，露出一张就把这一批都带出来了，不会逐张发请求。
  ehDetail.thumbIo = new IntersectionObserver(es => {
    es.forEach(e => {
      if (!e.isIntersecting) return;
      const t = e.target;
      if (t.dataset.bg) { t.style.backgroundImage = `url("${t.dataset.bg}")`; delete t.dataset.bg; }
      ehDetail.thumbIo.unobserve(t);
    });
  }, { rootMargin: '300px' });

  ehAppendThumbs(grid, images);
  ehDetail.hasMore = hasMore;
  ehSetupDetailPaging(gen, grid, more);
}

function ehAppendThumbs(grid, images) {
  const frag = document.createDocumentFragment();
  images.forEach(im => {
    ehDetail.urls.push(ehPageUrl(im));
    const idx = ehDetail.urls.length - 1;

    const cell = el('div', 'ehcell');
    const t = el('div', 'ehthumb');
    // 站点两套版式：逐张缩略图直接铺满；雪碧图则按格宽定位裁出那一格
    if (im.sprite) {
      t.style.width = (im.w || 100) + 'px';
      t.style.height = (im.h || 140) + 'px';
      t.style.backgroundPosition = `-${im.x || 0}px 0`;
      t.dataset.bg = ehImg(im.sprite);
    } else if (im.thumb) {
      t.style.width = (im.w || 100) + 'px';
      t.style.height = (im.h || 140) + 'px';
      t.style.backgroundSize = 'cover';
      t.dataset.bg = ehImg(im.thumb);
    }
    t.title = im.name || ('Page ' + im.index);
    // 点开即从这一页起翻；urls 是活数组，后面加载进来的页在灯箱里也能翻到
    t.onclick = () => LB.open(ehDetail.urls, idx);
    cell.appendChild(t);
    cell.appendChild(el('div', 'ehnum', String(im.index)));
    frag.appendChild(cell);
    ehDetail.thumbIo.observe(t);
  });
  grid.appendChild(frag);
}

// 下拉到底自动加载下一列表页（每页 20 张）
function ehSetupDetailPaging(gen, grid, sentinel) {
  if (ehDetail.io) ehDetail.io.disconnect();
  if (!ehDetail.hasMore) { sentinel.textContent = ''; return; }
  ehDetail.io = new IntersectionObserver(async es => {
    if (!es[0].isIntersecting || ehDetail.loading || !ehDetail.hasMore) return;
    ehDetail.loading = true;
    sentinel.innerHTML = '<span class="spin"></span> 正在加载更多…';
    let d;
    try {
      d = await api(`/api/eh/gallery?gid=${ehDetail.gid}&token=${enc(ehDetail.token)}&p=${ehDetail.p + 1}`);
    } catch (e) { ehDetail.loading = false; sentinel.textContent = ''; return; }
    if (gen !== ehDetailGen || ehLevel !== 'detail') return;
    if (d.error) { sentinel.textContent = d.error; ehDetail.hasMore = false; ehDetail.io.disconnect(); return; }
    ehDetail.p += 1;
    ehDetail.hasMore = !!d.hasMore;
    ehAppendThumbs(grid, d.images || []);
    ehDetail.loading = false;
    sentinel.textContent = '';
    if (!ehDetail.hasMore) { ehDetail.io.disconnect(); }
  }, { rootMargin: '400px' });
  ehDetail.io.observe(sentinel);
}

// ---------- 下载 ----------

function ehDownloadSelected() {
  const btn = $('ehDlBtn');
  if (!btn || btn.disabled || !ehSel.size) return;
  return ehEnqueue([...ehSel], btn);
}

function ehDownloadOne(key, btn) {
  return ehEnqueue([key], btn);
}

// 选媒体库 → 入队 → 提示 → 就地把入队成功的画廊转为「下载中」并取消其选中
async function ehEnqueue(keys, btn) {
  if (!keys.length || (btn && btn.disabled)) return;
  let lib = '', folder = '';
  const t = await api('/api/downtargets');
  if (t.libs && t.libs.length) {
    const target = await pickTarget(t.libs, 'E-Hentai');
    if (!target) return;
    lib = target.lib; folder = target.folder;
  }
  const orig = btn ? btn.textContent : '';
  // 入队要现抓每本画廊的图片页清单（一本几百张图要翻十几页），比 FANBOX 慢，提示要说清
  if (btn) { btn.disabled = true; btn.textContent = '入队中…'; }
  let r;
  try {
    r = await apiPost('/api/eh/enqueue', { items: keys, lib, folder });
  } catch (e) {
    if (btn) { btn.textContent = orig; btn.disabled = false; }
    return;
  }
  if (btn) { btn.textContent = orig; btn.disabled = false; }
  if (!r.ok) { await uiAlert(r.error || '加入下载失败'); return; }
  await uiAlert(`已加入下载：${r.galleries} 本画廊 / ${r.files} 张图片` + (r.skipped ? `，跳过 ${r.skipped} 本` : ''));
  keys.forEach(key => {
    const it = ehItems.find(x => ehKey(x) === key);
    if (it) it.state = '下载中';
    ehSel.delete(key);
  });
  ehRedrawGrid();
  ehUpdateSelInfo();
}
