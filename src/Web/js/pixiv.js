// pixiv.js ——「作品搜索」分区里的 pixiv 数据源：搜作品 → 选作品 → 批量/单件下载。
// 与 FANBOX / E-Hentai 一样只做搜索与下载，不含媒体库浏览：下好的作品落在磁盘的媒体库目录里。
// 工具栏（来源下拉 + 输入框 + 查询 + 下载 + 返回）由 search.js 统一提供，本模块只负责两个结果面板：
//   pxListPane   搜索结果：作品网格 + 卡内按钮选择 + 批量下载（下拉到底翻页）
//   pxDetailPane 单件作品内容：封面 + 字段 + 说明，整篇的图竖排铺在页尾
// 层级只有两级（搜索即出作品，没有 FANBOX 那种「作家」中间层）。
let pxItems = [];          // 已加载的作品（跨分页累积）
let pxSel = new Set();     // 选中待下载的作品，键为作品号
let pxPaging = null;       // { page, hasMore, loading, io }
let pxGen = 0;             // 请求代际：新一轮搜索即作废在途请求
let pxDetailGen = 0;       // 详情单独一套代际，免得作废列表的下拉翻页监听
let pxLevel = 'results';   // 当前层级：results / detail
let pxKeyword = '';        // 当前搜索词（翻页要带着）
let pxGridScroll = 0;      // 进详情前网格的滚动位置，返回时恢复
let pxAnonymous = false;   // 服务端报告的登录态：未登录时站点会把 R-18 滤掉，要提示用户
let pxDetail = null;       // 当前详情的作品 { id, state }，工具栏「下载」据此定文案
let pxPageIo = null;       // 详情页尾图流的懒加载观察器

// 站上的图统一走服务端代理：i.pximg.net 带防盗链，浏览器直接引用一律 403
const pxImg = (url) => '/api/px/image?url=' + enc(url);

// ---------- 面板骨架 ----------

// 由 search.js 搭建搜索区时调用一次
function pxBuildPanes(host) {
  pxResetState();
  const wrap = el('div'); wrap.id = 'pxResult';
  const lp = el('div'); lp.id = 'pxListPane';
  const dp = el('div'); dp.id = 'pxDetailPane'; dp.style.display = 'none';
  wrap.append(lp, dp);
  host.appendChild(wrap);
}

function pxResetState() {
  if (pxPaging && pxPaging.io) pxPaging.io.disconnect();
  pxStopPageIo();
  pxPaging = null; pxDetail = null;
  pxItems = []; pxSel = new Set();
  pxLevel = 'results'; pxKeyword = ''; pxGridScroll = 0; pxAnonymous = false;
  pxGen++; pxDetailGen++;
}

function pxStopPageIo() {
  if (pxPageIo) { pxPageIo.disconnect(); pxPageIo = null; }
}

function pxShowPane(level) {
  pxLevel = level;
  $('pxListPane').style.display = level === 'results' ? '' : 'none';
  $('pxDetailPane').style.display = level === 'detail' ? '' : 'none';
  if (level !== 'detail') { pxStopPageIo(); pxDetail = null; }   // 离开详情就别再为看不见的图发请求
  pxUpdateToolbarBtns();
}

const pxShowListPane = () => pxShowPane('results');
const pxShowDetailPane = () => pxShowPane('detail');

// 工具栏上属于本来源的两颗按钮（下载本篇 / 返回），按层级与作品状态归位。
// 详情页自己不再另起一条操作条（同 FANBOX）。
function pxUpdateToolbarBtns() {
  const b = $('pxBackBtn');
  if (b) { b.textContent = '返回'; b.onclick = pxGoBackToList; b.style.display = pxLevel === 'detail' ? '' : 'none'; }
  const d = $('pxDlPostBtn');
  if (!d) return;
  if (pxLevel !== 'detail' || !pxDetail) { d.style.display = 'none'; return; }
  const done = pxDetail.state === '已品悦';
  const busy = pxDetail.state === '下载中' || pxDetail.state === '已下载';
  d.style.display = '';
  d.textContent = done ? '已下载' : (busy ? pxDetail.state : '下载');
  d.disabled = done || busy;
  d.classList.toggle('primary', !(done || busy));
}

async function pxDownloadDetail() {
  const btn = $('pxDlPostBtn');
  if (!pxDetail || !btn || btn.disabled) return;
  await pxEnqueue([pxDetail.id], btn);
  const it = pxItems.find(x => x.id === pxDetail.id);
  if (it && it.state) pxDetail.state = it.state;
  pxUpdateToolbarBtns();
}

// 从详情返回：网格与已翻页数据都还在 DOM 里，恢复计数行与滚动位置即可
function pxGoBackToList() {
  pxShowListPane();
  pxUpdateCount();
  window.scrollTo(0, pxGridScroll);
}

