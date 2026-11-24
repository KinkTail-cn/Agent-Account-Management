import {
  accountIcon,
  byId,
  colors,
  dateLabel,
  escape,
  favoriteButton,
  groupChip,
  groupIcons,
  icon,
  recordStates,
  recordBadge,
  iconButton
} from './components.js';
import {codePanel} from './codes.js';
import {bindingForm, clientPicker, getClient} from './agents.js';
import {quotaPanel} from './quota.js';
import {updatePanel, paintUpdates} from './updates.js';

const optional = '<span class="optional">选填</span>';
const formError = '<p class="form-error" role="alert"></p>';

function frame(title, body) {
  return `<div class="sheet-head"><h2 id="sheet-title">${title}</h2>
    ${iconButton('back', 'x-lg', '关闭', '', 'sheet-close')}</div><div class="sheet-body">${body}</div>`;
}

function actions(saveLabel, extra = '') {
  return `<div class="sheet-actions">${extra}<button type="button" class="secondary" data-action="back">取消</button>
    <button type="submit" class="primary">${saveLabel}</button></div>`;
}

function accountForm(vault, screen) {
  const draft = screen.draft;
  const options = vault.groups.map(
    (g) => `<option value="${g.id}"${draft.groupId === g.id ? ' selected' : ''}>${escape(g.name)}</option>`
  );
  const favorite = `<label class="favorite-check"><input type="checkbox" id="account-favorite" name="favorite"${draft.favorite ? ' checked' : ''}>${icon('star')}加入收藏</label>`;
  const bound = draft.otp && !draft.removeOtp;
  const otpAction = draft.otp
    ? `<button type="button" class="text-button" data-action="remove-otp">${draft.removeOtp ? '撤销移除' : '移除绑定'}</button>`
    : '';
  return frame(
    screen.id ? '编辑账号' : '添加账号',
    `<form id="account-form" class="sheet-form" autocomplete="off">
    <div class="form-field"><label for="account-name">账号名称</label><input id="account-name" name="name" class="field-control" maxlength="100" required placeholder="例如：工作邮箱" value="${escape(draft.name)}" autofocus></div>
    <div class="form-field"><label for="account-login">登录账号</label><input id="account-login" name="login" class="field-control" maxlength="250" required placeholder="用户名、邮箱或手机号" spellcheck="false" value="${escape(draft.login)}"></div>
    <div class="form-field"><label for="account-status">账号状态<span class="optional">手动标记</span></label><select class="field-control" id="account-status" name="recordStatus"><option value="">未标记</option>${Object.entries(recordStates).map(([id, label]) => `<option value="${id}"${draft.recordStatus === id ? ' selected' : ''}>${label}</option>`).join('')}</select></div>
    <div class="form-field"><label for="account-password">密码${optional}</label><div class="password-line"><input id="account-password" name="password" type="password" class="field-control" maxlength="1024" autocomplete="new-password" placeholder="输入密码">${iconButton('form-password', 'eye', '显示密码')}</div></div>
    <div class="form-field"><div class="field-caption"><label for="account-otp">2FA 验证器密钥${optional}</label>${otpAction}</div>
      <input id="account-otp" name="otpInput" type="password" class="field-control" maxlength="4096" autocomplete="new-password" spellcheck="false" placeholder="${bound ? '已绑定，填写新密钥可替换' : '粘贴 Base32 密钥或 otpauth:// 链接'}" aria-describedby="otp-hint">
      <small id="otp-hint" class="field-hint">${bound ? `当前设置为 ${draft.otp.digits} 位、${draft.otp.period} 秒，留空保留绑定。` : draft.removeOtp ? '保存后移除当前绑定。' : '保存后可在「2FA 验证码」中查看。'}</small></div>
    <div class="form-field"><div class="field-caption"><label for="account-group">所属分组</label><button type="button" class="text-button" data-action="new-group">${icon('plus-lg')}新建分组</button></div>
      <select class="field-control" id="account-group" name="groupId"><option value="">未分组</option>${options.join('')}</select></div>
    <div class="form-field"><label for="account-website">网站${optional}</label><input id="account-website" name="website" class="field-control" maxlength="2048" placeholder="example.com" inputmode="url" spellcheck="false" value="${escape(draft.website)}"></div>
    <div class="form-field"><label for="account-note">备注${optional}</label><textarea id="account-note" name="note" class="field-control" maxlength="4000" placeholder="添加备注">${escape(draft.note)}</textarea></div>
    ${clientPicker(draft)}
    ${formError}${actions('保存账号', favorite)}</form>`
  );
}

