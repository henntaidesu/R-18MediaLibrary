// fanbox.js ——「作品搜索」分区里的 FANBOX 数据源（pawchive）：搜作家 → 作家主页选作品 → 批量/单篇下载。
// 只做搜索与下载，不含媒体库浏览：已下载的 fanbox 作品落在磁盘的媒体库目录里，程序内不再单独建卡片视图。
// 工具栏（来源下拉 + 输入框 + 查询）由 search.js 统一提供，本模块只负责两个结果面板：
//   fbArtistPane 作家搜索结果（只有一个结果时直接进主页）
//   fbPostPane   作家主页：作品网格 + 卡内按钮选择 + 批量下载
//   fbDetailPane 单篇作品内容：正文 + 图集（灯箱）+ 附件清单 + 下载本篇
// 作家监控（有新作品自动下载）的列表与间隔设置不在这里，在「系统设置 → FANBOX 作家监控」（settings.js）；
// 本模块只留作家主页选择条上的「+ 监控作家」，以及被设置页「打开主页」调用的 fbGotoArtist。
let fbArtist = null;        // 当前作家 { id, name, publicId }
let fbPosts = [];           // 作家主页已加载的作品（跨分页累积）
let fbSel = new Set();      // 选中待下载的作品号
let fbPaging = null;        // { offset, hasMore, loading, io }
let fbGen = 0;              // 请求代际：新一轮搜索/换作家即作废在途请求
let fbFromArtists = false;  // 当前主页是否从作家列表点进来（决定「返回作家列表」是否显示）
let fbLevel = 'artists';    // 当前层级：artists / posts / detail
let fbWatched = false;      // 当前作家是否已在监控中（作家主页按钮据此切文案）
// 详情请求单独一套代际：看详情不应作废作家主页的在途分页与下拉监听（返回后还要继续翻页）
let fbDetailGen = 0;
let fbGridScroll = 0;       // 进详情前作品网格的滚动位置，返回时恢复
let fbPageIo = null;        // 详情页图流的懒加载观察器（滚到跟前才给图贴 src）
let fbDetail = null;        // 当前详情页的作品 { id, state }，工具栏「下载」据此定文案

const fbImg = (url) => '/api/fanbox/image?url=' + enc(url);

// ---------- 面板骨架 ----------

// 由 search.js 搭建搜索区时调用一次：建出作家/作品两个面板（与 DLsite 的 makerPane/workPane 同构）
function fbBuildPanes(host) {
  fbResetState();
  const wrap = el('div'); wrap.id = 'fbResult';
  const ap = el('div'); ap.id = 'fbArtistPane';
  const pp = el('div'); pp.id = 'fbPostPane'; pp.style.display = 'none';
  const dp = el('div'); dp.id = 'fbDetailPane'; dp.style.display = 'none';
  wrap.append(ap, pp, dp);
  host.appendChild(wrap);
}

function fbResetState() {
  if (fbPaging && fbPaging.io) fbPaging.io.disconnect();
  fbPaging = null;
  fbArtist = null; fbPosts = []; fbSel = new Set(); fbFromArtists = false;
  fbLevel = 'artists'; fbGridScroll = 0; fbWatched = false; fbDetail = null;
  fbStopPageIo();
  fbGen++; fbDetailGen++;
}

function fbShowPane(level) {
  fbLevel = level;
  if (level !== 'detail') { fbStopPageIo(); fbDetail = null; }   // 离开详情就别再为看不见的图发请求
  $('fbArtistPane').style.display = level === 'artists' ? '' : 'none';
  $('fbPostPane').style.display = level === 'posts' ? '' : 'none';
  $('fbDetailPane').style.display = level === 'detail' ? '' : 'none';
  fbUpdateToolbarBtns();
}

const fbShowArtistPane = () => fbShowPane('artists');
const fbShowPostPane = () => fbShowPane('posts');
const fbShowDetailPane = () => fbShowPane('detail');