function pxUpdateCount() {
  if (!pxItems.length) { $('count').textContent = ''; return; }
  const more = pxPaging && pxPaging.hasMore ? '（下拉加载更多）' : '';
  const anon = pxAnonymous ? '　未登录：结果中不含 R-18' : '';
  $('count').textContent = `已加载 ${pxItems.length} 件作品${more}${anon}`;
}

// ---------- 搜索 ----------

// 由 search.js 的「查询」按钮 / 回车触发
async function pxRunSearch() {
  const raw = ($('sId') && $('sId').value.trim()) || '';
  if (!raw) return;
  const gen = ++pxGen;
  pxKeyword = raw;
  pxItems = []; pxSel = new Set();
  if (pxPaging && pxPaging.io) pxPaging.io.disconnect();
  pxPaging = { page: 1, hasMore: true, loading: false, io: null };

  pxShowListPane();
  const box = $('pxListPane');
  box.innerHTML = '<div class="empty"><span class="spin"></span> 正在搜索作品…</div>';
  $('count').textContent = '';

  const first = await pxFetch(gen, 1);
  if (gen !== pxGen || !first) return;
  box.innerHTML = '';
  if (!first.length) {
    box.appendChild(el('div', 'empty', pxAnonymous
      ? '没有匹配的作品（未登录，R-18 作品不会出现在结果里）'
      : '没有匹配的作品'));
    return;
  }

  // 选择操作条：已选计数 + 全选 / 清空 / 下载选中（同 FANBOX 作家主页）
  const selbar = el('div', 'toolbar fb-selbar');
  const info = el('div', 'fb-selinfo'); info.id = 'pxSelInfo';
  const all = el('button', 'icon-btn', '全选'); all.onclick = () => pxSelectAll(true);
  const none = el('button', 'icon-btn', '清空'); none.onclick = () => pxSelectAll(false);
  const dl = el('button', 'icon-btn primary', '下载选中'); dl.id = 'pxDlBtn'; dl.onclick = pxDownloadSelected;
  selbar.append(info, all, none, dl);
  box.appendChild(selbar);

  const grid = el('div', 'grid cards'); grid.id = 'pxGrid'; box.appendChild(grid);
  pxAppend(grid, first);
  pxSetupPaging(gen, grid, box);
  pxUpdateSelInfo();
}

async function pxFetch(gen, page) {
  const q = new URLSearchParams();
  q.set('q', pxKeyword);
  q.set('p', page);
  let d;
  try { d = await api('/api/px/search?' + q); } catch (e) { return null; }
  if (gen !== pxGen) return null;
  if (d.error) {
    const pane = $('pxListPane');
    pane.innerHTML = ''; pane.appendChild(el('div', 'empty', d.error));
    return null;
  }
  pxAnonymous = !!d.anonymous;
  const items = d.items || [];
  if (pxPaging) { pxPaging.page = d.page || page; pxPaging.hasMore = !!d.hasMore; }
  pxItems = pxItems.concat(items);
  pxUpdateCount();
  return items;
}

// 下拉到底自动加载下一页（站点按页号翻页，每页 60 件）
function pxSetupPaging(gen, grid, box) {
  if (!pxPaging || !pxPaging.hasMore) return;
  const sentinel = el('div'); sentinel.style.height = '1px'; box.appendChild(sentinel);
  pxPaging.io = new IntersectionObserver(async es => {
    if (!es[0].isIntersecting || pxPaging.loading || !pxPaging.hasMore) return;
    pxPaging.loading = true;
    const more = await pxFetch(gen, pxPaging.page + 1);
    if (gen !== pxGen) return;
    if (more && more.length) pxAppend(grid, more);
    pxPaging.loading = false;
    if (!pxPaging.hasMore) { pxPaging.io.disconnect(); sentinel.remove(); }
  }, { rootMargin: '0px' });
  pxPaging.io.observe(sentinel);
}

function pxAppend(grid, items) {
  const frag = document.createDocumentFragment();
  items.forEach(a => frag.appendChild(pxMakeCard(a)));
  grid.appendChild(frag);
}

// 左上角标：形式 + 分级/AI 合成一格（卡上只有左右两个角标位，右边留给张数）
function pxTagText(a) {
  const extra = [a.restrict, a.ai ? 'AI' : ''].filter(Boolean).join(' · ');
  return extra ? `${a.type} · ${extra}` : a.type;
}

