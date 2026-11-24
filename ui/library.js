import {
  byId,
  collections,
  icon,
  iconButton,
  escape,
  groupChip,
  recordBadge,
  accountIcon,
  favoriteButton,
  dateLabel
} from './components.js';
import {codePanel} from './codes.js';
import {renderAgentHub} from './agents.js';

const collator = new Intl.Collator('zh-CN', {numeric: true, sensitivity: 'base'});

function navigationItem(id, name, glyph, count, selected, group = null) {
  const marker = group ? `<span class="nav-icon color-${group.color}">${icon(glyph)}</span>` : icon(glyph);
  const navigate = `<button type="button" class="nav-item${selected ? ' selected' : ''}" data-action="navigate" data-id="${id}"${selected ? ' aria-current="page"' : ''}>
    ${marker}<span class="nav-name">${escape(name)}</span><span class="nav-count">${count}</span></button>`;
  if (!group) return navigate;
  return `<div class="nav-group-row">${navigate}${iconButton('edit-group', 'pencil', '编辑分组 ' + name, id, 'nav-edit')}</div>`;
}

function groupCard(group, count) {
  const name = escape(group.name);
  return `<article class="group-card color-${group.color}">
    <button type="button" class="group-card-open" data-action="navigate" data-id="${group.id}">
      <span class="group-icon">${icon(group.icon)}</span><span><strong>${name}</strong><small>${count} 个账号</small></span>
    </button>
    <button type="button" class="group-card-edit" data-action="edit-group" data-id="${group.id}" aria-label="编辑分组 ${name}" title="编辑分组 ${name}">${icon('pencil')}编辑</button>
  </article>`;
}

function accountCard(account, group, grid, showCode) {
  const binding =
    account.otp && !showCode
      ? `<span class="binding-mark" title="已绑定 2FA" aria-label="已绑定 2FA">${icon('shield-lock')}</span>`
      : '';
  const open = `<button type="button" class="account-open account-ident" data-action="detail" data-id="${account.id}" aria-label="查看 ${escape(account.name)}">
    ${accountIcon(account)}<span class="account-ident-text"><span class="account-title-line"><strong>${escape(account.name)}</strong>${binding}${recordBadge(account)}</span><small>${escape(account.login)}</small></span></button>`;
  const date = `<time class="${grid ? 'tile-date' : 'row-date'}" title="${dateLabel(account.updated, true)}">${dateLabel(account.updated)}</time>`;
  if (showCode)
    return `<article class="code-card ${grid ? 'code-tile' : 'code-line'}"><div class="code-ident">${open}${groupChip(group)}</div>${codePanel(account)}</article>`;
  if (grid)
    return `<article class="account-tile">${open}${favoriteButton(account)}<div class="tile-foot">${groupChip(group)}${date}</div></article>`;
  return `<div class="account-line account-row">${open}${groupChip(group)}${date}${favoriteButton(account)}</div>`;
}

function emptyState(title, description, label, action, glyph) {
  return `<div class="empty-state"><div class="empty-art">${icon(glyph)}</div><h3>${title}</h3><p>${description}</p>
    <button type="button" class="text-button" data-action="${action}">${icon(action === 'add' ? 'plus-lg' : 'arrow-left')}${label}</button></div>`;
}

function renderAccounts(matches, groups, preferences, active, query) {
  const results = byId('account-results');
  byId('account-count').textContent = matches.length;
  byId('sort-label').textContent = preferences.sort === 'recent' ? '最近更新' : '按名称';
  const segment = document.querySelector('.view-segment');
  segment.dataset.mode = preferences.view;
  for (const button of segment.querySelectorAll('button'))
    button.setAttribute('aria-pressed', button.dataset.value === preferences.view);
  if (!matches.length) {
    let empty;
    if (query)
      empty = emptyState(
        '没有找到匹配的账号',
        '可以搜索名称、登录账号、网站或分组。',
        '清除搜索',
        'clear-search',
        'search'
      );
    else if (active === 'codes')
      empty = emptyState(
        '还没有绑定 2FA 的账号',
        '在账号中填写验证器密钥，即可在这里查看动态验证码。',
        '添加账号',
        'add',
        'shield-lock'
      );
    else if (active === 'favorites')
      empty = emptyState(
        '还没有收藏',
        '点亮账号旁的星标，即可在这里快速找到它。',
        '查看全部账号',
        'all',
        'star'
      );
    else if (active === 'ungrouped')
      empty = emptyState('没有未分组的账号', '尚未选择分组的账号会出现在这里。', '添加账号', 'add', 'inbox');
    else if (active !== 'all')
      empty = emptyState(
        '分组里还没有账号',
        '添加账号时，系统会选中当前分组。',
        '添加账号',
        'add',
        'folder2'
      );
    else
      empty = emptyState(
        '账号库还是空的',
        '从常用的网站、邮箱或应用开始。',
        '添加第一个账号',
        'add',
        'person-vcard'
      );
    results.innerHTML = empty;
    return;
  }
  const grid = preferences.view === 'grid';
  const showCode = active === 'codes';
  const cards = matches.map((a) => accountCard(a, groups.get(a.groupId), grid, showCode)).join('');
  if (showCode) {
    results.innerHTML = `<div class="${grid ? 'code-grid' : 'code-list'}">${cards}</div>`;
    return;
  }
  results.innerHTML = grid
    ? `<div class="account-grid">${cards}</div>`
    : `<div class="account-panel">
    <div class="account-line account-header" aria-hidden="true"><span>账号</span><span>分组</span><span class="date-heading">更新时间</span><span></span></div>${cards}</div>`;
}

