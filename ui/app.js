import {native} from './bridge.js';
import {initializeUpdates, runUpdate} from './updates.js';
import {byId, escape, icon} from './components.js';
import {renderLibrary} from './library.js';
import {paintScreen, readAccountForm, syncSettings} from './screens.js';
import {refreshCodes} from './codes.js';
import {syncQuotaPanels, invalidateQuota, refreshQuota} from './quota.js';
import {defaultClient, detectClients, getClient, hasProjectFolder, selectProvider, setFolder, updateClientChoice} from './agents.js';

const dialog = byId('sheet');
const search = byId('search');
const systemDark = matchMedia('(prefers-color-scheme: dark)');
const systemReduced = matchMedia('(prefers-reduced-motion: reduce)');
const narrow = matchMedia('(max-width: 820px)');
let vault;
let active = 'all';
let saving = false;
let screens = [];
let opener;
let openerScope;
let toastTimer;
let closeTimer;
let passwordTimer;
let googleTimer;
let lastNativeTheme;

const current = () => screens.at(-1);
const findAccount = (id) => vault.accounts.find((a) => a.id === id);
const findGroup = (id) => vault.groups.find((g) => g.id === id);
const reduced = () => systemReduced.matches || document.documentElement.dataset.reduce === 'true';

function applyAppearance(p) {
  const theme = p.theme === 'system' ? (systemDark.matches ? 'dark' : 'light') : p.theme;
  const root = document.documentElement;
  root.dataset.theme = theme;
  root.dataset.reduce = String(p.reduceMotion);
  root.style.setProperty('--glass', String(0.42 + p.glass * 0.0058));
  root.style.setProperty('--glass-heavy', String(0.78 + p.glass * 0.0022));
  if (lastNativeTheme !== theme) {
    lastNativeTheme = theme;
    native('theme', theme).catch(() => {});
  }
}

function render() {
  renderLibrary(vault, active, search.value);
  refreshCodes();
  syncQuotaPanels();
}

function notice(message, error = false) {
  clearTimeout(toastTimer);
  const toast = byId('toast');
  (dialog.open && !dialog.classList.contains('closing') ? dialog : document.body).append(toast);
  toast.dataset.error = String(error);
  toast.innerHTML =
    icon(error ? 'exclamation-circle' : 'check-circle-fill') + `<span>${escape(message)}</span>`;
  toast.hidden = false;
  toast.classList.remove('showing');
  requestAnimationFrame(() => toast.classList.add('showing'));
  toastTimer = setTimeout(() => {
    toast.hidden = true;
  }, 2300);
}

function report(message) {
  const field = dialog.open && dialog.querySelector('.form-error');
  if (field) {
    field.textContent = message;
    field.scrollIntoView({block: 'nearest'});
  } else notice(message, true);
}

// Only acknowledge a change after the native host has committed the encrypted file.
async function commit(change, after) {
  if (saving) return;
  saving = true;
  document.body.setAttribute('aria-busy', 'true');
  const submit = dialog.querySelector('button[type=submit]');
  if (submit) submit.disabled = true;
  try {
    const next = structuredClone(vault);
    await change(next);
    next.revision = await native('save', next);
    vault = next;
    if (!['all', 'favorites', 'ungrouped', 'codes', 'agents'].includes(active) && !findGroup(active))
      active = 'ungrouped';
    render();
    after?.();
  } catch (error) {
    report(error.message);
  } finally {
    saving = false;
    document.body.removeAttribute('aria-busy');
    if (submit?.isConnected) submit.disabled = false;
  }
}

function showNav(open) {
  byId('app').classList.toggle('nav-open', open);
  document.querySelector('.nav-toggle').setAttribute('aria-expanded', String(open));
  if (narrow.matches) document.querySelector('.stage').inert = open;
  else document.querySelector('.stage').inert = false;
  if (open) document.querySelector('.nav-item.selected, .nav-item')?.focus();
}

function closeSort(returnFocus = false) {
  if (byId('sort-menu').hidden) return;
  byId('sort-menu').hidden = true;
  byId('sort-button').setAttribute('aria-expanded', 'false');
  if (returnFocus) byId('sort-button').focus();
}