// 工具栏上属于 FANBOX 的两颗按钮（下载 / 返回）按当前层级归位。
// 返回：详情 → 回作品列表，作家主页 → 回作家列表（仅从作家结果点进来时才有得回），文案一律「返回」。
// 下载：只在详情页出现，已入库/下载中转成禁用的状态按钮。
function fbUpdateToolbarBtns() {
  const b = $('fbBackBtn');
  if (b) {
    b.textContent = '返回';
    b.onclick = fbLevel === 'detail' ? fbGoBackToPosts : fbGoBackToArtists;
    b.style.display = (fbLevel === 'detail' || (fbLevel === 'posts' && fbFromArtists)) ? '' : 'none';
  }
  const d = $('fbDlPostBtn');
  if (!d) return;
  if (fbLevel !== 'detail' || !fbDetail) { d.style.display = 'none'; return; }
  const done = fbDetail.state === '已品悦';
  const busy = fbDetail.state === '下载中' || fbDetail.state === '已下载';
  d.style.display = '';
  d.textContent = done ? '已下载' : (busy ? fbDetail.state : '下载');
  d.disabled = done || busy;
  d.classList.toggle('primary', !done && !busy);
}

// 工具栏「下载」：只入队当前详情这一篇
async function fbDownloadDetail() {
  const btn = $('fbDlPostBtn');
  if (!fbDetail || !btn || btn.disabled) return;
  await fbDownloadOne(fbDetail.id, btn);
  // 入队成功时 fbEnqueue 会把 fbPosts 里的状态改成「下载中」，按钮跟着转成禁用态
  const p = fbPosts.find(x => x.id === fbDetail.id);
  if (p && p.state) fbDetail.state = p.state;
  fbUpdateToolbarBtns();
}

function fbGoBackToArtists() {
  fbShowArtistPane();
  $('count').textContent = '';
}

// 从详情返回作家主页：网格与已翻页数据都还在 DOM 里，恢复标题行与滚动位置即可
function fbGoBackToPosts() {
  fbShowPostPane();
  fbUpdateArtistLine();
  window.scrollTo(0, fbGridScroll);
}

// 作家主页的标题行：只写这是谁的主页。
// 已加载多少篇不显示——下拉到底自动续页，这个数字随时在变、对用户也没用。
function fbUpdateArtistLine() {
  $('count').textContent = (fbArtist && (fbArtist.name || fbArtist.id)) || '';
}

// ---------- 搜索作家 ----------

// 输入可以是作家名、纯数字作家号，或直接粘 pawchive 的作家/作品链接
function fbParseInput(raw) {
  raw = (raw || '').trim();
  const m = raw.match(/\/fanbox\/user\/(\d+)/i);
  if (m) return { kind: 'artist', id: m[1] };
  if (/^\d{4,}$/.test(raw)) return { kind: 'artist', id: raw };
  return { kind: 'keyword', kw: raw };
}

// 由 search.js 的「查询」按钮 / 回车触发
async function fbRunSearch() {
  const raw = ($('sId') && $('sId').value.trim()) || '';
  if (!raw) return;
  const p = fbParseInput(raw);
  // 直接给出作家号（或粘链接）时跳过搜索，径直进主页
  if (p.kind === 'artist') { await fbOpenArtist({ id: p.id, name: '', publicId: '' }, false); return; }
  await fbSearchArtists(p.kw);
}

async function fbSearchArtists(kw) {
  const gen = ++fbGen;
  fbShowArtistPane();
  const box = $('fbArtistPane');
  box.innerHTML = '<div class="empty"><span class="spin"></span> 正在搜索作家…（首次搜索需下载站点作家索引，约 15MB）</div>';
  $('count').textContent = '';
  let d;
  try { d = await api('/api/fanbox/artists?q=' + enc(kw)); } catch (e) { return; }
  if (gen !== fbGen) return;
  const artists = d.artists || [];
  // 只有一个结果 → 直接进入该作家主页（免去多余一次点击）
  if (artists.length === 1) { await fbOpenArtist(artists[0], false); return; }
  box.innerHTML = '';
  if (!artists.length) { box.appendChild(el('div', 'empty', '没有匹配的作家')); return; }
  $('count').textContent = `搜索到 ${artists.length} 位作家` + (d.cachedAt ? `　索引更新于 ${d.cachedAt}` : '');
  const grid = el('div', 'grid cards');
  artists.forEach(a => grid.appendChild(fbMakeArtistCard(a)));
  box.appendChild(grid);
}

