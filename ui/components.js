export const byId = (id) => document.getElementById(id);

export const colors = {indigo: '紫藤', peach: '杏橙', mint: '薄荷', sky: '晴蓝', rose: '玫瑰', amber: '琥珀'};
export const groupIcons = {
  briefcase: '公文包',
  person: '个人',
  leaf: '叶子',
  controller: '游戏',
  book: '书本',
  wallet2: '钱包',
  heart: '爱心',
  folder2: '文件夹'
};
export const collections = {
  all: {name: '全部账号', icon: 'collection'},
  agents: {name: 'Agent 切换', icon: 'window-stack'},
  codes: {name: '2FA 验证码', icon: 'shield-lock'},
  favorites: {name: '我的收藏', icon: 'star'},
  ungrouped: {name: '未分组', icon: 'inbox'}
};

const entities = {'&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'};
export const escape = (value) => String(value ?? '').replace(/[&<>"']/g, (ch) => entities[ch]);
export const icon = (name) => `<i class="i i-${name}" aria-hidden="true"></i>`;
export const recordStates = {normal: '正常', issue: '异常', disabled: '停用'};
export function recordBadge(account, fallback = false) {
  const name = recordStates[account.recordStatus];
  if (!name && !fallback) return '';
  return `<span class="record-badge ${name ? account.recordStatus : 'unmarked'}" title="手动标记的账号状态">${name || '未标记'}</span>`;
}

export function iconButton(action, glyph, label, id = '', extraClass = '') {
  return `<button type="button" class="icon-button ${extraClass}" data-action="${action}" data-id="${escape(id)}" aria-label="${escape(label)}" title="${escape(label)}">${icon(glyph)}</button>`;
}

export function groupChip(group) {
  if (!group) return `<span class="chip">${icon('inbox')}<span>未分组</span></span>`;
  return `<span class="chip color-${group.color}">${icon(group.icon)}<span>${escape(group.name)}</span></span>`;
}

export function accountIcon(account) {
  let sum = 0;
  for (const char of account.name) sum = (sum * 31 + char.codePointAt(0)) >>> 0;
  const color = Object.keys(colors)[sum % Object.keys(colors).length];
  const initial = [...account.name.trim()][0]?.toLocaleUpperCase('zh-CN') || '·';
  return `<span class="account-icon color-${color}" aria-hidden="true">${escape(initial)}</span>`;
}

export function favoriteButton(account) {
  const label = account.favorite ? '取消收藏' : '加入收藏';
  const active = account.favorite ? ' is-on' : '';
  return `<button type="button" class="icon-button favorite-button${active}" data-action="favorite" data-id="${account.id}" aria-label="${label}" aria-pressed="${account.favorite}" title="${label}">${icon(account.favorite ? 'star-fill' : 'star')}</button>`;
}

export function dateLabel(timestamp, full = false) {
  const date = new Date(timestamp);
  if (full) return new Intl.DateTimeFormat('zh-CN', {dateStyle: 'medium', timeStyle: 'short'}).format(date);
  const today = new Date();
  if (date.toDateString() === today.toDateString()) return '今天';
  today.setDate(today.getDate() - 1);
  if (date.toDateString() === today.toDateString()) return '昨天';
  return date.toLocaleDateString('zh-CN', {
    month: 'numeric',
    day: 'numeric',
    ...(date.getFullYear() !== new Date().getFullYear() ? {year: 'numeric'} : {})
  });
}
