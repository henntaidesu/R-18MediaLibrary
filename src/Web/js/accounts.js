// accounts.js —— 账号访问（一级导航「账号访问」，分区 key accounts）：FANBOX / pixiv / E-Hentai 的登录。
// 三个站的鉴权都是 cookie。pixiv 与 FANBOX 的登录带人机验证，程序里走不通，只能在浏览器登录后复制 cookie；
// E-Hentai 另有账号密码登录（服务端代登论坛、换回三个 cookie），账号密码按用户要求存库。
// 服务端只回「登没登、账号名」，cookie 与密码从不回给网页：输入框只写不读，留空即保持不变。
// 页面不放说明文字，只有 FANBOX / pixiv 留一行 cookie 的获取方法（用户指定）。
// FANBOX 登录后，作品搜索的来源下拉多一项「FANBOX（账号名）」（search.js 的 searchSources），直连 fanbox.cc 官方站。

let accounts = {};   // site → { loggedIn, account, title }，作品搜索的来源下拉也读它

async function loadAccounts() {
  let d;
  try { d = await api('/api/accounts'); } catch (e) { return; }
  accounts = {};
  (d.accounts || []).forEach(a => { accounts[a.site] = a; });
  accounts._ehUsername = d.ehUsername || '';
  accounts._ehHasPassword = !!d.ehHasPassword;
}

// 各站点的 cookie 字段：[请求体字段名, 标签]
const ACCOUNT_FIELDS = {
  fanbox: [['sessid', 'FANBOXSESSID']],
  pixiv: [['php_sessid', 'PHPSESSID']],
  ehentai: [['member_id', 'ipb_member_id'], ['pass_hash', 'ipb_pass_hash'], ['igneous', 'igneous']],
};
// 获取方法：去哪个网站登录、从哪个域的 cookie 里复制（只有 FANBOX / pixiv 显示）
const ACCOUNT_SITES = {
  fanbox: { url: 'https://www.fanbox.cc/', domain: 'fanbox.cc' },
  pixiv: { url: 'https://www.pixiv.net/', domain: 'pixiv.net' },
};

async function renderAccountsPage() {
  $('title').textContent = '账号访问';
  const host = $('content'); host.innerHTML = '<div class="empty"><span class="spin"></span> 加载中…</div>';
  await loadAccounts();
  if (section !== 'accounts') return;   // 加载期间已切走
  host.innerHTML = '';
  ['fanbox', 'pixiv', 'ehentai'].forEach(site => { if (accounts[site]) host.appendChild(accountCard(site)); });
}

function accountCard(site) {
  const a = accounts[site];
  const s = el('div', 'sec acct');
  const head = el('h3');
  head.appendChild(el('span', null, a.title));
  head.appendChild(el('span', 'acct-st' + (a.loggedIn ? ' on' : ''),
    a.loggedIn ? `已登录（${a.account}）` : '未登录'));
  s.appendChild(head);

  const result = el('span', 'tres');
  const say = (ok, msg) => { result.textContent = msg || ''; result.style.color = ok ? '#4ade80' : '#f87171'; };
  const busy = (btn, on, text) => { btn.disabled = on; if (text) btn.textContent = text; };

  // E-Hentai：账号密码登录（成功即写好下面三个 cookie）。密码已存过时留空即沿用
  if (site === 'ehentai') {
    const user = el('input'); user.value = accounts._ehUsername || '';
    user.style.width = '180px';
    const pass = el('input'); pass.type = 'password';
    if (accounts._ehHasPassword) pass.placeholder = '已保存';
    pass.style.width = '200px';
    const login = el('button', 'icon-btn primary', '登录并获取 Cookie');
    login.onclick = async () => {
      if (!user.value.trim() || (!pass.value && !accounts._ehHasPassword)) { say(false, '请先填写账号与密码'); return; }
      busy(login, true, '登录中…');
      let r;
      try { r = await apiPost('/api/account/login', { site, username: user.value.trim(), password: pass.value }); }
      finally { busy(login, false, '登录并获取 Cookie'); }
      pass.value = '';
      if (!r.ok) { say(false, r.message); return; }
      await uiAlert(r.message, '登录成功');
      renderAccountsPage();
    };
    const r1 = el('div', 'frow');
    r1.append(el('label', null, '账号'), user, el('label', null, '密码'), pass, login);
    s.appendChild(r1);
  }

  // Cookie 输入：只写不读，留空 = 保持原值不变
  const inputs = {};
  ACCOUNT_FIELDS[site].forEach(([key, label]) => {
    const inp = el('input', 'grow'); inp.type = 'password';
    inputs[key] = inp;
    const row = el('div', 'frow');
    row.append(el('label', null, label), inp);
    s.appendChild(row);
  });

  const save = el('button', 'icon-btn primary', '保存并检测');
  save.onclick = async () => {
    const body = { site };
    let any = false;
    Object.entries(inputs).forEach(([k, inp]) => { const v = inp.value.trim(); if (v) { body[k] = v; any = true; } });
    if (!any) { say(false, '没有填写任何内容'); return; }
    busy(save, true, '检测中…');
    try {
      await apiPost('/api/account/save', body);
      const r = await apiPost('/api/account/check', { site });
      say(r.ok, r.message);
      if (r.ok) { await uiAlert(r.message, '登录有效'); renderAccountsPage(); }
    } finally { busy(save, false, '保存并检测'); }
  };
  const check = el('button', 'icon-btn', '检测');
  check.disabled = !a.loggedIn;
  check.onclick = async () => {
    busy(check, true, '检测中…');
    let r;
    try { r = await apiPost('/api/account/check', { site }); } finally { busy(check, false, '检测'); }
    say(r.ok, r.message);
    await loadAccounts();
    // 名字可能刚查到：标题行跟着刷新
    head.lastChild.textContent = accounts[site].loggedIn ? `已登录（${accounts[site].account}）` : '未登录';
  };
  const out = el('button', 'icon-btn', '退出登录');
  out.disabled = !a.loggedIn;
  out.onclick = async () => {
    if (!await uiConfirm(`清除 ${a.title} 的登录信息？`, { danger: true, okText: '退出登录' })) return;
    await apiPost('/api/account/logout', { site });
    renderAccountsPage();
  };
  const actions = el('div', 'frow');
  actions.append(el('label'), save, check, out, result);
  s.appendChild(actions);

  const where = ACCOUNT_SITES[site];
  if (where) {
    const tip = el('div', 'note acct-tip');
    tip.append('获取方法：在浏览器登录 ');
    const link = el('a', null, where.url); link.href = where.url; link.target = '_blank'; link.rel = 'noreferrer';
    tip.append(link, ` 后按 F12 →「应用 / Application」→ Cookie → ${where.domain}，复制对应名称的「值」粘到上面。`);
    s.appendChild(tip);
  }
  return s;
}