function fbMakeArtistCard(a) {
  const c = el('div', 'card');
  const cov = el('div', 'cover');
  if (a.icon) { const img = el('img'); img.loading = 'lazy'; img.src = fbImg(a.icon); cov.appendChild(img); }
  if (a.publicId) cov.appendChild(el('div', 'badge rj', a.publicId));
  if (a.favorited) cov.appendChild(el('div', 'badge type', '♥ ' + a.favorited));
  c.append(cov, el('div', 'wt', a.name || a.publicId || a.id));
  c.onclick = () => fbOpenArtist(a, true);
  return c;
}

// ---------- 作家主页 ----------

async function fbOpenArtist(artist, fromArtists) {
  const gen = ++fbGen;
  fbArtist = { id: artist.id, name: artist.name || '', publicId: artist.publicId || '' };
  fbFromArtists = !!fromArtists;
  fbPosts = []; fbSel = new Set();
  if (fbPaging && fbPaging.io) fbPaging.io.disconnect();
  fbPaging = { offset: 0, hasMore: true, loading: false, io: null };

  fbShowPostPane();
  const box = $('fbPostPane');
  box.innerHTML = '<div class="empty"><span class="spin"></span> 正在获取作品…</div>';

  const first = await fbFetchPosts(gen, 0);
  if (gen !== fbGen || !first) return;
  box.innerHTML = '';

  // 选择操作条：已选计数 + 全选 / 清空 / 下载选中
  const selbar = el('div', 'toolbar fb-selbar');
  const info = el('div', 'fb-selinfo'); info.id = 'fbSelInfo';
  const all = el('button', 'icon-btn', '全选'); all.onclick = () => fbSelectAll(true);
  const none = el('button', 'icon-btn', '清空'); none.onclick = () => fbSelectAll(false);
  const dl = el('button', 'icon-btn primary', '下载选中'); dl.id = 'fbDlBtn'; dl.onclick = fbDownloadSelected;
  // 监控这位作家：加进监控后按设定的间隔轮询，有新作品就自动下载
  const watch = el('button', 'icon-btn'); watch.id = 'fbWatchToggle'; watch.onclick = fbToggleWatch;
  selbar.append(info, watch, all, none, dl);
  box.appendChild(selbar);
  fbUpdateWatchBtn();

  const grid = el('div', 'grid cards'); grid.id = 'fbGrid'; box.appendChild(grid);
  fbAppendPosts(grid, first);
  fbSetupPaging(gen, grid, box);
  fbUpdateSelInfo();
}

async function fbFetchPosts(gen, offset) {
  const q = new URLSearchParams();
  q.set('id', fbArtist ? fbArtist.id : ''); q.set('service', 'fanbox'); q.set('o', offset);
  let d;
  try { d = await api('/api/fanbox/posts?' + q); } catch (e) { return null; }
  if (gen !== fbGen) return null;
  if (d.error) {
    const pane = $('fbPostPane');
    pane.innerHTML = ''; pane.appendChild(el('div', 'empty', d.error));
    return null;
  }
  if (d.artist) fbArtist = { id: d.artist.id, name: d.artist.name, publicId: d.artist.publicId };
  if (offset === 0) { fbWatched = !!d.watched; fbUpdateWatchBtn(); }
  if (fbPaging) { fbPaging.offset = offset + (d.posts || []).length; fbPaging.hasMore = !!d.hasMore; }
  fbPosts = fbPosts.concat(d.posts || []);
  fbUpdateArtistLine();
  return d.posts || [];
}