function navigate(id) {
  active = id;
  search.value = '';
  closeSort();
  showNav(false);
  render();
  byId('main').scrollTop = 0;
  const content = document.querySelector('.page-content');
  content.classList.remove('page-enter');
  void content.offsetWidth;
  content.classList.add('page-enter');
  if (narrow.matches) byId('main').focus({preventScroll: true});
  else document.querySelector(`.nav-item[data-id="${CSS.escape(id)}"]`)?.focus({preventScroll: true});
}

function paintCurrent(focus = true) {
  clearTimeout(passwordTimer);
  clearTimeout(googleTimer);
  paintScreen(vault, current());
  if (current().kind === 'google-login') watchGoogle(current());
  dialog.scrollTop = 0;
  refreshCodes();
  syncQuotaPanels();
  if (focus)
    (dialog.querySelector('[autofocus]') || dialog.querySelector('button'))?.focus({preventScroll: true});
}

function open(screen) {
  if (!dialog.open) {
    opener = document.activeElement;
    openerScope = opener?.parentElement?.closest('[id]');
  }
  closeSort();
  showNav(false);
  clearTimeout(closeTimer);
  dialog.classList.remove('closing');
  screens.push(screen);
  paintCurrent(false);
  if (!dialog.open) dialog.showModal();
  (dialog.querySelector('[autofocus]') || dialog.querySelector('button'))?.focus({preventScroll: true});
  refreshCodes();
  syncQuotaPanels();
}

function finishClose() {
  clearTimeout(closeTimer);
  clearTimeout(passwordTimer);
  clearTimeout(googleTimer);
  dialog.classList.remove('closing', 'drawer');
  if (dialog.contains(byId('toast'))) document.body.append(byId('toast'));
  dialog.close();
  byId('sheet-content').replaceChildren();
  screens = [];
  if (vault) applyAppearance(vault.preferences);
  refreshCodes();
  syncQuotaPanels();
  let target = opener;
  if (!target?.isConnected && opener?.dataset?.action) {
    const selector =
      `[data-action="${CSS.escape(opener.dataset.action)}"]` +
      (opener.dataset.id ? `[data-id="${CSS.escape(opener.dataset.id)}"]` : '');
    target = openerScope?.querySelector(selector);
  }
  if (narrow.matches && target?.closest('#sidebar')) target = document.querySelector('.nav-toggle');
  (target?.isConnected ? target : byId('main')).focus({preventScroll: true});
}

function closeSheet(immediate = false) {
  if (!dialog.open || dialog.classList.contains('closing')) return;
  if (immediate || reduced()) return finishClose();
  dialog.classList.add('closing');
  closeTimer = setTimeout(finishClose, 160);
}

function back() {
  if (screens.length <= 1) return closeSheet();
  screens.pop();
  paintCurrent();
}

function newAccount(id, agent = active === 'agents') {
  const record = id && findAccount(id);
  const draft = record
    ? {...record}
    : {
        name: '',
        login: '',
        password: '',
        website: '',
        note: '',
        groupId: findGroup(active)?.id || null,
        agentClient: agent ? defaultClient() : null,
        agentFolder: null,
        favorite: active === 'favorites',
        otp: null
      };
  open({kind: 'account', id, requireClient: !id && agent, draft: {...draft, otpInput: '', removeOtp: false}});
}

function openGroupEditor(id) {
  if (current()?.kind === 'account') Object.assign(current().draft, readAccountForm());
  const draft = id ? {...findGroup(id)} : {name: '', color: 'indigo', icon: 'folder2'};
  open({kind: 'group', id, draft});
}

function validField(id, message) {
  const field = byId(id);
  field.setCustomValidity(message);
  field.reportValidity();
  field.focus();
}

function websiteAddress(raw) {
  const text = raw.trim();
  if (!text) return '';
  const withScheme = /^[a-z][a-z\d+.\-]*:/i.test(text);
  const url = new URL(withScheme ? text : `https://${text}`);
  if (!['http:', 'https:'].includes(url.protocol) || !url.hostname || url.href.length > 2048) throw Error();
  return url.href;
}

