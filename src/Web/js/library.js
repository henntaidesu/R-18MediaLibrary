// library.js —— 媒体库家族页面：媒体库 / 标签 / 社团 / 作品形式 / 收藏 的卡片渲染 + 作品详情页。
async function renderLibs() {
  $('title').textContent = '媒体库';
  const d = await api('/api/libs');
  curItems = d.libs.map(l => ({ ...l, _key: l.name.toLowerCase() }));
  // 进入媒体库先显示什么由该库的「默认显示」决定（缺省作品）；右上角 ⚙ 打开该库的设置
  drawGroups(curItems, l => ({
    title: l.name, caption: `${l.works} 个作品 · ${l.folders} 个文件夹`,
    onEdit: () => openLibSettings(l.name, () => { if (section === 'medialib' && stack.length === 1) render(); }),
    editIcon: '⚙', editTitle: '媒体库设置',
    onClick: () => pushView('libview', { lib: l.name, mode: l.view === 'makers' ? 'makers' : 'works' }),
  }), '个媒体库');
}

// 媒体库内视图：默认平铺作品，带"显示作品/显示社团"就地切换（不算一级，便于手势返回）
async function renderLibView(ctx) {
  const mode = ctx.mode || 'works';
  $('title').textContent = ctx.lib;
  if (mode === 'works') {
    setSort(WORK_SORTS, workSort, v => { workSort = v; render(); });
    const d = await api(`/api/libworks?lib=${enc(ctx.lib)}&sort=${workSort}`);
    curItems = d.works.map(w => ({ ...w, _key: `${w.id} ${w.name} ${w.maker}`.toLowerCase() }));
    drawWorks(curItems);
    prependLibToggle('🏢 显示社团', 'makers');
  } else {
    setSort(MAKER_SORTS, makerSort, v => { makerSort = v; render(); });
    const d = await api(`/api/makers?lib=${enc(ctx.lib)}&sort=${makerSort}`);
    curItems = d.makers.map(m => ({ ...m, _key: `${m.maker} ${m.display || ''}`.trim().toLowerCase() || '未知社团' }));
    drawGroups(curItems, m => makerGroup(m, () => pushView('works', { lib: ctx.lib, maker: m.maker, label: makerLabel(m) })), '个社团');
    prependLibToggle('▦ 显示作品', 'works');
  }
}
function prependLibToggle(label, toMode) {
  const b = $('libToggle');   // 复用顶栏常驻按钮，与标题/排序同一行，不再另起一行
  b.hidden = false; b.textContent = label;
  b.onclick = () => { stack[stack.length - 1].ctx.mode = toMode; render(); };  // 就地切换，不 push 新层级
}
async function renderGenres() {
  $('title').textContent = '作品标签';
  const d = await api('/api/genres');
  curItems = d.genres.map(g => ({ ...g, _key: g.genre.toLowerCase() }));
  drawGroups(curItems, g => ({ title: g.genre, caption: `${g.count} 个作品`, onClick: () => pushView('makers', { genre: g.genre }) }), '个标签');
}
async function renderTypes() {
  $('title').textContent = '作品形式';
  const d = await api('/api/types');
  curItems = d.types.map(t => ({ ...t, _key: t.type.toLowerCase() }));
  drawGroups(curItems, t => ({ title: t.type, caption: `${t.count} 个作品`, onClick: () => pushView('makers', { type: t.type }) }), '个形式');
}
// 顶级"作品社团"分区：列出全部媒体库的社团，点击下钻到该社团作品
async function renderAllMakers() {
  $('title').textContent = '作品社团';
  setSort(MAKER_SORTS, makerSort, v => { makerSort = v; render(); });
  const d = await api('/api/makers?sort=' + makerSort);
  curItems = d.makers.map(m => ({ ...m, _key: `${m.maker} ${m.display || ''}`.trim().toLowerCase() || '未知社团' }));
  drawGroups(curItems, m => makerGroup(m, () => pushView('works', { maker: m.maker, label: makerLabel(m) })), '个社团');
}
async function renderMakers(ctx) {
  $('title').textContent = ctx.genre || ctx.type || ctx.lib || '作品社团';
  setSort(MAKER_SORTS, makerSort, v => { makerSort = v; render(); });
  const q = new URLSearchParams();
  if (ctx.lib) q.set('lib', ctx.lib); if (ctx.genre) q.set('genre', ctx.genre); if (ctx.type) q.set('type', ctx.type);
  q.set('sort', makerSort);
  const d = await api('/api/makers?' + q);
  curItems = d.makers.map(m => ({ ...m, _key: `${m.maker} ${m.display || ''}`.trim().toLowerCase() || '未知社团' }));
  drawGroups(curItems, m => makerGroup(m, () => pushView('works', { ...ctx, maker: m.maker, label: makerLabel(m) })), '个社团');
}
// 社团显示名：设过别名用别名，否则真名；两者都空是"未知社团"
function makerLabel(m) { return m.display || m.maker || '未知社团'; }

