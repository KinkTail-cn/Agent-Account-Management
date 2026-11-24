import {native} from './bridge.js';
import {byId, escape, icon} from './components.js';

let release = {current: '', latest: null, state: 'idle'};
let job = '';
let error = '';

const captions = {
  idle: '检查是否有新版本',
  current: '已是最新版本',
  empty: 'GitHub 尚未发布正式版',
  available: '有新版本可下载',
  ready: '更新已下载并通过校验'
};

export function updatePanel() {
  return `<div id="update-pane"><div class="update-product"><img src="assets/mark.png" alt=""><div><strong>${escape(document.title)}</strong>
    <p id="installed-version"></p></div></div><p class="sheet-desc" id="update-message" role="status"></p>
    <div class="sheet-actions"><button type="button" class="text-button" data-action="update-page">${icon('box-arrow-up-right')}GitHub 发行页</button>
    <button type="button" class="primary" id="update-action"></button></div></div>`;
}

export function paintUpdates() {
  const badge = byId('update-badge');
  const newer = ['available', 'ready'].includes(release.state);
  badge.textContent = newer ? (release.state === 'ready' ? '可重启' : '有新版') : release.current && 'v' + release.current;
  badge.classList.toggle('has-update', newer);
  const pane = byId('update-pane');
  if (!pane) return;
  byId('installed-version').textContent = release.current ? '当前版本 v' + release.current : '正在读取版本…';
  const message = byId('update-message');
  const inProgress = {check: '正在检查 GitHub Releases…', download: '正在下载并校验更新包…', restart: '正在重启应用…'};
  message.textContent = inProgress[job] || error || (captions[release.state] + (newer ? ' · ' + release.latest : ''));
  const button = byId('update-action');
  const action = release.state === 'ready' ? 'restart' : release.state === 'available' ? 'download' : 'check';
  button.dataset.action = 'update-' + action;
  button.textContent = {check: '检查更新', download: '下载更新', restart: '重启并更新'}[action];
  button.disabled = Boolean(job) || !release.current;
}

export async function runUpdate(action, automatic = false) {
  if (action === 'page') return native('updatePage');
  if (job) return;
  job = action;
  error = '';
  paintUpdates();
  try {
    if (action === 'restart') await native('updateRestart');
    if (action === 'download') release = await native('updateDownload');
    if (action === 'check') release = await native('updateCheck', automatic);
  } catch (failure) {
    error = failure.message;
    try { release = await native('updateInfo'); } catch { /* The original failure is more useful here. */ }
  } finally {
    job = '';
    paintUpdates();
  }
}

export async function initializeUpdates() {
  release = await native('updateInfo');
  paintUpdates();
  await runUpdate('check', true);
}
