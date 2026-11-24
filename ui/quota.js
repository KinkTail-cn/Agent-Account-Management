import {escape, icon} from './components.js';
import {native} from './bridge.js';

const entries = new Map();
const authLabels = {
  in: ['已登录', 'good'], out: ['未登录', 'muted'], first: ['尚未登录', 'muted'],
  expired: ['需要重新登录', 'warn'], mismatch: ['登录账号不符', 'warn'],
  quotaAuthorized: ['额度已授权', 'good'], web: ['登录状态在网页确认', 'muted'], unknown: ['登录状态待确认', 'muted']
};
const fmt = new Intl.DateTimeFormat('zh-CN', {month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit'});
const percent = n => `${Number(n.toFixed(1))}%`;

export function quotaPanel(account) {
  return `<section class="quota-panel" aria-label="登录状态与模型额度" data-quota-id="${account.id}" data-quota-client="${account.agentClient}"></section>`;
}

function bucket(row, now) {
  const expired = row.reset != null && row.reset <= now;
  const known = Number.isFinite(row.remaining);
  const value = expired ? '待更新' : known ? `剩余 ${percent(row.remaining)}` : '比例未提供';
  const bar = known && !expired ? `<progress max="100" value="${row.remaining}" aria-label="${escape(row.model)} ${escape(row.period)} ${value}"></progress>` : '';
  const reset = row.reset == null ? '重置时间未提供' : `${fmt.format(row.reset)} ${expired ? '已到重置时间' : '重置'}`;
  return `<div class="quota-bucket${!expired && known && row.remaining <= 10 ? ' low' : ''}">
    <div><strong>${escape(row.model)}</strong><span>${escape(row.period)}</span></div>
    <b>${value}</b>${bar}<small>${reset}</small></div>`;
}

function paint(panel, entry) {
  const data = entry?.reply;
  const client = panel.dataset.quotaClient;
  const id = panel.dataset.quotaId;
  const [label, tone] = authLabels[data?.auth] || authLabels.unknown;
  const report = data?.report;
  const rows = report?.windows || [];
  const now = Date.now();
  const stale = report && (data.state !== 'ready' || now - report.observed > 15 * 60_000);
  const exhausted = !stale && rows.some(r => r.remaining === 0 && (!r.reset || r.reset > now));
  const open = panel.querySelector('details')?.open ?? false;
  const focusAction = panel.contains(document.activeElement) ? document.activeElement.dataset.action : null;
  const tools = [];
  const action = (name, text) => `<button type="button" class="text-button" data-action="${name}" data-id="${id}">${text}</button>`;
  if (client.endsWith('-web')) {
    tools.push(action('quota-page', '官方用量页'), action('quota-import', '摘录用量'));
  } else if (client.startsWith('antigravity') && data && ['link', 'login', 'waiting', 'unavailable', 'timeout', 'empty', 'error'].includes(data.state)) {
    tools.push(action('quota-link', '登录此账号'));
  }
  tools.push(`<button type="button" class="text-button" data-action="quota-refresh" data-id="${id}" ${entry?.busy ? 'disabled' : ''}>${icon('arrow-repeat')}${entry?.busy ? '查询中' : '刷新'}</button>`);
  const first = rows.find(r => r.remaining != null && (!r.reset || r.reset > now));
  const summary = !rows.length ? '模型额度' : first ? `${escape(first.model)} · ${escape(first.period)}剩余 ${percent(first.remaining)}` : '模型额度待更新';
  const details = rows.length ? `<details class="quota-details"${open ? ' open' : ''}><summary>${icon('chevron-down')}${summary}${rows.length > 1 ? ` <small>共 ${rows.length} 项</small>` : ''}</summary>
    <div class="quota-grid">${rows.map(r => bucket(r, now)).join('')}</div>
    <p class="quota-source">${escape(report.source)} · ${fmt.format(report.observed)} ${stale ? '上次记录' : '记录'}</p></details>` : `<strong class="quota-heading">模型额度</strong>`;
  panel.innerHTML = `<div class="quota-toolbar"><span class="auth-chip ${tone}">${entry?.busy && !data ? '正在检测状态' : label}</span>
    ${exhausted ? '<span class="auth-chip warn">有额度已用完</span>' : ''}${stale ? '<small class="quota-old">上次记录</small>' : ''}<div class="quota-actions">${tools.join('')}</div></div>${details}
    ${data?.message ? `<p class="quota-note">${escape(data.message)}</p>` : ''}`;
  if (focusAction) panel.querySelector(`[data-action="${CSS.escape(focusAction)}"]:not(:disabled)`)?.focus({preventScroll: true});
}

function visiblePanels() {
  const dialog = document.querySelector('#sheet[open]:not(.closing)');
  if (dialog) return [...dialog.querySelectorAll('[data-quota-id]')];
  const section = document.getElementById('agent-section');
  return section && !section.hidden ? [...section.querySelectorAll('[data-quota-id]')] : [];
}

function relevant(panel) {
  const box = panel.getBoundingClientRect();
  return !document.hidden && box.height > 0 && box.top < innerHeight + 120 && box.bottom > -120;
}

const keyOf = panel => panel.dataset.quotaId + '/' + panel.dataset.quotaClient;
const matches = key => visiblePanels().filter(panel => keyOf(panel) === key);

async function request(panel, manual = false) {
  const key = keyOf(panel);
  const entry = entries.get(key) || {};
  if (entry.busy || (!manual && Date.now() - (entry.checked || 0) < (entry.retry || 180000))) return;
  entries.set(key, entry);
  entry.busy = true;
  matches(key).forEach(p => paint(p, entry));
  try {
    entry.reply = await native('agentQuota', {id: panel.dataset.quotaId, client: panel.dataset.quotaClient, refresh: manual});
  } catch {
    entry.reply = {state: 'error', auth: 'unknown', message: '查询失败，请稍后刷新。', report: entry.reply?.report};
  } finally {
    entry.busy = false;
    entry.checked = Date.now();
    entry.retry = entry.reply.state === 'ready' ? 180000 : 60000;
    if (entries.get(key) === entry) matches(key).forEach(p => paint(p, entry));
  }
}

let observer;
export function syncQuotaPanels() {
  observer?.disconnect();
  observer = new IntersectionObserver(items => {
    for (const item of items) if (item.isIntersecting && relevant(item.target)) request(item.target);
  }, {rootMargin: '120px'});
  for (const panel of visiblePanels()) {
    paint(panel, entries.get(keyOf(panel)));
    observer.observe(panel);
  }
}

export function invalidateQuota(id) {
  for (const key of entries.keys()) if (key.startsWith(id + '/')) entries.delete(key);
}

export async function refreshQuota(id) {
  const panel = visiblePanels().find(p => p.dataset.quotaId === id);
  if (panel) await request(panel, true);
}

setInterval(() => {
  for (const panel of visiblePanels().filter(relevant)) {
    const entry = entries.get(keyOf(panel));
    if (!entry?.busy && !panel.contains(document.activeElement)) paint(panel, entry);
    request(panel);
  }
}, 30000);