// 下拉到底自动加载下一页（与 DLsite 社团作品网格同一交互）
function fbSetupPaging(gen, grid, box) {
  if (!fbPaging || !fbPaging.hasMore) return;
  const sentinel = el('div'); sentinel.style.height = '1px'; box.appendChild(sentinel);
  fbPaging.io = new IntersectionObserver(async es => {
    if (!es[0].isIntersecting || fbPaging.loading || !fbPaging.hasMore) return;
    fbPaging.loading = true;
    const more = await fbFetchPosts(gen, fbPaging.offset);
    if (gen !== fbGen) return;
    if (more && more.length) fbAppendPosts(grid, more);
    fbPaging.loading = false;
    if (!fbPaging.hasMore) { fbPaging.io.disconnect(); sentinel.remove(); }
  }, { rootMargin: '0px' });
  fbPaging.io.observe(sentinel);
}

function fbAppendPosts(grid, posts) {
  const frag = document.createDocumentFragment();
  posts.forEach(p => frag.appendChild(fbMakePostCard(p)));
  grid.appendChild(frag);
}

// 作品卡：结构与操作方式对齐 DLsite 作品卡（.card.mk）——封面占 2/3，
// 底部区放标题 + 卡内常驻操作按钮行（.mcard-actions）。
// 未下载：[选择/已选] [下载]；已入库/下载中：禁用的状态按钮。
// 点卡片本体进详情看内容。
function fbMakePostCard(p) {
  const done = p.state === '已品悦';
  const busy = p.state === '下载中' || p.state === '已下载';
  const picked = fbSel.has(p.id);
  const c = el('div', 'card mk' + (done || busy ? ' dim' : '') + (picked ? ' picked' : ''));

  const cov = el('div', 'cover');
  if (p.cover) { const img = el('img'); img.loading = 'lazy'; img.src = fbImg(p.cover); cov.appendChild(img); }
  if (p.files) cov.appendChild(el('div', 'badge type', p.files + ' 文件'));
  // 作品本体在网盘上（站上只归档了一张封面图）：入队时会连网盘文件一起下
  if (p.drive) cov.appendChild(el('div', 'badge rj', '☁ 网盘'));
  if (done || busy) cov.appendChild(el('div', 'badge lib', done ? '已下载' : p.state));
  c.appendChild(cov);

  const foot = el('div', 'mk-foot');
  foot.appendChild(el('div', 'wt', p.title || p.id));
  const acts = el('div', 'mcard-actions');
  if (done || busy) {
    const st = el('button', null, done ? '已下载' : p.state); st.disabled = true;
    acts.appendChild(st);
  } else {
    const selBtn = el('button', picked ? 'primary' : null, picked ? '已选' : '选择');
    selBtn.onclick = (ev) => { ev.stopPropagation(); fbTogglePick(p.id, c, selBtn); };
    const dlBtn = el('button', 'primary', '下载');
    dlBtn.title = '只下载这一篇';
    dlBtn.onclick = (ev) => { ev.stopPropagation(); fbDownloadOne(p.id, dlBtn); };
    acts.append(selBtn, dlBtn);
  }
  foot.appendChild(acts);
  c.appendChild(foot);

  // 封面/标题区点击 → 进入作品详情查看内容（卡内按钮已 stopPropagation，选择/下载不受影响）。
  // 已下载/下载中的作品同样可以点进去看，只是不能再选。
  c.onclick = () => fbOpenPost(p);
  return c;
}

// 翻转单张卡片的选中态：整卡描边强调 + 按钮在 选择/已选 之间切换
function fbTogglePick(id, card, btn) {
  const on = !fbSel.has(id);
  if (on) fbSel.add(id); else fbSel.delete(id);
  if (card) card.classList.toggle('picked', on);
  if (btn) { btn.textContent = on ? '已选' : '选择'; btn.classList.toggle('primary', on); }
  fbUpdateSelInfo();
}

const fbPostSelectable = (p) => p.state !== '已品悦' && p.state !== '下载中' && p.state !== '已下载';

function fbSelectAll(on) {
  fbSel = new Set();
  if (on) fbPosts.forEach(p => { if (fbPostSelectable(p)) fbSel.add(p.id); });
  fbRedrawGrid();
  fbUpdateSelInfo();
}