export function readAccountForm() {
  const data = new FormData(byId('account-form'));
  return {
    name: data.get('name'),
    login: data.get('login'),
    recordStatus: data.get('recordStatus') || null,
    password: data.get('password'),
    groupId: data.get('groupId') || null,
    agentClient: data.get('agentClient') || null,
    agentFolder: data.get('agentFolder') || null,
    website: data.get('website'),
    note: data.get('note'),
    favorite: data.has('favorite'),
    otpInput: data.get('otpInput')
  };
}

function accountDetail(vault, screen) {
  const account = vault.accounts.find((a) => a.id === screen.id);
  const group = vault.groups.find((g) => g.id === account.groupId);
  const copy = (field, label) =>
    `<button class="icon-button" type="button" title="复制${label}" aria-label="复制${label}" data-action="copy" data-field="${field}" data-id="${account.id}">${icon('copy')}</button>`;
  const field = (label, value, buttons = '') =>
    `<div class="detail-field"><div class="detail-value"><small>${label}</small>${value}</div>${buttons}</div>`;
  const login = field('登录账号', `<strong>${escape(account.login)}</strong>`, copy('login', '登录账号'));
  let password = field('密码', '<span class="muted-text">未填写</span>');
  if (account.password) {
    password = field(
      '密码',
      '<strong class="secret" id="detail-password">••••••••</strong>',
      iconButton('detail-password', 'eye', '显示密码', account.id) + copy('password', '密码')
    );
  }
  let website = '';
  if (account.website) {
    website = field(
      '网站',
      `<button type="button" class="website-link" data-action="open-website" data-id="${account.id}">${escape(account.website)} ${icon('box-arrow-up-right')}</button>`,
      copy('website', '网站')
    );
  }
  const note = account.note
    ? `<div class="note-block"><small>备注</small><p>${escape(account.note)}</p></div>`
    : '';
  return frame(
    '账号详情',
    `<div class="detail-top"><div class="detail-ident">${accountIcon(account)}<div><h3>${escape(account.name)}</h3>${groupChip(group)}</div></div>${favoriteButton(account)}</div>
    ${account.otp ? codePanel(account) : ''}<div class="detail-fieldset">${login}${password}${website}${field('账号状态 · 手动标记', recordBadge(account, true))}</div>${note}
    ${account.agentClient ? `<div class="detail-agent"><span>${icon('window-stack')} ${escape(getClient(account.agentClient)?.label)}</span><button type="button" class="secondary" data-action="launch-agent" data-id="${account.id}">打开客户端 ${icon('box-arrow-up-right')}</button></div>` : ''}
    ${account.agentClient ? quotaPanel(account) : ''}
    <div class="detail-dates">创建于 ${dateLabel(account.created, true)}<br>更新于 ${dateLabel(account.updated, true)}</div>
    ${formError}<div class="sheet-actions detail-actions"><button type="button" class="danger-button" data-action="delete-account" data-id="${account.id}">${icon('trash3')}删除</button>
    <button type="button" class="primary" data-action="edit-account" data-id="${account.id}">${icon('pencil')}编辑账号</button></div>`
  );
}

function groupManager(vault) {
  const counts = new Map();
  for (const a of vault.accounts) counts.set(a.groupId, (counts.get(a.groupId) || 0) + 1);
  const rows = vault.groups.map(
    (
      g
    ) => `<div class="manage-row color-${g.color}"><span class="group-icon">${icon(g.icon)}</span><div><strong>${escape(g.name)}</strong><small>${counts.get(g.id) || 0} 个账号</small></div>
    ${iconButton('edit-group', 'pencil', '编辑分组 ' + g.name, g.id)}${iconButton('delete-group', 'trash3', '删除分组 ' + g.name, g.id)}</div>`
  );
  return frame(
    '管理分组',
    `<div>${rows.join('') || '<p class="manage-empty">还没有分组</p>'}</div>
    <button type="button" class="secondary manage-add" data-action="new-group">${icon('plus-lg')}新建分组</button>`
  );
}