// 作品卡：结构与 FANBOX / E-Hentai 一致——封面占 2/3，底部标题 + 常驻操作按钮行。
// 未下载：[选择/已选] [下载]；已入库/下载中：禁用的状态按钮。点卡片本体进详情。
function pxMakeCard(a) {
  const done = a.state === '已品悦';
  const busy = a.state === '下载中' || a.state === '已下载';
  const picked = pxSel.has(a.id);
  const c = el('div', 'card mk' + (done || busy ? ' dim' : '') + (picked ? ' picked' : ''));

  const cov = el('div', 'cover');
  if (a.cover) { const img = el('img'); img.loading = 'lazy'; img.src = pxImg(a.cover); cov.appendChild(img); }
  cov.appendChild(el('div', 'badge type', pxTagText(a)));
  if (a.pages > 1) cov.appendChild(el('div', 'badge rj', a.pages + ' 张'));
  if (done || busy) cov.appendChild(el('div', 'badge lib', done ? '已下载' : a.state));
  c.appendChild(cov);

  const foot = el('div', 'mk-foot');
  foot.appendChild(el('div', 'wt', a.title || a.id));
  const acts = el('div', 'mcard-actions');
  if (done || busy) {
    const st = el('button', null, done ? '已下载' : a.state); st.disabled = true;
    acts.appendChild(st);
  } else {
    const selBtn = el('button', picked ? 'primary' : null, picked ? '已选' : '选择');
    selBtn.onclick = (ev) => { ev.stopPropagation(); pxTogglePick(a.id, c, selBtn); };
    const dlBtn = el('button', 'primary', '下载');
    dlBtn.title = '只下载这一件';
    dlBtn.onclick = (ev) => { ev.stopPropagation(); pxDownloadOne(a.id, dlBtn); };
    acts.append(selBtn, dlBtn);
  }
  foot.appendChild(acts);
  c.appendChild(foot);

  c.onclick = () => pxOpenArtwork(a);
  return c;
}

function pxTogglePick(id, card, btn) {
  const on = !pxSel.has(id);
  if (on) pxSel.add(id); else pxSel.delete(id);
  if (card) card.classList.toggle('picked', on);
  if (btn) { btn.textContent = on ? '已选' : '选择'; btn.classList.toggle('primary', on); }
  pxUpdateSelInfo();
}

const pxSelectable = (a) => a.state !== '已品悦' && a.state !== '下载中' && a.state !== '已下载';

function pxSelectAll(on) {
  pxSel = new Set();
  if (on) pxItems.forEach(a => { if (pxSelectable(a)) pxSel.add(a.id); });
  pxRedrawGrid();
  pxUpdateSelInfo();
}

function pxRedrawGrid() {
  const grid = $('pxGrid');
  if (!grid) return;
  grid.innerHTML = '';
  pxAppend(grid, pxItems);
}

function pxUpdateSelInfo() {
  const info = $('pxSelInfo'); if (info) info.textContent = `已选 ${pxSel.size} 件`;
  const btn = $('pxDlBtn'); if (btn) btn.disabled = pxSel.size === 0;
}

// ---------- 作品详情 ----------

// 看图不走灯箱：整篇的图直接竖排铺在详情页页尾，滚动即读（同 FANBOX 详情页的做法）。
// 图一律用 1200px 的 regular：原图动辄几 MB，一篇漫画几十张全取原图会把带宽占满，
// 而详情只是预览——原图是下载时才取的。
const PX_EAGER_PAGES = 2;   // 首屏那几张不等观察器，进详情就开始下

// 竖排图流：首屏的几张直接给 src，其余滚到跟前才贴
function pxBuildPages(imgs) {
  pxStopPageIo();
  const box = el('div', 'fbpages');
  pxPageIo = new IntersectionObserver(es => {
    es.forEach(e => {
      if (!e.isIntersecting) return;
      pxLoadPage(e.target);
      if (pxPageIo) pxPageIo.unobserve(e.target);
    });
  }, { rootMargin: '600px' });   // 提前一屏开始取，滚到跟前时多半已就位
  imgs.forEach((im, i) => {
    const cell = el('div', 'fbpage');
    const img = el('img');
    img.alt = String(im.index || i + 1);
    img.dataset.src = pxImg(im.url);
    // 未加载的格子要留一段高度：整列都塌成 0 高的话会一次性全落进视口，懒加载等于没做
    img.onload = () => { cell.style.minHeight = '0'; };
    cell.appendChild(img);
    box.appendChild(cell);
    if (i < PX_EAGER_PAGES) pxLoadPage(img); else pxPageIo.observe(img);
  });
  return box;
}

function pxLoadPage(img) {
  if (!img.dataset.src) return;
  img.src = img.dataset.src;
  delete img.dataset.src;
}