// 按当前 fbPosts / fbSel 重建卡片：卡内按钮文案与选中描边随之刷新
function fbRedrawGrid() {
  const grid = $('fbGrid');
  if (!grid) return;
  grid.innerHTML = '';
  fbAppendPosts(grid, fbPosts);
}

function fbUpdateSelInfo() {
  const info = $('fbSelInfo'); if (info) info.textContent = `已选 ${fbSel.size} 篇`;
  const btn = $('fbDlBtn'); if (btn) btn.disabled = fbSel.size === 0;
}

// ---------- 作品详情 ----------

// 看图不走灯箱：整篇的图直接竖排铺在详情页页尾，滚动即读（同 E-Hentai 详情页把整本铺在页尾的做法）。
// 图一律用 800px 预览：原图动辄几 MB，而且站点只归档了预览的投稿（has_full=false）取原图直接 404。
const FB_EAGER_PAGES = 2;   // 首屏那几张不等观察器，进详情就开始下

function fbStopPageIo() {
  if (fbPageIo) { fbPageIo.disconnect(); fbPageIo = null; }
}

// 竖排图流：首屏的几张直接给 src，其余滚到跟前才贴。
// 一篇动辄三四十张，进详情就全下会把带宽占满，首屏反而最慢。
function fbBuildPages(imgs) {
  fbStopPageIo();
  const box = el('div', 'fbpages');
  fbPageIo = new IntersectionObserver(es => {
    es.forEach(e => {
      if (!e.isIntersecting) return;
      fbLoadPage(e.target);
      if (fbPageIo) fbPageIo.unobserve(e.target);
    });
  }, { rootMargin: '600px' });   // 提前一屏开始取，滚到跟前时多半已就位
  imgs.forEach((im, i) => {
    const cell = el('div', 'fbpage');
    const img = el('img');
    img.alt = im.name || String(i + 1);
    img.dataset.src = fbImg(im.thumb);
    // 未加载的格子要留一段高度：整列都塌成 0 高的话会一次性全落进视口，懒加载等于没做。
    // 图一到位就撤掉这段占位高度，按图片真实高度排版。
    img.onload = () => { cell.style.minHeight = '0'; };
    cell.appendChild(img);
    box.appendChild(cell);
    if (i < FB_EAGER_PAGES) fbLoadPage(img); else fbPageIo.observe(img);
  });
  return box;
}

function fbLoadPage(img) {
  if (!img.dataset.src) return;
  img.src = img.dataset.src;
  delete img.dataset.src;
}

// 点作品卡进入：拉取正文与附件清单并渲染
async function fbOpenPost(post) {
  const gen = ++fbDetailGen;
  fbGridScroll = window.scrollY || 0;
  fbShowDetailPane();
  const box = $('fbDetailPane');
  box.innerHTML = '<div class="empty"><span class="spin"></span> 正在获取作品内容…</div>';
  $('count').textContent = '';   // 标题就在下面的详情里，顶上不再重复一遍
  window.scrollTo(0, 0);

  const q = new URLSearchParams();
  q.set('id', fbArtist ? fbArtist.id : ''); q.set('post', post.id); q.set('service', 'fanbox');
  let d;
  try { d = await api('/api/fanbox/post?' + q); } catch (e) { return; }
  // 期间已返回列表或换了作家就丢弃本次结果
  if (gen !== fbDetailGen || fbLevel !== 'detail') return;
  box.innerHTML = '';
  if (d.error) { box.appendChild(el('div', 'empty', d.error)); return; }
  fbRenderPost(box, d);
}

