// accounts.js —— 账号访问（一级导航「账号访问」，分区 key accounts）：FANBOX / pixiv / E-Hentai 的登录账号。
// 账号只能「在服务器上登录」：运行本程序的电脑开一个内嵌浏览器（Services/LoginBrowser），画面串流到这里操作
// （stream.js 的 GS.openLogin），人登录后服务端自动取 cookie 存成账号。不提供手填 cookie（用户指定）。
// pixiv / FANBOX 可登多个账号、并行使用：作品搜索的来源下拉每个账号一项（search.js 的 searchSources）；
// E-Hentai 只留一个，再登录即替换。cookie 失效由服务端 AccountKeeper 从浏览器资料里自动续上，结果显示在账号行下。
// 服务端只回用户号、账号名与维护结果，cookie 从不回给网页。

let accounts = {};   // site → [{ uid, name, keep, canKeep }]，作品搜索的来源下拉也读它
let accountSites = [];   // [{ site, title, multi }]，页面顺序

async function loadAccounts() {
  let d;
  try { d = await api('/api/accounts'); } catch (e) { return; }
  accounts = {};
  accountSites = d.sites || [];
  accountSites.forEach(s => { accounts[s.site] = s.accounts || []; });
}

async function renderAccountsPage() {
  $('title').textContent = '账号访问';
  const host = $('content'); host.innerHTML = '<div class="empty"><span class="spin"></span> 加载中…</div>';
  await loadAccounts();
  if (section !== 'accounts') return;   // 加载期间已切走
  host.innerHTML = '';
  accountSites.forEach(s => host.appendChild(accountCard(s)));
}

function accountCard(meta) {
  const { site, title, multi } = meta;
  const list = accounts[site] || [];
  const s = el('div', 'sec acct');
  const head = el('h3');
  head.appendChild(el('span', null, title));
  head.appendChild(el('span', 'acct-st' + (list.length ? ' on' : ''),
    !list.length ? '未登录' : multi ? `已登录 ${list.length} 个账号` : `已登录（${list[0].name}）`));
  s.appendChild(head);

  list.forEach(a => s.appendChild(accountRow(site, title, a)));

  // 在运行本程序的电脑上开浏览器登录，画面串流过来操作；登录成功服务端自动存成账号
  const login = el('button', 'icon-btn primary',
    multi ? (list.length ? '＋ 在服务器上登录其它账号' : '🖥 在服务器上登录')
      : (list.length ? '🖥 在服务器上重新登录' : '🖥 在服务器上登录'));
  login.onclick = () => GS.openLogin(site, title, async msg => {
    if (section !== 'accounts') return;
    await renderAccountsPage();
    if (msg) uiAlert(msg, title);
  });
  const r = el('div', 'frow acct-add');
  r.append(login);
  s.appendChild(r);
  return s;
}

// 一个账号：名称（用户号）· 检测 · 退出登录，其下一行是自动维护的最近结果
function accountRow(site, title, a) {
  const row = el('div', 'acct-row');
  const line = el('div', 'frow');
  const name = el('span', 'acct-name', a.name);
  if (a.name !== a.uid) name.appendChild(el('span', 'acct-uid', a.uid));
  const result = el('span', 'tres');
  const say = (ok, msg) => { result.textContent = msg || ''; result.style.color = ok ? '#4ade80' : '#f87171'; };
  const check = el('button', 'icon-btn', '检测');
  check.onclick = async () => {
    check.disabled = true; check.textContent = '检测中…';
    let r;
    try { r = await apiPost('/api/account/check', { site, uid: a.uid }); }
    finally { check.disabled = false; check.textContent = '检测'; }
    say(r.ok, r.message);
  };
  const out = el('button', 'icon-btn', '退出登录');
  out.onclick = async () => {
    if (!await uiConfirm(`退出 ${title} 账号「${a.name}」？服务器上该账号的浏览器登录记录也会一并删除。`,
      { danger: true, okText: '退出登录' })) return;
    await apiPost('/api/account/logout', { site, uid: a.uid });
    renderAccountsPage();
  };
  line.append(name, check, out, result);
  row.appendChild(line);

  const keepText = a.keep ? `自动维护（${a.keep.at}）：${a.keep.message}`
    : !a.canKeep ? '旧版手动填写的登录，无法自动续期，失效后请重新在服务器上登录' : '';
  if (keepText) {
    const keep = el('div', 'note acct-keep', keepText);
    if (a.keep && !a.keep.ok) keep.style.color = '#f87171';
    row.appendChild(keep);
  }
  return row;
}
