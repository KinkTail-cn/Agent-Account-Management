import {accountIcon, byId, escape, groupChip, icon, iconButton, recordBadge} from './components.js';
import {native} from './bridge.js';
import {quotaPanel} from './quota.js';

const providers = {
  chatgpt: {name: 'ChatGPT', color: 'mint', icon: 'window-stack'},
  claude: {name: 'Claude', color: 'peach', icon: 'brightness-high'},
  antigravity: {name: 'Antigravity', color: 'sky', icon: 'layers'}
};
let clients = [];
let provider = 'all';

export async function detectClients() {
  clients = await native('agentCatalog');
}

export const getClient = id => clients.find(client => client.id === id);
export const hasProjectFolder = id => ['codex-cli', 'claude-code', 'antigravity-ide'].includes(id);
export const selectProvider = selected => { provider = selected; };
export function defaultClient() {
  const options = clients.filter(c => provider === 'all' || c.provider === provider);
  return (options.find(c => c.available) || options[0])?.id || '';
}

export function clientPicker(record, open = false) {
  const options = Object.entries(providers).map(([id, info]) => {
    const group = clients.filter(client => client.provider === id);
    return `<optgroup label="${info.name}">${group.map(c => `<option value="${c.id}"${record.agentClient === c.id ? ' selected' : ''}>${escape(c.label)}${c.available ? '' : '（未检测到）'}</option>`).join('')}</optgroup>`;
  });
  return `<details class="group-members client-picker"${record.agentClient || open ? ' open' : ''}>
    <summary><strong>${icon('window-stack')} Agent 切换</strong><span class="picker-state">${escape(getClient(record.agentClient)?.label || '选择客户端')}</span>${icon('chevron-down')}</summary>
    <div class="member-picker"><label class="form-field"><span class="field-label">启动客户端</span>
      <select id="agent-client" name="agentClient" class="field-control"><option value="">不绑定</option>${options.join('')}</select></label>
      <p class="field-hint">首次在官方客户端登录。以后打开这个账号时，会沿用它自己的登录环境。</p>
      <div id="agent-folder-row" class="agent-folder-row"${hasProjectFolder(record.agentClient) ? '' : ' hidden'}>
        <span class="field-label">项目文件夹</span>
        <input type="hidden" name="agentFolder" id="agent-folder" value="${escape(record.agentFolder)}">
        <small id="agent-folder-label" title="${escape(record.agentFolder)}">${escape(record.agentFolder || '默认工作目录')}</small>
        <button type="button" class="text-button" data-action="choose-folder">选择</button>
        <button type="button" class="text-button" data-action="clear-folder"${record.agentFolder ? '' : ' hidden'}>清除</button>
      </div>
    </div></details>`;
}

export function setFolder(path) {
  byId('agent-folder').value = path || '';
  byId('agent-folder-label').textContent = path || '默认工作目录';
  byId('agent-folder-label').title = path || '';
  document.querySelector('[data-action="clear-folder"]').hidden = !path;
}

export function updateClientChoice() {
  const client = byId('agent-client').value;
  document.querySelector('.picker-state').textContent = getClient(client)?.label || '选择客户端';
  byId('agent-folder-row').hidden = !hasProjectFolder(client);
}

export function bindingForm(vault) {
  const choices = vault.accounts.filter(a => !a.agentClient);
  if (!choices.length) return `<p class="sheet-desc">账号库中没有待绑定的账号。</p><div class="sheet-actions"><button type="button" class="primary" data-action="new-agent">添加账号</button></div>`;
  const options = choices.map(a => `<option value="${a.id}">${escape(a.name)} · ${escape(a.login)}</option>`).join('');
  return `<form id="bind-agent-form" class="sheet-form">
    <label class="form-field"><span class="field-label">账号库中的账号</span><select id="bind-account" class="field-control">${options}</select></label>
    ${clientPicker({agentClient: defaultClient()}, true)}<p role="alert" class="form-error"></p>
    <div class="sheet-actions"><button type="button" class="secondary" data-action="back">取消</button><button type="submit" class="primary">绑定客户端</button></div></form>`;
}