async function saveAccount() {
  const screen = current();
  const fields = readAccountForm();
  fields.name = fields.name.trim();
  fields.login = fields.login.trim();
  if (!fields.name) return validField('account-name', '请输入账号名称。');
  if (!fields.login) return validField('account-login', '请输入登录账号。');
  if (screen.requireClient && !fields.agentClient) {
    dialog.querySelector('.client-picker').open = true;
    return validField('agent-client', '请为切换账号选择客户端。');
  }
  if (!hasProjectFolder(fields.agentClient)) fields.agentFolder = null;
  try {
    fields.website = websiteAddress(fields.website);
  } catch {
    return validField('account-website', '请输入有效的 HTTP 或 HTTPS 网站地址。');
  }
  const targetId = screen.id || crypto.randomUUID();
  await commit(
    async (next) => {
      let otp = screen.draft.removeOtp ? null : screen.draft.otp;
      const typed = fields.otpInput.trim();
      if (typed) {
        try {
          otp = await native('parseOtp', typed);
        } catch (error) {
          validField('account-otp', error.message);
          throw error;
        }
      }
      const {otpInput, ...values} = fields;
      const previous = next.accounts.find((a) => a.id === screen.id);
      const now = Math.max(Date.now(), previous?.created ?? 0, previous?.updated ?? 0);
      const account = {
        id: targetId,
        created: previous?.created ?? now,
        updated: now,
        ...values,
        agentLastOpened: previous?.agentClient === values.agentClient ? (previous?.agentLastOpened || 0) : 0,
        otp: otp || null
      };
      if (previous) next.accounts[next.accounts.indexOf(previous)] = account;
      else next.accounts.push(account);
      if (previous?.login !== account.login || previous?.agentClient !== account.agentClient) invalidateQuota(targetId);
    },
    () => {
      if (active === 'agents' && fields.agentClient) {
        selectProvider(getClient(fields.agentClient).provider);
        render();
      }
      if (!screen.id) {
        search.value = '';
        if (active === 'codes' && !findAccount(targetId).otp) active = 'all';
        if (active === 'favorites' && !fields.favorite) active = 'all';
        if (findGroup(active) && active !== fields.groupId) active = fields.groupId || 'ungrouped';
        if (active === 'ungrouped' && fields.groupId) active = fields.groupId;
        render();
      }
      if (screens.length > 1 && screens.at(-2).kind === 'detail') back();
      else {
        screens = [{kind: 'detail', id: targetId}];
        paintCurrent();
      }
      notice('账号已保存');
    }
  );
}

async function saveGroup() {
  const screen = current();
  const name = byId('group-name').value.trim();
  if (!name) return validField('group-name', '请输入分组名称。');
  if (
    vault.groups.some((g) => g.id !== screen.id && g.name.toLocaleLowerCase() === name.toLocaleLowerCase())
  ) {
    return validField('group-name', '已有同名分组，请换一个名称。');
  }
  const id = screen.id || crypto.randomUUID();
  const members = new Set(new FormData(byId('group-form')).getAll('member'));
  await commit(
    (next) => {
      const group = {id, name, color: screen.draft.color, icon: screen.draft.icon};
      const index = next.groups.findIndex((g) => g.id === id);
      if (index === -1) next.groups.push(group);
      else next.groups[index] = group;
      const now = Date.now();
      for (const account of next.accounts) {
        const belongs = account.groupId === id;
        if (members.has(account.id) === belongs) continue;
        account.groupId = belongs ? null : id;
        account.updated = Math.max(now, account.updated);
      }
    },
    () => {
      const parent = screens.at(-2);
      if (parent?.kind === 'account') parent.draft.groupId = id;
      back();
      notice(screen.id ? '分组已更新' : '分组已创建');
    }
  );
}

async function deleteItem() {
  const screen = current();
  const group = screen.kind === 'confirm-group';
  await commit(
    (next) => {
      if (group) {
        next.groups = next.groups.filter((g) => g.id !== screen.id);
        for (const account of next.accounts) {
          if (account.groupId === screen.id) {
            account.groupId = null;
            account.updated = Math.max(account.updated, Date.now());
          }
        }
      } else {
        next.accounts = next.accounts.filter((a) => a.id !== screen.id);
        invalidateQuota(screen.id);
      }
    },
    () => {
      if (group && screens.at(-2)?.kind === 'manager') back();
      else closeSheet();
      notice(group ? '分组已删除' : '账号已删除');
    }
  );
}

