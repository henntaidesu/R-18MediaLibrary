// boot.js —— 卡片区本地搜索监听 + 启动引导。最后加载。
// ---------- 卡片区本地搜索 ----------
let searchTimer = null;
$('search').addEventListener('input', () => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(() => {
    const st = stack.length ? stack[stack.length - 1] : null;
    const v = st ? st.view : '';
    const kw = $('search').value.trim();
    if (v === 'detail' || v === 'files') return;
    // 媒体库根 / 库内 / 标签·形式社团页：键入关键字 → 服务端作用域作品搜索；清空 → 恢复原视图
    if (['libs', 'libview', 'makers'].includes(v)) {
      if (kw) scopedWorkSearch(st, kw, ++searchGen);
      else { searchGen++; render(); }
      return;
    }
    if (['works', 'favorites', 'filter', 'genreworks'].includes(v)) drawWorks(curItems);
    else if (lastMap) drawGroups(curItems, lastMap, lastUnit);
  }, 200);
});

// ---------- 启动 ----------
async function boot() {
  applyLayout();
  syncStickyBars();
  buildTabs();
  try {
    const s = await fetch('/api/state').then(r => r.json());
    loginState = { password: s.password !== false, sso: !!s.sso };
    if (s.user) { currentUser = s.user; buildTabs(); }
    if (s.needLogin) { showLogin(true); return; }
  } catch (e) {}
  restoreFromHash();
}
// 按地址栏哈希恢复：叶子分区直接进入；卡片分区可深链接到具体层级（刷新/分享）
function restoreFromHash() {
  const p = parseHash();
  if (!p) { selectSection('medialib'); return; }
  if (p.key === 'search') { restoreSearch(p); return; }
  if (p.key === 'download') { restoreDownload(p); return; }
  if (!p.root) { selectSection(p.key); return; }
  section = p.key; stopTimers(); closeVideo(); closeAudio(); closeDrawer();
  buildTabs();
  makerSort = 0; workSort = 0;
  stack = p.stack.length ? p.stack : [{ view: SECTIONS.find(x => x.key === p.key).root, ctx: {} }];
  $('search').hidden = false; $('search').value = '';
  history.replaceState({ nav: true, depth: stack.length }, '', encodeHash());
  render();
}
// 恢复作品搜索分区：进入分区（同步建好 #srcSel / #sId）后切到对应来源，填回查询词并复跑一次
function restoreSearch(p) {
  selectSection('search');
  if (p.src && p.src !== searchSrc) {
    const sel = $('srcSel');
    if (sel) sel.value = p.src;
    setSearchSource(p.src);
  }
  if (!p.query) return;
  const inp = $('sId');
  if (!inp) return;
  inp.value = p.query;
  history.replaceState({ nav: true }, '', encodeSearchHash());
  runSearchAny();
}
// 恢复下载管理分区：先按常规进入（基线为下载列表），再切到已下载视图
function restoreDownload(p) {
  selectSection('download');
  if (p.dl !== 'downloaded') return;
  dlView = 'downloaded';
  renderDownloadArea();
  history.replaceState({ nav: true, dl: 'downloaded' }, '', encodeDlHash());
}
boot();
