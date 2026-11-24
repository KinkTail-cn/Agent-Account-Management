import {native} from './bridge.js';
import {icon} from './components.js';

let generation = 0;
let timer;

export function codePanel(account) {
  return `<div class="otp-panel" data-otp-for="${account.id}">
    <div class="otp-main"><div class="otp-label">${icon('shield-lock')}2FA 验证码</div>
      <output class="otp-code" aria-live="off" aria-label="当前验证码">••• •••</output></div>
    <div class="otp-ring" role="timer" aria-live="off" title="距离下次换码"><span>–</span></div>
    <button type="button" class="icon-button otp-copy" data-action="copy" data-field="otp" data-id="${account.id}" aria-label="复制验证码" title="复制验证码" disabled>${icon('copy')}</button></div>`;
}

function draw(panel, windowData, now) {
  const output = panel.querySelector('.otp-code');
  const ring = panel.querySelector('.otp-ring');
  const button = panel.querySelector('.otp-copy');
  if (!windowData || now < windowData.starts || now >= windowData.expires) {
    output.textContent = '••• •••';
    ring.firstElementChild.textContent = '–';
    ring.style.setProperty('--remaining', '0%');
    ring.setAttribute('aria-label', '正在更新验证码');
    button.disabled = true;
    return false;
  }
  const {code, starts, expires} = windowData;
  const split = Math.ceil(code.length / 2);
  const formatted = `${code.slice(0, split)} ${code.slice(split)}`;
  if (formatted !== output.textContent) output.textContent = formatted;
  const seconds = Math.ceil((expires - now) / 1000);
  const percent = ((expires - now) / (expires - starts)) * 100;
  ring.firstElementChild.textContent = String(seconds);
  ring.setAttribute('aria-label', `剩余 ${seconds} 秒`);
  ring.title = `${seconds} 秒后换码`;
  ring.style.setProperty('--remaining', `${percent}%`);
  ring.classList.toggle('expiring', seconds <= 5);
  button.disabled = false;
  return true;
}

// Use absolute expiry times so returning from suspend cannot display an old code.
export function refreshCodes() {
  const run = ++generation;
  clearTimeout(timer);
  if (document.hidden) return;
  const scope = document.querySelector('dialog[open]') || document.getElementById('account-results');
  const panels = [...scope.querySelectorAll('[data-otp-for]')];
  if (!panels.length) return;
  const windows = new Map();
  async function tick() {
    if (generation !== run) return;
    let now = Date.now();
    const missing = panels.filter((p) => !draw(p, windows.get(p.dataset.otpFor), now));
    if (missing.length) {
      try {
        const values = await native(
          'codes',
          missing.map((p) => p.dataset.otpFor)
        );
        if (generation !== run) return;
        now = Date.now();
        for (const p of missing) {
          const next = values[p.dataset.otpFor];
          windows.set(p.dataset.otpFor, next);
          draw(p, next, now);
        }
      } catch {
        if (generation !== run) return;
        for (const p of missing) p.querySelector('.otp-code').textContent = '暂不可用';
      }
    }
    timer = setTimeout(tick, Math.max(25, 1005 - (Date.now() % 1000)));
  }
  tick();
}