// 详情结构对齐媒体库详情页（.detail > .dtop > .gallery + .fields），沿用同一套样式
function fbRenderPost(box, d) {
  const done = d.state === '已品悦';
  const busy = d.state === '下载中' || d.state === '已下载';

  // 下载这一篇的按钮在共用工具栏上（查询 与 返回 之间），详情页自己不再另起一条操作条
  fbDetail = { id: d.id, state: d.state || '' };
  fbUpdateToolbarBtns();

  const root = el('div', 'detail');
  root.appendChild(el('h1', null, d.title || d.id));

  const top = el('div', 'dtop');
  const imgs = d.images || [];
  const gallery = el('div', 'gallery');
  if (imgs.length) {
    // 上半只摆一张封面（首图的 800px 预览），正经看图在页尾的图流里，点封面不再开灯箱
    const main = el('img', 'main fbcover');
    main.src = fbImg(imgs[0].thumb);
    gallery.appendChild(main);
  } else {
    gallery.appendChild(el('div', 'empty', '这篇没有图片'));
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
  addF('作家', (fbArtist && (fbArtist.name || fbArtist.id)) || '');
  addF('发布', fbFmtDate(d.published));
  if (d.tags && d.tags.length) {
    const wrap = el('span');
    d.tags.forEach(t => wrap.appendChild(el('a', 'tag', t)));
    addF('标签', wrap);
  }
  const links = d.links || [];
  addF('文件', `${d.files} 个（图片 ${imgs.length}）` +
    (links.length ? `　网盘 ${links.length} 条` : ''));
  addF('状态', done ? '已下载' : (d.state || '未下载'));
  top.appendChild(fields);
  root.appendChild(top);

  if (d.content) {
    root.appendChild(el('h1', 'fb-subtitle', '正文'));
    root.appendChild(el('div', 'fb-body', d.content));
  }
  const others = d.others || [];
  if (others.length) {
    root.appendChild(el('h1', 'fb-subtitle', `其他附件（${others.length}）`));
    const list = el('div', 'fb-files');
    others.forEach(f => list.appendChild(el('div', 'fb-file', f.name)));
    root.appendChild(list);
  }
  // 网盘链接：作者把作品本体（多为压缩包）放在网盘上时，站上只归档一张封面图。
  // 下载本篇会连这些文件一起下（共享文件夹展开成逐个文件），下完按设置自动解压。
  if (links.length) {
    root.appendChild(el('h1', 'fb-subtitle', `网盘链接（${links.length}）`));
    const list = el('div', 'fb-files');
    links.forEach(l => {
      const row = el('div', 'fb-file');
      const a = el('a', null, (l.folder ? '📁 ' : '📦 ') + l.url);
      a.href = l.url; a.target = '_blank'; a.rel = 'noreferrer';
      row.appendChild(a);
      list.appendChild(row);
    });
    root.appendChild(list);
    root.appendChild(el('div', 'fb-note', '下载本篇时会一并下载网盘里的文件，完成后按设置自动解压'));
  }

  // 全部图片放在最后：上面的信息与操作看完就是一路滚到底连读，中间不再被别的区块打断
  if (imgs.length) {
    root.appendChild(el('h1', 'fb-subtitle', `全部图片（${imgs.length} 张）`));
    root.appendChild(fbBuildPages(imgs));
  }
  box.appendChild(root);
}

// 站点给的是 2026-01-23T08:00:00 这种，去掉 T 与秒即可
function fbFmtDate(s) {
  if (!s) return '';
  return String(s).replace('T', ' ').slice(0, 16);
}

// ---------- 下载 ----------

// 操作条「下载选中」：批量入队当前选中的作品
function fbDownloadSelected() {
  const btn = $('fbDlBtn');
  if (!btn || btn.disabled || !fbSel.size) return;
  return fbEnqueue([...fbSel], btn);
}

// 卡内「下载」：只入队这一篇
function fbDownloadOne(id, btn) {
  return fbEnqueue([id], btn);
}

// 选媒体库 → 入队 → 提示 → 就地把入队成功的作品转为「下载中」并取消其选中
async function fbEnqueue(ids, btn) {
  if (!ids.length || !fbArtist || (btn && btn.disabled)) return;
  let lib = '', folder = '';
  const t = await api('/api/downtargets');
  if (t.libs && t.libs.length) {
    const target = await pickTarget(t.libs, fbArtist.name || fbArtist.id);
    if (!target) return;
    lib = target.lib; folder = target.folder;
  }
  const orig = btn ? btn.textContent : '';
  if (btn) { btn.disabled = true; btn.textContent = '入队中…'; }
  let r;
  try {
    r = await apiPost('/api/fanbox/enqueue', {
      id: fbArtist.id, service: 'fanbox', posts: ids, lib, folder,
    });
  } catch (e) {
    if (btn) { btn.textContent = orig; btn.disabled = false; }
    return;
  }
  if (btn) { btn.textContent = orig; btn.disabled = false; }
  if (!r.ok) { await uiAlert(r.error || '加入下载失败'); return; }
  await uiAlert(`已加入下载：${r.posts} 篇作品 / ${r.files} 个文件` + (r.skipped ? `，跳过 ${r.skipped} 篇` : ''));
  ids.forEach(id => {
    const p = fbPosts.find(x => x.id === id);
    if (p) p.state = '下载中';
    fbSel.delete(id);
  });
  fbRedrawGrid();
  fbUpdateSelInfo();
}

// ---------- 作家监控（有新作品自动下载） ----------
// 监控列表、轮询间隔与总开关都在「系统设置 → FANBOX 作家监控」（settings.js 的 renderFanboxWatchSection），
// 本模块只留两处：作家主页选择条上的「+ 监控作家 / ✓ 已监控」，以及设置页「打开主页」回跳到这里。
// 数据在服务端的 fanbox_watch 表里，后台按各作家自己的间隔轮询 pawchive：
// 发现比"添加监控那一刻"更新的投稿，就按该作家设定的媒体库自动入队下载。
// 判"新"看的是投稿的发布时间而不是"本地有没有这篇"，所以手动删掉的作品不会被一次次下回来。

// 设置页监控卡的「打开主页」：跨分区切到 作品搜索 → FANBOX，并直接进这位作家的作品网格
function fbGotoArtist(id, name) {
  selectSection('search');
  const sel = $('srcSel');
  if (sel) sel.value = 'fanbox';
  setSearchSource('fanbox');
  fbOpenArtist({ id, name: name || '', publicId: '' }, false);
}

// ---------- 作家主页的监控开关 ----------

function fbUpdateWatchBtn() {
  const b = $('fbWatchToggle');
  if (!b) return;
  b.textContent = fbWatched ? '✓ 已监控' : '+ 监控作家';
  b.classList.toggle('on', fbWatched);
  b.title = fbWatched
    ? '点击取消监控这位作家'
    : '添加后按设定的间隔轮询，这位作家发新作品时自动下载';
}

async function fbToggleWatch() {
  if (!fbArtist) return;
  const who = fbArtist.name || fbArtist.id;
  const btn = $('fbWatchToggle');

  if (fbWatched) {
    if (!await uiConfirm(`不再监控「${who}」？\n已经下载的作品不受影响。`, { danger: true })) return;
    await apiPost('/api/fanbox/watch/update', { id: fbArtist.id, remove: true });
    fbWatched = false; fbUpdateWatchBtn();
    return;
  }

  // 自动下载要有个落点：和手动下载一样先选媒体库，之后每次自动入队都往这里放
  let lib = '', folder = '';
  const t = await api('/api/downtargets');
  if (t.libs && t.libs.length) {
    const target = await pickTarget(t.libs, who);
    if (!target) return;
    lib = target.lib; folder = target.folder;
  }
  // 现有作品下不下：默认只要新作品（取消 / Esc 即落到这一边）
  const backfill = await uiConfirm(
    `监控「${who}」。这位作家站上已有的作品要一并下载吗？\n\n` +
    '「全部下载」＝把现有作品也加入下载队列（数量可能很多）\n' +
    '「只要新作品」＝从现在起，只下载今后新发布的作品',
    { title: '添加监控', okText: '全部下载', cancelText: '只要新作品' });

  if (btn) { btn.disabled = true; btn.textContent = '添加中…'; }
  let r;
  try { r = await apiPost('/api/fanbox/watch/add', { id: fbArtist.id, backfill, lib, folder }); }
  finally { if (btn) btn.disabled = false; }
  if (!r || !r.ok) { fbUpdateWatchBtn(); await uiAlert((r && r.message) || '添加监控失败'); return; }
  fbWatched = true; fbUpdateWatchBtn();
  await uiAlert(r.message);
}