function togglePassword(input, button, reveal) {
  if (!input || !button) return;
  clearTimeout(passwordTimer);
  if (input.tagName === 'INPUT') input.type = reveal ? 'text' : 'password';
  else input.textContent = reveal ? findAccount(current().id).password : '••••••••';
  button.setAttribute('aria-label', reveal ? '隐藏密码' : '显示密码');
  button.title = button.getAttribute('aria-label');
  button.setAttribute('aria-pressed', reveal);
  button.innerHTML = icon(reveal ? 'eye-slash' : 'eye');
  if (reveal) passwordTimer = setTimeout(hideSecrets, 20000);
}

function hideSecrets() {
  togglePassword(byId('account-password'), dialog.querySelector('[data-action="form-password"]'), false);
  togglePassword(byId('detail-password'), dialog.querySelector('[data-action="detail-password"]'), false);
}

function setSearch(value) {
  search.value = value;
  render();
  search.focus();
  byId('main').scrollTop = 0;
}

function sortMenu() {
  const menu = byId('sort-menu');
  if (!menu.hidden) return closeSort(true);
  menu.innerHTML = [
    ['recent', '最近更新'],
    ['name', '按名称']
  ]
    .map(
      ([mode, label]) =>
        `<button type="button" data-action="sort" data-value="${mode}" aria-pressed="${vault.preferences.sort === mode}">${label}${vault.preferences.sort === mode ? icon('check-lg') : ''}</button>`
    )
    .join('');
  menu.hidden = false;
  byId('sort-button').setAttribute('aria-expanded', 'true');
  menu.querySelector('[aria-pressed=true]').focus();
}