function groupMembers(vault, groupId) {
  if (!vault.accounts.length) return '';
  const groups = new Map(vault.groups.map((group) => [group.id, group]));
  let selected = 0;
  const rows = vault.accounts.map((account) => {
    const checked = Boolean(groupId && groupId === account.groupId);
    if (checked) selected++;
    return `<label class="member-row">
      <input type="checkbox" name="member" value="${account.id}"${checked ? ' checked' : ''}>
      <span class="member-identity"><strong>${escape(account.name)}</strong><small>${escape(account.login)}</small></span>
      ${groupChip(groups.get(account.groupId))}
    </label>`;
  });
  return `<details class="group-members">
    <summary><strong>组内账号</strong><output id="member-count">已选 ${selected} 个</output>${icon('chevron-down')}</summary>
    <div class="member-picker"><p class="field-hint">勾选的账号会移入本组，取消勾选的本组账号会移至「未分组」。</p>
      <input type="search" class="field-control" id="member-search" aria-label="搜索组内候选账号" placeholder="搜索账号名称或登录账号" autocomplete="off">
      <div class="member-list">${rows.join('')}<p id="member-empty" class="manage-empty" hidden>没有匹配的账号</p></div>
    </div></details>`;
}

function groupForm(vault, screen) {
  const g = screen.draft;
  const swatches = Object.entries(colors).map(
    ([color, label]) =>
      `<button type="button" class="swatch color-${color}" data-action="group-color" data-value="${color}" title="${label}" aria-label="${label}" aria-pressed="${g.color === color}">${g.color === color ? icon('check-lg') : ''}</button>`
  );
  const choices = Object.entries(groupIcons).map(
    ([glyph, label]) =>
      `<button type="button" class="icon-option" data-action="group-icon" data-value="${glyph}" title="${label}" aria-label="${label}" aria-pressed="${g.icon === glyph}">${icon(glyph)}</button>`
  );
  return frame(
    screen.id ? '编辑分组' : '新建分组',
    `<form id="group-form" class="sheet-form">
    <div class="group-preview color-${g.color}"><span class="group-icon">${icon(g.icon)}</span><strong id="group-preview-name">${escape(g.name) || '新分组'}</strong></div>
    <div class="form-field"><label for="group-name">分组名称</label><input id="group-name" class="field-control" maxlength="40" required autocomplete="off" placeholder="例如：游戏" value="${escape(g.name)}" autofocus></div>
    <div class="form-field"><span class="field-label" id="color-label">颜色</span><div class="option-row" role="group" aria-labelledby="color-label">${swatches.join('')}</div></div>
    <div class="form-field"><span class="field-label" id="icon-label">图标</span><div class="option-row" role="group" aria-labelledby="icon-label">${choices.join('')}</div></div>
    ${groupMembers(vault, screen.id)}${formError}${actions('保存分组')}</form>`
  );
}

function confirmDelete(vault, screen) {
  const deletingGroup = screen.kind === 'confirm-group';
  const item = (deletingGroup ? vault.groups : vault.accounts).find((record) => record.id === screen.id);
  const kind = deletingGroup ? '分组' : '账号';
  const count = deletingGroup ? vault.accounts.filter((a) => a.groupId === item.id).length : 0;
  const info = deletingGroup && count ? `其中的 ${count} 个账号将保留在「未分组」。` :
    item.agentClient ? '客户端的登录环境会保留。如需退出账号，请先在对应客户端退出登录。' : '此操作无法撤销。';
  return frame(
    '删除' + kind,
    `<p class="sheet-desc">确定删除${kind}「${escape(item.name)}」吗？${info}</p>${formError}
    <div class="sheet-actions"><button class="secondary" type="button" data-action="back" autofocus>取消</button><button class="danger-button destructive" type="button" data-action="confirm-delete">删除${kind}</button></div>`
  );
}

function settings(screen) {
  const p = screen.draft;
  const themes = [
    ['system', 'circle-half', '跟随系统'],
    ['light', 'brightness-high', '浅色'],
    ['dark', 'moon-stars', '深色']
  ];
  const choices = themes.map(
    ([theme, glyph, label]) =>
      `<button type="button" data-action="theme" data-value="${theme}" aria-pressed="${p.theme === theme}">${icon(glyph)}${label}</button>`
  );
  return frame(
    '外观设置',
    `<form id="settings-form"><div class="settings-group"><h3 id="theme-label">外观</h3><div class="segment appearance-segment" role="group" aria-labelledby="theme-label"><span class="segment-thumb"></span>${choices.join('')}</div></div>
    <div class="settings-group"><div class="settings-line"><label class="settings-label" for="glass-range">玻璃浓度</label><output id="glass-output" for="glass-range">${p.glass}%</output></div>
      <input class="range" type="range" id="glass-range" min="0" max="100" value="${p.glass}"><div class="range-labels"><span>通透</span><span>浓郁</span></div></div>
    <div class="settings-group settings-line"><div><h3 id="reduce-label">减少动态效果</h3><p>关闭视差与弹性转场，也会遵循系统偏好。</p></div><button type="button" class="switch" role="switch" data-action="motion" aria-labelledby="reduce-label" aria-checked="${p.reduceMotion}"></button></div>
    ${formError}${actions('保存设置')}</form>`
  );
}