function sessionRow(account, groups) {
  const client = getClient(account.agentClient);
  if (!client) return '';
  const previous = account.agentLastOpened > 0;
  const terminal = client.id === 'codex-cli' || client.id === 'claude-code';
  const google = client.provider === 'antigravity';
  const label = previous ? '切换到账号' : google ? '首次登录' : '首次打开';
  const login = terminal ? `<button type="button" class="text-button" data-action="agent-login" data-id="${account.id}"${client.available ? '' : ' disabled'}>登录</button>` : '';
  return `<article class="session-row">
    <button type="button" class="session-ident account-ident" data-action="detail" data-id="${account.id}">
      ${accountIcon(account)}<span class="account-ident-text"><span class="account-title-line"><strong>${escape(account.name)}</strong>${recordBadge(account)}</span><small>${escape(account.login)}</small></span></button>
    <div class="session-client"><strong>${escape(client.label)}</strong><small>${client.available ? (previous ? '上次打开 ' + escape(new Date(account.agentLastOpened).toLocaleDateString('zh-CN')) : '等待首次打开') : '未检测到客户端'}</small></div>
    ${groupChip(groups.get(account.groupId))}
    <div class="session-actions">${login}${google && previous ? `<button type="button" class="text-button" data-action="google-relogin" data-id="${account.id}">重新登录</button>` : ''}<button type="button" class="secondary" data-action="launch-agent" data-id="${account.id}"${client.available ? '' : ' disabled'}>${icon('arrow-right')}${label}</button>
      ${iconButton('edit-account', 'pencil', '编辑 ' + account.name, account.id)}</div>${quotaPanel(account)}</article>`;
}

export function renderAgentHub(vault, query) {
  const hub = byId('agent-section');
  const accounts = vault.accounts.filter(a => a.agentClient);
  const filters = [['all', '全部'], ...Object.entries(providers).map(([id, p]) => [id, p.name])];
  const groups = new Map(vault.groups.map(g => [g.id, g]));
  const match = query.trim().normalize('NFKC').toLocaleLowerCase();
  const rows = accounts.filter(a => {
    const client = getClient(a.agentClient);
    return client && (provider === 'all' || client.provider === provider)
      && [a.name, a.login, client.label, groups.get(a.groupId)?.name].some(v => v?.normalize('NFKC').toLocaleLowerCase().includes(match));
  }).sort((a, b) => b.agentLastOpened - a.agentLastOpened || a.name.localeCompare(b.name, 'zh-CN'));
  const filtersHtml = filters.map(([id, label]) => {
    const count = id === 'all' ? accounts.length : accounts.filter(a => getClient(a.agentClient)?.provider === id).length;
    return `<button type="button" data-action="agent-provider" data-value="${id}" aria-pressed="${provider === id}">${escape(label)}<small>${count}</small></button>`;
  }).join('');
  byId('hero-title').textContent = 'Agent 账号切换';
  byId('hero-total').textContent = `${accounts.length} 个切换账号`;
  byId('hero-meta').textContent = '选一个账号，打开它自己的客户端。';
  byId('crumb-name').textContent = 'Agent 切换';
  hub.innerHTML = `<div class="section-heading"><h2>切换账号</h2><div class="hub-tools">
      <button type="button" class="text-button" data-action="detect-agents" title="重新检测本机客户端">${icon('arrow-repeat')}重新检测</button>
      <button type="button" class="text-button" data-action="bind-agent">${icon('person-vcard')}绑定已有账号</button></div></div>
    <div class="agent-filters" role="group" aria-label="客户端筛选">${filtersHtml}</div>
    <div class="session-list">${rows.length ? rows.map(a => sessionRow(a, groups)).join('') :
      `<div class="empty-state"><div class="empty-art">${icon('window-stack')}</div><h3>${match ? '没有匹配的账号' : '还没有切换账号'}</h3>
      <p>${match ? '试试其他账号名称或登录名。' : '添加账号并选择客户端，首次打开后完成登录。'}</p>
      <button type="button" class="text-button" data-action="${match ? 'clear-search' : 'new-agent'}">${icon(match ? 'search' : 'plus-lg')}${match ? '清除搜索' : '添加切换账号'}</button></div>`}</div>`;
}