async function handleAction(button) {
  const {action, id, value, field} = button.dataset;
  switch (action) {
    case 'updates': open({kind: 'updates'}); break;
    case 'update-check':
    case 'update-download':
    case 'update-restart':
    case 'update-page':
      await runUpdate(action.slice(7));
      break;
    case 'quota-refresh': await refreshQuota(id); break;
    case 'quota-link':
    case 'google-relogin':
      await beginGoogle(id);
      break;
    case 'google-retry':
    case 'google-cancel':
    case 'google-browser': {
      const screen = current();
      if (screen?.kind !== 'google-login' || !screen.attempt) break;
      button.disabled = true;
      try {
        const payload = {id: screen.id, attempt: screen.attempt};
        if (action === 'google-retry') await native('googleLoginRetry', payload);
        else await native('googleLoginAction', {...payload, action: action === 'google-cancel' ? 'cancel' : 'browser', code: ''});
        if (current() === screen) watchGoogle(screen);
      } finally { if (button.isConnected) button.disabled = false; }
      break;
    }
    case 'quota-page': await native('openQuotaPage', id); break;
    case 'quota-import': open({kind: 'quota-import', id}); break;
    case 'detect-agents':
      await detectClients();
      render();
      notice('已更新本机客户端');
      break;
    case 'agent-provider':
      selectProvider(value);
      render();
      byId('agent-section').querySelector(`[data-value="${value}"]`).focus();
      break;
    case 'bind-agent':
      open({kind: 'bind-agent'});
      break;
    case 'new-agent':
      if (current()?.kind === 'bind-agent') finishClose();
      newAccount(null, true);
      break;
    case 'choose-folder': {
      const folder = await native('agentFolder');
      if (folder) setFolder(folder);
      break;
    }
    case 'clear-folder':
      setFolder(null);
      break;
    case 'agent-login':
    case 'launch-agent': {
      const account = findAccount(id);
      if (account.agentClient?.startsWith('antigravity')) {
        const status = await native('googleLoginState', id);
        if (!status.hasActive || ['starting', 'authorizing', 'verifying'].includes(status.phase)) { await beginGoogle(id); break; }
      }
      let focused;
      await commit(async next => {
        focused = await native('launchAgent', {id, login: action === 'agent-login'});
        next.accounts.find(a => a.id === id).agentLastOpened = Date.now();
      }, () => {
        if (current()?.kind === 'google-login') closeSheet();
        notice(focused ? '已切换到账号窗口' : '已打开 ' + getClient(findAccount(id).agentClient).label);
      });
      break;
    }
    case 'reload':
      location.reload();
      break;
    case 'nav':
      showNav(true);
      break;
    case 'close-nav':
      showNav(false);
      document.querySelector('.nav-toggle').focus();
      break;
    case 'navigate':
      navigate(id);
      break;
    case 'all':
      navigate('all');
      break;
    case 'clear-search':
      setSearch('');
      break;
    case 'add':
      newAccount();
      break;
    case 'detail':
      open({kind: 'detail', id});
      break;
    case 'edit-account':
      newAccount(id);
      break;
    case 'delete-account':
      open({kind: 'confirm-account', id});
      break;
    case 'manage-groups':
      open({kind: 'manager'});
      break;
    case 'new-group':
      openGroupEditor();
      break;
    case 'edit-group':
      openGroupEditor(id);
      break;
    case 'delete-group':
      open({kind: 'confirm-group', id});
      break;
    case 'confirm-delete':
      await deleteItem();
      break;
    case 'back':
      back();
      break;
    case 'favorite':
      await commit(
        (next) => {
          const a = next.accounts.find((record) => record.id === id);
          a.favorite = !a.favorite;
          a.updated = Math.max(Date.now(), a.updated);
        },
        () => {
          if (current()?.kind === 'detail') paintCurrent(false);
          const scope = dialog.open ? dialog : byId('account-results');
          (scope.querySelector(`[data-action="favorite"][data-id="${id}"]`) || byId('main')).focus({
            preventScroll: true
          });
        }
      );
      break;
    case 'form-password':
    case 'detail-password':
      togglePassword(
        byId(action === 'form-password' ? 'account-password' : 'detail-password'),
        button,
        button.getAttribute('aria-pressed') !== 'true'
      );
      break;
    case 'remove-otp':
      current().draft.removeOtp = !current().draft.removeOtp;
      Object.assign(current().draft, readAccountForm(), {otpInput: ''});
      paintCurrent(false);
      byId('account-otp').focus();
      break;
    case 'copy':
      await native('copy', {id, field});
      notice(field === 'otp' ? '验证码已复制' : field === 'password' ? '密码已复制' : '已复制');
      break;
    case 'open-website':
      await native('openWebsite', id);
      break;
    case 'sort-menu':
      sortMenu();
      break;
    case 'sort':
    case 'view':
      closeSort(action === 'sort');
      if (vault.preferences[action] !== value)
        await commit((next) => {
          next.preferences[action] = value;
        });
      break;
    case 'group-color':
    case 'group-icon': {
      const draft = current().draft;
      const previous = dialog.querySelector(`[data-action="${action}"][aria-pressed="true"]`);
      previous.setAttribute('aria-pressed', 'false');
      button.setAttribute('aria-pressed', 'true');
      const preview = dialog.querySelector('.group-preview');
      if (action === 'group-color') {
        preview.classList.replace('color-' + draft.color, 'color-' + value);
        draft.color = value;
        previous.replaceChildren();
        button.innerHTML = icon('check-lg');
      } else {
        draft.icon = value;
        preview.querySelector('.group-icon').innerHTML = icon(value);
      }
      break;
    }
    case 'settings':
      open({kind: 'settings', draft: {...vault.preferences}});
      break;
    case 'theme':
    case 'motion': {
      const draft = current().draft;
      if (action === 'theme') draft.theme = value;
      else draft.reduceMotion = !draft.reduceMotion;
      syncSettings(draft);
      applyAppearance(draft);
      break;
    }
  }
}

async function beginGoogle(id) {
  await native('googleLoginBegin', id);
  if (current()?.kind === 'google-login' && current().id === id) watchGoogle(current());
  else open({kind: 'google-login', id});
}

async function watchGoogle(screen) {
  if (!screen || screen !== current() || screen.kind !== 'google-login' || screen.sending) return;
  clearTimeout(googleTimer);
  const update = screen.update = (screen.update || 0) + 1;
  try {
    const state = await native('googleLoginState', screen.id);
    if (screen !== current() || screen.update !== update) return;
    paintGoogle(screen, state);
  } catch (error) {
    if (screen === current() && screen.update === update) report(error.message);
  }
}