// 社团卡：头像 + 名称 + 作品数 + 主页外链（FANBOX / pawchive），右上角 🖊 打开社团设置
function makerGroup(m, onClick) {
  const links = [];
  if (m.fanboxUrl) links.push({ label: 'FANBOX', url: m.fanboxUrl });
  if (m.pawchiveUrl) links.push({ label: 'pawchive', url: m.pawchiveUrl });
  return {
    title: makerLabel(m), caption: `${m.count} 个作品`, links,
    icon: m.iconUrl || m.icon || '', iconFallback: m.iconUrl ? m.icon : '', avatar: true,
    onEdit: m.maker ? () => editMaker(m) : null, editTitle: '社团设置', onClick,
  };
}

// 右上角 🖊：社团设置——显示名称 + 主页。只改映射，作品库里的真名不动
async function editMaker(m) {
  const v = await uiForm({
    title: '社团设置',
    message: `社团「${m.maker}」（不会改动作品数据）`,
    fields: [
      { key: 'alias', label: '显示名称（留空恢复原名）', value: m.display && m.display !== m.maker ? m.display : '', placeholder: m.maker },
      { key: 'fanbox', label: 'FANBOX 主页', value: m.fanboxUrl || '', placeholder: 'https://xxx.fanbox.cc/' },
      { key: 'pawchive', label: 'pawchive 主页', value: m.pawchiveUrl || '', placeholder: 'https://pawchive.pw/fanbox/user/…' },
    ],
  });
  if (v === null) return;
  const r = await apiPost('/api/makersettings', { maker: m.maker, ...v });
  if (r && r.error) { await uiAlert(r.error, '社团设置'); return; }
  render();
}

function worksUrl(ctx) {
  const q = new URLSearchParams();
  if (ctx.lib) q.set('lib', ctx.lib); if (ctx.genre) q.set('genre', ctx.genre); if (ctx.type) q.set('type', ctx.type);
  q.set('maker', ctx.maker || ''); q.set('sort', workSort);
  return '/api/works?' + q;
}
async function renderWorks(url, title, sortable) {
  $('title').textContent = title;
  if (sortable) setSort(WORK_SORTS, workSort, v => { workSort = v; render(); });
  const full = url.includes('sort=') ? url : url + (url.includes('?') ? '&' : '?') + 'sort=' + workSort;
  const d = await api(full);
  curItems = d.works.map(w => ({ ...w, _key: `${w.id} ${w.name} ${w.maker}`.toLowerCase() }));
  drawWorks(curItems);
}

// 作用域作品搜索：媒体库根搜全部库 / 库内搜该库 / 标签·形式社团页搜对应分组（服务端 LIKE，对齐 WPF）
let searchGen = 0;
async function scopedWorkSearch(st, kw, gen) {
  const ctx = st.ctx || {};
  const q = new URLSearchParams(); q.set('kw', kw); q.set('sort', workSort);
  if (st.view === 'libview') { if (ctx.lib) q.set('lib', ctx.lib); }
  else if (st.view === 'makers') {
    if (ctx.genre) q.set('genre', ctx.genre);
    else if (ctx.type) q.set('type', ctx.type);
    else if (ctx.lib) q.set('lib', ctx.lib);
  }
  // libs 根：不带范围参数 → 搜全部库
  let d; try { d = await api('/api/searchworks?' + q); } catch (e) { return; }
  if (gen !== searchGen) return;   // 已有更新的搜索请求
  const works = d.works || [];
  $('sort').hidden = true;
  $('count').textContent = `搜索到 ${works.length} 个作品`;
  lazyCards($('content'), works, makeWorkCard, '没有匹配的作品');
}