export function renderLibrary(vault, active, searchText) {
  const groups = new Map(vault.groups.map((group) => [group.id, group]));
  const counts = {all: vault.accounts.length, agents: 0, codes: 0, favorites: 0, ungrouped: 0};
  for (const account of vault.accounts) {
    const key = account.groupId || 'ungrouped';
    counts[key] = (counts[key] || 0) + 1;
    if (account.favorite) counts.favorites++;
    if (account.otp) counts.codes++;
    if (account.agentClient) counts.agents++;
  }
  const group = groups.get(active);
  const query = searchText.trim().normalize('NFKC').toLocaleLowerCase();
  const name = group?.name || collections[active].name;
  const currentTitle = query ? '搜索结果' : name;
  byId('crumb-name').textContent = currentTitle;
  byId('library-nav').innerHTML = Object.entries(collections)
    .map(([id, c]) => navigationItem(id, c.name, c.icon, counts[id], !query && active === id))
    .join('');
  byId('group-nav').innerHTML = vault.groups
    .map((g) => navigationItem(g.id, g.name, g.icon, counts[g.id] || 0, !query && active === g.id, g))
    .join('');
  byId('hero').hidden = !!query;
  const agentHub = active === 'agents';
  byId('agent-section').hidden = !agentHub;
  byId('accounts-section').hidden = agentHub;
  document.querySelector('.add-account span').textContent = agentHub ? '添加切换账号' : '添加账号';
  byId('clear-search').hidden = !searchText;
  document.querySelector('.search-box kbd').hidden = !!searchText;
  byId('groups-section').hidden = active !== 'all' || !!query;
  if (agentHub) {
    byId('account-results').replaceChildren();
    renderAgentHub(vault, searchText);
    return;
  }
  byId('hero-title').textContent = active === 'all' ? '我的账号库' : name;
  byId('hero-total').textContent = `${counts[active] || 0} 个账号`;
  byId('group-cards').innerHTML = vault.groups.length
    ? vault.groups.map((g) => groupCard(g, counts[g.id] || 0)).join('')
    : `<div class="group-card color-indigo"><button type="button" class="group-card-open" data-action="new-group"><span class="group-icon">${icon('folder-plus')}</span><strong>新建分组</strong></button></div>`;
  const matches = vault.accounts.filter((account) => {
    if (active === 'codes' && !account.otp) return false;
    if (query)
      return [account.name, account.login, account.website, groups.get(account.groupId)?.name].some((value) =>
        value?.normalize('NFKC').toLocaleLowerCase().includes(query)
      );
    if (active === 'all' || active === 'codes') return true;
    if (active === 'favorites') return account.favorite;
    if (active === 'ungrouped') return !account.groupId;
    return account.groupId === active;
  });
  const meta = byId('hero-meta');
  if (active === 'all') {
    meta.innerHTML = `<span>${icon('folder2')}${vault.groups.length} 个分组</span><b class="separator"></b><span>${icon('star')}${counts.favorites} 个收藏</span>`;
  } else if (group) {
    meta.innerHTML = `<span>${icon('star')}${matches.filter((a) => a.favorite).length} 个收藏</span><b class="separator"></b><button type="button" class="hero-edit" data-action="edit-group" data-id="${group.id}">${icon('pencil')}编辑分组</button>`;
  } else {
    meta.textContent =
      active === 'codes'
        ? '验证码按账号设置的周期自动刷新'
        : active === 'favorites'
          ? '已加星标的账号'
          : '尚未归类的账号';
  }
  matches.sort((a, b) => preferencesOrder(a, b, vault.preferences.sort));
  byId('accounts-title').textContent = query
    ? '搜索结果'
    : active === 'codes'
      ? '动态验证码'
      : group
        ? '分组账号'
        : active === 'all'
          ? name
          : '账号列表';
  renderAccounts(matches, groups, vault.preferences, active, query);
}

function preferencesOrder(a, b, sort) {
  if (sort === 'name')
    return collator.compare(a.name, b.name) || collator.compare(a.login, b.login) || b.updated - a.updated;
  return b.updated - a.updated || collator.compare(a.name, b.name);
}