function paintGoogle(screen, state) {
  const status = byId('google-login-status');
  if (!status) return;
  const live = ['starting', 'authorizing', 'verifying'].includes(state.phase);
  const messages = {
    starting: '正在生成此账号专属的 Google 授权请求…',
    authorizing: `请在 Google 窗口选择 ${findAccount(screen.id).login} 并授权，再粘贴页面显示的授权码。`,
    verifying: '授权码已送达 Antigravity，正在等待验证结果…',
    ready: 'Google 已完成授权，可以打开这个账号的客户端。',
    cancelled: '本次授权已取消。'
  };
  const message = messages[state.phase] || state.message || '请重新获取授权码。';
  if (screen.attempt !== state.attemptId || (screen.phase !== state.phase && state.phase !== 'authorizing')) {
    const error = dialog.querySelector('.form-error');
    if (error) error.textContent = '';
  }
  if (status.textContent !== message) status.textContent = message;
  screen.attempt = state.attemptId;
  screen.phase = state.phase;
  const remaining = state.expiresAt ? Math.max(0, Math.ceil((state.expiresAt - Date.now()) / 1000)) : null;
  const countdown = byId('google-deadline');
  countdown.hidden = remaining === null;
  if (remaining !== null) countdown.textContent = remaining ? `本次请求还可提交 ${remaining} 秒` : '本次请求已到期';
  if (state.phase !== 'authorizing') byId('google-code').value = '';
  byId('google-code-box').hidden = state.phase !== 'authorizing';
  byId('google-code').readOnly = screen.sending || remaining === 0;
  byId('google-submit').disabled = !!screen.sending || remaining === 0 || state.phase !== 'authorizing';
  byId('google-cancel').hidden = !live;
  byId('google-retry').hidden = live || state.phase === 'ready';
  byId('google-open').hidden = state.phase !== 'ready';
  if (state.phase === 'ready') invalidateQuota(screen.id);
  clearTimeout(googleTimer);
  if (live) googleTimer = setTimeout(() => watchGoogle(screen), 650);
}

async function submitGoogleCode() {
  const screen = current();
  if (screen?.kind !== 'google-login' || screen.phase !== 'authorizing' || screen.sending || !screen.attempt) return;
  const input = byId('google-code');
  const code = input.value.trim();
  if (!/^[A-Za-z0-9._~+/=-]{1,4096}$/.test(code)) return validField('google-code', '请粘贴 Google 页面显示的完整授权码。');
  screen.sending = true;
  screen.update++;
  clearTimeout(googleTimer);
  input.readOnly = true;
  byId('google-submit').disabled = true;
  dialog.querySelector('.form-error').textContent = '';
  byId('google-login-status').textContent = '正在提交授权码…';
  try {
    const state = await native('googleLoginAction', {id: screen.id, attempt: screen.attempt, action: 'code', code});
    if (current() !== screen) return;
    screen.sending = false;
    paintGoogle(screen, state);
  } catch (error) {
    if (current() === screen) {
      screen.sending = false;
      report(error.message);
      watchGoogle(screen);
    }
  } finally { screen.sending = false; }
}

document.addEventListener('click', (event) => {
  if (!event.target.closest('.sort-wrap')) closeSort();
  const button = event.target.closest('button[data-action]');
  if (button && !saving && !dialog.classList.contains('closing'))
    handleAction(button).catch((error) => report(error.message));
});

document.addEventListener('submit', (event) => {
  event.preventDefault();
  if (saving) return;
  if (event.target.id === 'google-code-form') {
    submitGoogleCode();
    return;
  }
  if (event.target.id === 'quota-import-form') {
    const id = current().id;
    native('importWebQuota', {id, text: byId('quota-text').value}).then(() => {
      invalidateQuota(id);
      if (current()?.kind === 'quota-import' && current().id === id) back();
      syncQuotaPanels();
      notice('网页用量已保存');
    }).catch(error => report(error.message));
    return;
  }
  if (event.target.id === 'account-form') saveAccount();
  if (event.target.id === 'group-form') saveGroup();
  if (event.target.id === 'bind-agent-form') {
    const client = byId('agent-client').value;
    if (!client) return validField('agent-client', '请选择要绑定的客户端。');
    const id = byId('bind-account').value;
    const folder = hasProjectFolder(client) ? byId('agent-folder').value || null : null;
    commit(next => {
      const account = next.accounts.find(a => a.id === id);
      Object.assign(account, {agentClient: client, agentFolder: folder, agentLastOpened: 0});
    }, () => { selectProvider(getClient(client).provider); render(); closeSheet(); notice('已绑定客户端'); });
  }
  if (event.target.id === 'settings-form') {
    commit(
      (next) => {
        next.preferences = {...current().draft};
      },
      () => {
        closeSheet();
        notice('外观已保存');
      }
    );
  }
});