// 作品详情：标题 → 可折叠的「作品详情」（封面/字段/正文，默认收起）→ 作品文件（原「查看作品」页并入此处）。
// ctx 即导航栈上本层的 ctx：折叠状态与文件树缓存都记在它上面，从子文件夹/看图返回时原样恢复。
async function renderDetail(ctx) {
  const id = ctx.id;
  $('title').textContent = ''; $('sort').hidden = true;
  $('topbar').classList.add('detail-mode');   // 顶栏不显示作品名，仅留按钮
  const d = await api('/api/detail?id=' + enc(id));
  if (d.error) { $('content').appendChild(el('div', 'empty', '作品不存在')); return; }
  const root = el('div', 'detail');
  root.appendChild(el('h1', null, d.name || d.id));
  // 折叠头：默认收起；没有作品文件可看时详情就是全部内容，直接展开
  if (ctx.open == null) ctx.open = !d.hasFiles;
  const fold = el('div', 'dfold' + (ctx.open ? ' open' : ''));
  const head = el('div', 'dfold-head');
  const arrow = el('span', 'arrow', ctx.open ? '▾' : '▸');
  head.append(arrow, el('span', null, '作品详情'), el('span', 'dfold-sub', [d.id, d.maker].filter(Boolean).join(' · ')));
  head.onclick = () => { ctx.open = !ctx.open; fold.classList.toggle('open', ctx.open); arrow.textContent = ctx.open ? '▾' : '▸'; };
  const inner = el('div', 'dfold-body');
  fold.append(head, inner); root.appendChild(fold);
  const top = el('div', 'dtop');
  const gallery = el('div', 'gallery');
  const imgs = d.slider.length ? d.slider.map(n => `/api/asset?id=${enc(d.id)}&name=${enc(n)}`) : (d.hasCover ? [`/api/cover?id=${enc(d.id)}`] : []);
  if (imgs.length) {
    const main = el('img', 'main'); main.src = imgs[0]; main.onclick = () => LB.open(imgs, 0); gallery.appendChild(main);
    if (imgs.length > 1) {
      const thumbs = el('div', 'thumbs');
      imgs.forEach((src, i) => { const t = el('img'); t.src = src + '&thumb=1'; t.loading = 'lazy'; if (i === 0) t.className = 'sel';
        t.onclick = () => { main.src = src; main.onclick = () => LB.open(imgs, i); thumbs.querySelectorAll('img').forEach(x => x.classList.remove('sel')); t.classList.add('sel'); }; thumbs.appendChild(t); });
      gallery.appendChild(thumbs);
    }
  }
  top.appendChild(gallery);
  const fields = el('div', 'fields');
  const addF = (k, node) => { if (node == null) return; fields.appendChild(el('div', 'k', k)); const v = el('div', 'v'); if (typeof node === 'string') v.textContent = node; else v.appendChild(node); fields.appendChild(v); };
  const link = (text, col, val) => { const a = el('a', null, text); a.onclick = () => pushView('filter', { col, val, label: text }); return a; };
  const multi = (text, col, sep) => { const w = el('span'); const parts = (text || '').split(sep).map(s => s.trim()).filter(Boolean); parts.forEach((p, i) => { w.appendChild(link(p, col, p)); if (i < parts.length - 1) w.appendChild(document.createTextNode(' / ')); }); return parts.length ? w : null; };
  // 作品号：点击在新标签打开 DLsite 作品页（对齐 WPF 详情 RJ 超链接）
  const idLink = el('a', null, d.id); idLink.href = `https://www.dlsite.com/maniax/work/=/product_id/${enc(d.id)}.html`; idLink.target = '_blank'; idLink.rel = 'noopener';
  addF('作品号', idLink);
  if (d.maker) addF('社团', link(d.maker, 'maker_name', d.maker));
  if (d.sellDate) addF('販売日', d.sellDate);
  if (d.series) addF('系列', link(d.series, 'series', d.series));
  if (d.scenario) addF('剧本', link(d.scenario, 'scenario', d.scenario));
  if (d.illust) addF('插画', link(d.illust, 'illust', d.illust));
  if (d.voiceActor) addF('声优', multi(d.voiceActor, 'voice_actor', '/'));
  if (d.age) addF('年龄分级', link(d.ageText || AGE[d.age] || d.age, 'age_category', d.age));
  if (d.workType) addF('作品形式', d.workType);
  // 类型标签：就地下钻到该标签的作品列表（保留详情所在层级，返回即回到本详情页），直接列作品、不再经社团分组
  if (d.tags && d.tags.length) { const box = el('span'); d.tags.forEach(t => { const a = el('a', 'tag', t); a.onclick = () => pushView('genreworks', { genre: t, label: t }); box.appendChild(a); }); addF('类型', box); }
  if (d.fileSize) addF('文件容量', d.fileSize);
  if (d.intro) addF('简介', d.intro);
  top.appendChild(fields); inner.appendChild(top);

  const actions = $('detailActions'); actions.hidden = false;
  const rb = el('button', 'toggle' + (d.read ? ' on' : ''), (d.read ? '★ ' : '☆ ') + '已读');
  rb.onclick = async () => { const r = await apiPost('/api/toggle', { id: d.id, field: 'read', value: !d.read }); d.read = r.value; rb.className = 'toggle' + (d.read ? ' on' : ''); rb.textContent = (d.read ? '★ ' : '☆ ') + '已读'; };
  const fb = el('button', 'toggle' + (d.favorite ? ' on' : ''), (d.favorite ? '♥ ' : '♡ ') + '收藏');
  fb.onclick = async () => { const r = await apiPost('/api/toggle', { id: d.id, field: 'favorite', value: !d.favorite }); d.favorite = r.value; fb.className = 'toggle' + (d.favorite ? ' on' : ''); fb.textContent = (d.favorite ? '♥ ' : '♡ ') + '收藏'; };
  actions.appendChild(rb); actions.appendChild(fb);
  if (d.hasFiles) {
    // 移动媒体库：选择目标库/文件夹 → 移动文件夹并改库（对齐 WPF 详情"移动媒体库"）
    const mb = el('button', 'toggle', '📦 移动媒体库');
    mb.onclick = async () => {
      const t = await api('/api/downtargets');
      if (!t.libs || !t.libs.length) { await uiAlert('尚未配置媒体库文件夹（请在桌面端添加）'); return; }
      const target = await pickTarget(t.libs);
      if (!target) return;
      if (!await uiConfirm(`确定将 ${d.id} 移动到媒体库“${target.lib}”吗？`)) return;
      mb.disabled = true; mb.textContent = '移动中…';
      const r = await apiPost('/api/movework', { id: d.id, lib: target.lib, folder: target.folder });
      await uiAlert(r.ok ? '已移动到新媒体库' : ('移动失败：' + (r.message || '')));
      if (r.ok) render(); else { mb.disabled = false; mb.textContent = '📦 移动媒体库'; }
    };
    actions.appendChild(mb);
  }
  // 删除作品：磁盘上的作品文件夹连同数据库记录一并清除，不可恢复，故二次确认
  // （文件夹不在了也要能删——正是清理这种孤儿记录的入口，所以不放进 hasFiles 分支）
  const delb = el('button', 'toggle danger', '🗑 删除作品');
  delb.onclick = async () => {
    if (!await uiConfirm(
      `确定删除《${d.name || d.id}》吗？\n作品文件夹内的全部文件与数据库记录都会被删除，且无法恢复。`,
      { title: '删除作品', okText: '删除', danger: true })) return;
    delb.disabled = true; delb.textContent = '删除中…';
    const r = await apiPost('/api/delwork', { id: d.id });
    if (r.ok) { history.back(); return; }   // 回上一级列表并重新拉取，被删的卡片随之消失
    await uiAlert('删除失败：' + (r.message || ''));
    delb.disabled = false; delb.textContent = '🗑 删除作品';
  };
  actions.appendChild(delb);

  if (d.body && d.body.length) {
    const body = el('div', 'body'); const bodyImgs = [];
    d.body.forEach(b => { if (b.kind === 'image') bodyImgs.push(`/api/asset?id=${enc(d.id)}&name=${enc(b.value)}`); });
    let ix = 0;
    d.body.forEach(b => {
      if (b.kind === 'text') { if (b.value.trim()) body.appendChild(el('p', null, b.value)); }
      else { const img = el('img'); img.src = `/api/asset?id=${enc(d.id)}&name=${enc(b.value)}`; img.loading = 'lazy'; const idx = ix++; img.onclick = () => LB.open(bodyImgs, idx); body.appendChild(img); }
    });
    inner.appendChild(body);
  }
  const host = $('content'); host.innerHTML = ''; host.appendChild(root); window.scrollTo(0, 0);
  // 作品文件：缩略图/文件列表，子文件夹仍逐级下钻（files 视图）。等它画完再返回，popView 才能恢复滚动位置
  if (d.hasFiles) {
    const files = el('div', 'dfiles'); host.appendChild(files);
    await drawFiles(files, ctx, '作品文件');
  }
}