// 点作品卡进入：拉取详情与逐页图片地址并渲染
async function pxOpenArtwork(a) {
  const gen = ++pxDetailGen;
  pxGridScroll = window.scrollY || 0;
  pxShowDetailPane();
  const box = $('pxDetailPane');
  box.innerHTML = '<div class="empty"><span class="spin"></span> 正在获取作品内容…</div>';
  $('count').textContent = '';   // 标题就在下面的详情里，顶上不再重复一遍
  window.scrollTo(0, 0);

  let d;
  try { d = await api('/api/px/artwork?id=' + enc(a.id)); } catch (e) { return; }
  // 期间已返回列表或换了作品就丢弃本次结果
  if (gen !== pxDetailGen || pxLevel !== 'detail') return;
  box.innerHTML = '';
  if (d.error) { box.appendChild(el('div', 'empty', d.error)); return; }
  pxRenderArtwork(box, d.artwork || a, d.images || []);
}

// 详情结构对齐媒体库详情页（.detail > .dtop > .gallery + .fields），沿用同一套样式
function pxRenderArtwork(box, a, images) {
  const done = a.state === '已品悦';

  // 下载这一件的按钮在共用工具栏上（查询 与 返回 之间），详情页自己不再另起一条操作条
  pxDetail = { id: a.id, state: a.state || '' };
  pxUpdateToolbarBtns();

  const root = el('div', 'detail');
  root.appendChild(el('h1', null, a.title || a.id));

  // 上半：封面 + 字段
  const top = el('div', 'dtop');
  const gallery = el('div', 'gallery');
  const cover = images.length ? images[0].url : a.cover;
  if (cover) {
    const main = el('img', 'main fbcover');
    main.src = pxImg(cover);
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
  addF('作者', a.maker || '');
  addF('投稿', a.posted || '');
  addF('形式', pxTagText(a));
  addF('张数', a.pages ? `${a.pages} 张` : '');
  if (a.width && a.height) addF('尺寸', `${a.width} × ${a.height}`);
  if (a.bookmarks) addF('收藏', String(a.bookmarks));
  if (a.tags && a.tags.length) {
    const wrap = el('span');
    a.tags.forEach(t => wrap.appendChild(el('a', 'tag', t)));
    addF('标签', wrap);
  }
  addF('状态', done ? '已下载' : (a.state || '未下载'));
  const site = el('a', null, '在 pixiv 打开');
  site.href = 'https://www.pixiv.net/artworks/' + a.id;
  site.target = '_blank'; site.rel = 'noreferrer';
  addF('原址', site);
  top.appendChild(fields);
  root.appendChild(top);

  if (a.description) {
    root.appendChild(el('h1', 'fb-subtitle', '说明'));
    root.appendChild(el('div', 'fb-body', a.description));
  }

  // 页尾：整篇的图竖排铺开，逐张懒加载
  if (images.length) {
    root.appendChild(el('h1', 'fb-subtitle', `全部图片（${images.length} 张）`));
    root.appendChild(pxBuildPages(images));
  }
  box.appendChild(root);
}

// ---------- 下载 ----------

function pxDownloadSelected() {
  const btn = $('pxDlBtn');
  if (!btn || btn.disabled || !pxSel.size) return;
  return pxEnqueue([...pxSel], btn);
}

function pxDownloadOne(id, btn) {
  return pxEnqueue([id], btn);
}

// 选媒体库 → 入队 → 提示 → 就地把入队成功的作品转为「下载中」并取消其选中
async function pxEnqueue(ids, btn) {
  if (!ids.length || (btn && btn.disabled)) return;
  let lib = '', folder = '';
  const t = await api('/api/downtargets');
  if (t.libs && t.libs.length) {
    const target = await pickTarget(t.libs, 'pixiv');
    if (!target) return;
    lib = target.lib; folder = target.folder;
  }
  const orig = btn ? btn.textContent : '';
  // 入队要逐件现取图片清单，件数多时要等一会儿，提示要说清
  if (btn) { btn.disabled = true; btn.textContent = '入队中…'; }
  let r;
  try {
    r = await apiPost('/api/px/enqueue', { items: ids, lib, folder });
  } catch (e) {
    if (btn) { btn.textContent = orig; btn.disabled = false; }
    return;
  }
  if (btn) { btn.textContent = orig; btn.disabled = false; }
  if (!r.ok) { await uiAlert(r.error || '加入下载失败'); return; }
  await uiAlert(`已加入下载：${r.artworks} 件作品 / ${r.files} 个文件` + (r.skipped ? `，跳过 ${r.skipped} 件` : ''));
  ids.forEach(id => {
    const it = pxItems.find(x => x.id === id);
    if (it) it.state = '下载中';
    pxSel.delete(id);
  });
  pxRedrawGrid();
  pxUpdateSelInfo();
}