dialog.addEventListener('input', (event) => {
  event.target.setCustomValidity?.('');
  const error = dialog.querySelector('.form-error');
  if (error) error.textContent = '';
  if (event.target.id === 'group-name')
    byId('group-preview-name').textContent = event.target.value.trim() || '新分组';
  if (event.target.id === 'agent-client') updateClientChoice();
  if (event.target.name === 'member') {
    const selected = dialog.querySelectorAll('input[name="member"]:checked').length;
    byId('member-count').value = `已选 ${selected} 个`;
  }
  if (event.target.id === 'member-search') {
    const query = event.target.value.trim().normalize('NFKC').toLocaleLowerCase();
    let found = false;
    for (const row of dialog.querySelectorAll('.member-row')) {
      const label = row.querySelector('.member-identity').textContent.normalize('NFKC').toLocaleLowerCase();
      row.hidden = !label.includes(query);
      if (!row.hidden) found = true;
    }
    byId('member-empty').hidden = found;
  }
  if (event.target.id === 'glass-range') {
    current().draft.glass = Number(event.target.value);
    syncSettings(current().draft);
    applyAppearance(current().draft);
  }
});

search.addEventListener('input', () => {
  render();
  byId('main').scrollTop = 0;
});
dialog.addEventListener('cancel', (event) => {
  event.preventDefault();
  if (!saving) back();
});
let pressedBackdrop = false;
function outsideSheet(event) {
  if (event.target !== dialog) return false;
  const bounds = dialog.getBoundingClientRect();
  return (
    event.clientX < bounds.left ||
    event.clientX > bounds.right ||
    event.clientY < bounds.top ||
    event.clientY > bounds.bottom
  );
}
dialog.addEventListener('pointerdown', (event) => {
  pressedBackdrop = outsideSheet(event);
});
dialog.addEventListener('click', (event) => {
  if (pressedBackdrop && outsideSheet(event) && !saving) back();
  pressedBackdrop = false;
});

document.addEventListener('keydown', (event) => {
  if (!vault || event.isComposing || saving) return;
  if (dialog.open) {
    if (dialog.querySelector('form') && event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      dialog.querySelector('form').requestSubmit();
    } else if (event.target.id === 'member-search' && event.key === 'Enter') event.preventDefault();
    return;
  }
  if (event.ctrlKey || event.metaKey) {
    const key = event.key.toLowerCase();
    if (!['k', 'f', 'n'].includes(key)) return;
    event.preventDefault();
    showNav(false);
    if (key === 'n') newAccount();
    else {
      search.focus();
      search.select();
    }
  } else if (event.key === 'Escape') {
    if (!byId('sort-menu').hidden) closeSort(true);
    else if (byId('app').classList.contains('nav-open')) {
      showNav(false);
      document.querySelector('.nav-toggle').focus();
    } else if (search.value) setSearch('');
  }
});

const hero = byId('hero');
const heroArt = hero.querySelector('.hero-art');
hero.addEventListener('pointermove', (event) => {
  if (event.pointerType !== 'mouse' || reduced()) return;
  const rect = hero.getBoundingClientRect();
  heroArt.style.setProperty('--art-x', `${((event.clientX - rect.left) / rect.width - 0.5) * 9}px`);
  heroArt.style.setProperty('--art-y', `${((event.clientY - rect.top) / rect.height - 0.5) * 6}px`);
});
hero.addEventListener('pointerleave', () => {
  heroArt.style.removeProperty('--art-x');
  heroArt.style.removeProperty('--art-y');
});
window.addEventListener('vault-inactive', hideSecrets);
window.addEventListener('blur', hideSecrets);
document.addEventListener('visibilitychange', () => {
  if (document.hidden) hideSecrets();
  refreshCodes();
});
narrow.addEventListener('change', () => showNav(false));
systemDark.addEventListener('change', () => {
  if (vault) applyAppearance(current()?.kind === 'settings' ? current().draft : vault.preferences);
});

async function start() {
  try {
    [vault] = await Promise.all([native('load'), detectClients()]);
    applyAppearance(vault.preferences);
    render();
    byId('app').inert = false;
    byId('startup').hidden = true;
    initializeUpdates().catch(() => {});
  } catch (error) {
    const startup = byId('startup');
    startup.querySelector('p').textContent = error.message;
    startup.insertAdjacentHTML(
      'beforeend',
      '<button class="secondary" type="button" data-action="reload">重新打开</button>'
    );
  }
}
start();