export function paintScreen(vault, screen) {
  const content = byId('sheet-content');
  switch (screen.kind) {
    case 'updates':
      content.innerHTML = frame('软件更新', updatePanel());
      paintUpdates();
      break;
    case 'google-login': {
      const account = vault.accounts.find(a => a.id === screen.id);
      content.innerHTML = frame('登录 Antigravity', `<div class="group-preview color-sky">${accountIcon(account)}<div><strong>${escape(account.name)}</strong><p class="sheet-desc">${escape(account.login)}</p></div></div>
        <p class="google-login-status sheet-desc" id="google-login-status" role="status">正在生成此账号专属的 Google 授权请求…</p>
        <small class="field-hint" id="google-deadline" hidden></small>
        <div id="google-code-box" hidden><button type="button" class="text-button" data-action="google-browser">${icon('box-arrow-up-right')}重新打开授权窗口</button>
          <details class="group-members"><summary>Google 页面显示了授权码${icon('chevron-down')}</summary>
            <form id="google-code-form" class="member-picker"><label for="google-code" class="field-label">本次授权码</label>
              <input type="password" id="google-code" class="field-control" maxlength="4096" required autocomplete="off" spellcheck="false">
               <button type="submit" class="secondary" id="google-submit">提交给 Antigravity</button></form></details></div>
        <p role="alert" class="form-error"></p><div class="sheet-actions">
        <button type="button" class="secondary" data-action="back">关闭</button>
        <button type="button" class="text-button" id="google-cancel" data-action="google-cancel">取消本次授权</button>
        <button type="button" class="primary" id="google-retry" data-action="google-retry" hidden>重新获取授权码</button>
        <button type="button" class="primary" id="google-open" data-action="launch-agent" data-id="${account.id}" hidden>打开 ${escape(getClient(account.agentClient).label)}</button></div>`);
      break;
    }
    case 'quota-import':
      content.innerHTML = frame('摘录网页用量', `<form class="sheet-form" id="quota-import-form"><p class="sheet-desc">从这个账号的官方用量页复制模型或周期名称，以及带「已用」或「剩余」的百分比。</p>
        <label class="form-field"><span class="field-label">页面文字</span><textarea class="field-control" id="quota-text" maxlength="16000" required autofocus placeholder="Current session&#10;12% used"></textarea></label>${formError}${actions('保存摘录')}</form>`);
      break;
    case 'bind-agent':
      content.innerHTML = frame('绑定已有账号', bindingForm(vault));
      break;
    case 'account':
      content.innerHTML = accountForm(vault, screen);
      byId('account-password').value = screen.draft.password;
      byId('account-otp').value = screen.draft.otpInput;
      break;
    case 'detail':
      content.innerHTML = accountDetail(vault, screen);
      break;
    case 'manager':
      content.innerHTML = groupManager(vault);
      break;
    case 'group':
      content.innerHTML = groupForm(vault, screen);
      break;
    case 'confirm-account':
    case 'confirm-group':
      content.innerHTML = confirmDelete(vault, screen);
      break;
    case 'settings':
      content.innerHTML = settings(screen);
      syncSettings(screen.draft);
      break;
  }
  byId('sheet').classList.toggle('drawer', screen.kind === 'detail');
}

export function syncSettings(preferences) {
  const segment = document.querySelector('.appearance-segment');
  if (!segment) return;
  const buttons = [...segment.querySelectorAll('button')];
  buttons.forEach((button) =>
    button.setAttribute('aria-pressed', button.dataset.value === preferences.theme)
  );
  const index = buttons.findIndex((button) => button.dataset.value === preferences.theme);
  segment.querySelector('.segment-thumb').style.transform = `translateX(${index * 100}%)`;
  byId('glass-output').value = `${preferences.glass}%`;
  document.querySelector('[data-action="motion"]').setAttribute('aria-checked', preferences.reduceMotion);
}
