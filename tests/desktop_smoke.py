"""Exercise the built exe through its real WebView2 window, with an isolated vault."""
import argparse
import base64
from contextlib import contextmanager
import ctypes
import hashlib
import hmac
import os
from pathlib import Path
import re
import socket
import struct
import subprocess
import tempfile
import time
from urllib.request import build_opener, ProxyHandler

from playwright.sync_api import expect, sync_playwright

ROOT = Path(__file__).resolve().parents[1]
EXE = ROOT / 'agent账号切换器.exe'
KEY = 'JBSWY3DPEHPK3PXP'
ADDRESS = 'studio@example.invalid'
PASSWORD = 'Only-in-the-isolated-test!'
HTTP = build_opener(ProxyHandler({}))


def close_window(process):
    if process.poll() is not None:
        return

    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    def close(hwnd, _):
        pid = ctypes.c_ulong()
        ctypes.windll.user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value == process.pid:
            ctypes.windll.user32.PostMessageW(hwnd, 0x10, 0, 0)
        return True

    ctypes.windll.user32.EnumWindows(close, 0)
    try:
        process.wait(7)
    except subprocess.TimeoutExpired:
        process.terminate()
        process.wait(5)


@contextmanager
def application(playwright, profile, screenshots):
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        port = sock.getsockname()[1]
    endpoint = f'http://127.0.0.1:{port}'
    env = dict(os.environ, LOCALAPPDATA=str(profile), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=f'--remote-debugging-port={port}')
    process = subprocess.Popen([str(EXE)], env=env, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    page = None
    try:
        for _ in range(120):
            if process.poll() is not None:
                raise RuntimeError(f'Window exited with code {process.returncode}')
            try:
                with HTTP.open(endpoint + '/json/version', timeout=.4):
                    break
            except OSError:
                time.sleep(.12)
        else:
            raise RuntimeError('WebView2 failed to start its test debugging session')
        browser = playwright.chromium.connect_over_cdp(endpoint)
        context = browser.contexts[0]
        page = context.pages[0] if context.pages else context.wait_for_event('page')
        page.set_default_timeout(5500)
        errors = []
        page.on('pageerror', lambda error: errors.append(str(error)))
        page.wait_for_url('https://keyisle.local/index.html')
        yield page
        assert not errors, errors
    except Exception:
        if page and not page.is_closed():
            if screenshots:
                page.screenshot(path=str(screenshots / 'failure.png'))
            print('Window state:', page.locator('#startup p, #sheet-title, .form-error').all_text_contents(), flush=True)
        raise
    finally:
        close_window(process)


def action(scope, name, **attrs):
    more = ''.join(f'[data-{key}="{value}"]' for key, value in attrs.items())
    return scope.locator(f'button[data-action="{name}"]' + more)


def current_code(second):
    digest = hmac.new(base64.b32decode(KEY), struct.pack('>Q', second // 4), hashlib.sha256).digest()
    start = digest[-1] & 15
    number = int.from_bytes(digest[start:start + 4], 'big') & 0x7fffffff
    return f'{number % 10**8:08d}'


def check_display(page):
    code = page.locator('.otp-code:visible')
    expect(code).to_have_text(re.compile(r'^\d{4} \d{4}$'))
    before = int(time.time())
    actual = code.inner_text().replace(' ', '')
    after = int(time.time())
    assert actual in {current_code(t) for t in range(before - 1, after + 1)}, 'Display does not match the independent HMAC calculation'
    return actual


def screenshot(page, folder, name):
    if folder:
        page.screenshot(path=str(folder / (name + '.png')), animations='disabled')


def edit_group_from_card(page, screenshots):
    sheet = page.locator('#sheet')
    cards = page.locator('#group-cards')
    edit = cards.get_by_role('button', name='编辑分组 工作', exact=True)
    results = page.locator('#account-results')
    work = results.locator('.account-tile').filter(has_text=ADDRESS)
    personal = results.locator('.account-tile').filter(has_text='personal@example.invalid')

    # Create an account already in a different group to exercise a real transfer.
    page.keyboard.press('Control+n')
    page.locator('#account-name').fill('个人邮箱')
    page.locator('#account-login').fill('personal@example.invalid')
    page.locator('#account-group').select_option(label='个人')
    sheet.get_by_role('button', name='保存账号').click()
    expect(sheet.locator('#sheet-title')).to_have_text('账号详情')
    page.keyboard.press('Escape')
    expect(sheet).to_be_hidden()

    edit.click()
    expect(sheet.locator('#group-name')).to_have_value('工作')
    sheet.locator('.group-members summary').click()
    member = sheet.locator('.member-row').filter(has_text=ADDRESS).get_by_role('checkbox')
    other = sheet.locator('.member-row').filter(has_text='personal@example.invalid').get_by_role('checkbox')
    expect(member).to_be_checked()
    expect(other).not_to_be_checked()
    member.uncheck()
    other.check()
    sheet.locator('#group-name').fill('未保存的名称')
    sheet.get_by_role('button', name='取消', exact=True).click()
    expect(sheet).to_be_hidden()
    expect(work.locator('.chip')).to_have_text('工作')
    expect(personal.locator('.chip')).to_have_text('个人')
    expect(edit).to_be_focused()

    edit.click()
    sheet.locator('#group-name').fill('个人')
    sheet.get_by_role('button', name='保存分组').click()
    expect(sheet.locator('#group-name:invalid')).to_have_count(1)
    sheet.locator('#group-name').fill('项目组')
    sheet.locator('.group-members summary').click()
    expect(member).to_be_checked()
    member.uncheck()
    sheet.locator('#member-search').fill('personal')
    other.check()
    action(sheet, 'group-color', value='amber').click()
    action(sheet, 'group-icon', value='controller').click()
    expect(other).to_be_checked()
    screenshot(page, screenshots, 'group-editor')
    sheet.locator('#member-search').fill('no-match')
    expect(sheet.locator('#member-empty')).to_be_visible()
    sheet.get_by_role('button', name='保存分组').click()
    expect(sheet).to_be_hidden()
    expect(work.locator('.chip')).to_have_text('未分组')
    expect(personal.locator('.chip')).to_have_text('项目组')
    expect(cards.get_by_role('button', name='编辑分组 项目组', exact=True)).to_be_focused()


def workflow(playwright, profile, screenshots):
    with application(playwright, profile, screenshots) as page:
        expect(page.locator('#startup')).to_be_hidden()
        screenshot(page, screenshots, 'library')
        page.keyboard.press('Control+n')
        sheet = page.locator('#sheet')
        expect(sheet.locator('#sheet-title')).to_have_text('添加账号')
        page.locator('#account-name').fill('设计工作室')
        page.locator('#account-login').fill(ADDRESS)
        page.locator('#account-password').fill(PASSWORD)
        page.locator('#account-website').fill('example.invalid')
        page.locator('#account-note').fill('季度归档 <draft>')
        page.locator('#account-favorite').check()
        action(sheet, 'new-group').click()
        page.locator('#group-name').fill('设计')
        action(sheet, 'group-color', value='sky').click()
        action(sheet, 'group-icon', value='book').click()
        sheet.get_by_role('button', name='保存分组').click()
        expect(page.locator('#account-name')).to_have_value('设计工作室')
        expect(page.locator('#account-group option:checked')).to_have_text('设计')
        page.locator('#account-otp').fill('invalid 101')
        sheet.get_by_role('button', name='保存账号').click()
        expect(sheet.locator('.form-error')).to_contain_text('Base32')
        page.locator('#account-otp').fill(f'otpauth://totp/Fixture?secret={KEY}&period=4&digits=8&algorithm=SHA256')
        sheet.get_by_role('button', name='保存账号').click()
        expect(sheet.locator('#sheet-title')).to_have_text('账号详情')
        old = check_display(page)
        page.wait_for_function('(old) => {const s=document.querySelector("#sheet .otp-code").textContent.replace(/ /g,"");return /^\\d{8}$/.test(s) && s!==old;}', arg=old, timeout=7000)
        check_display(page)
        expect(page.locator('#detail-password')).to_have_text('••••••••')
        action(sheet, 'detail-password').click()
        expect(page.locator('#detail-password')).to_have_text(PASSWORD)
        action(sheet, 'detail-password').click()
        screenshot(page, screenshots, 'detail')
        action(sheet, 'edit-account').click()
        page.locator('#account-group').select_option(label='工作')
        page.locator('#account-name').fill('工作邮箱')
        sheet.get_by_role('button', name='保存账号').click()
        expect(sheet.locator('.detail-ident h3')).to_have_text('工作邮箱')
        expect(sheet.locator('.detail-ident .chip')).to_have_text('工作')
        check_display(page)
        page.keyboard.press('Escape')
        expect(sheet).to_be_hidden()
        action(page.locator('#library-nav'), 'navigate', id='codes').click()
        check_display(page)
        page.keyboard.press('Control+k')
        page.keyboard.type('absent')
        expect(page.locator('#account-count')).to_have_text('0')
        page.keyboard.press('Escape')
        expect(page.locator('#account-count')).to_have_text('1')
        action(page, 'view', value='grid').click()
        expect(page.locator('.code-tile')).to_have_count(1)
        action(page.locator('#library-nav'), 'navigate', id='all').click()
        expect(page.locator('.account-tile')).to_have_count(1)
        edit_group_from_card(page, screenshots)
        action(page, 'settings').click()
        action(sheet, 'theme', value='dark').click()
        action(sheet, 'motion').click()
        sheet.get_by_role('button', name='保存设置').click()
        expect(sheet).to_be_hidden()
        expect(page.locator('html')).to_have_attribute('data-theme', 'dark')
        # Also exercise the small-window navigation and layout on the same document.
        page.set_viewport_size({'width': 520, 'height': 660})
        action(page, 'nav').click()
        page.locator('#group-nav').get_by_role('button', name='编辑分组 设计', exact=True).click()
        sheet.locator('#group-name').fill('设计室')
        sheet.get_by_role('button', name='保存分组').click()
        expect(sheet).to_be_hidden()
        expect(action(page, 'nav')).to_be_focused()
        action(page, 'nav').click()
        action(page.locator('#library-nav'), 'navigate', id='favorites').click()
        expect(page.locator('.account-tile')).to_have_count(1)
        assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
        screenshot(page, screenshots, 'compact-dark')

    data = profile / 'KeyIsle/vault.dat'
    encrypted = data.read_bytes()
    assert len(encrypted) > 128
    for secret in (KEY, PASSWORD, ADDRESS, '工作邮箱'):
        assert secret.encode() not in encrypted and secret.encode('utf-16-le') not in encrypted

    with application(playwright, profile, screenshots) as page:
        expect(page.locator('#startup')).to_be_hidden()
        expect(page.locator('html')).to_have_attribute('data-theme', 'dark')
        expect(page.locator('.account-tile')).to_have_count(2)
        saved = page.locator('.group-card.color-amber').filter(has_text='项目组')
        expect(saved.locator('.i-controller')).to_have_count(1)
        expect(saved.locator('small')).to_have_text('1 个账号')
        expect(page.locator('#group-nav').get_by_role('button', name='编辑分组 设计室', exact=True)).to_be_visible()
        expect(page.locator('.account-tile').filter(has_text=ADDRESS).locator('.chip')).to_have_text('未分组')
        expect(page.locator('.account-tile').filter(has_text='personal@example.invalid').locator('.chip')).to_have_text('项目组')
        action(page.locator('#library-nav'), 'navigate', id='codes').click()
        check_display(page)
        action(page.locator('#library-nav'), 'navigate', id='all').click()
        sheet = page.locator('#sheet')
        action(page, 'manage-groups').click()
        edit = sheet.get_by_role('button', name='编辑分组 项目组', exact=True)
        edit.click()
        page.locator('#group-name').fill('工作室')
        sheet.get_by_role('button', name='保存分组').click()
        sheet.get_by_role('button', name='删除分组 工作室', exact=True).click()
        expect(sheet.locator('.sheet-desc')).to_contain_text('1 个账号')
        action(sheet, 'confirm-delete').click()
        expect(sheet.locator('#sheet-title')).to_have_text('管理分组')
        page.keyboard.press('Escape')
        expect(sheet).to_be_hidden()
        action(page.locator('#library-nav'), 'navigate', id='ungrouped').click()
        page.locator('#account-results').get_by_role('button', name='查看 工作邮箱', exact=True).click()
        expect(sheet.locator('.detail-ident .chip')).to_have_text('未分组')
        action(sheet, 'edit-account').click()
        action(sheet, 'remove-otp').click()
        sheet.get_by_role('button', name='保存账号').click()
        expect(sheet.locator('#sheet-title')).to_have_text('账号详情')
        expect(sheet.locator('.otp-panel')).to_have_count(0)
        action(sheet, 'delete-account').click()
        action(sheet, 'confirm-delete').click()
        expect(sheet).to_be_hidden()
        expect(page.locator('#account-count')).to_have_text('1')

    # An unreadable existing vault must never be mistaken for a fresh one.
    data.write_bytes(b'invalid test fixture')
    with application(playwright, profile, screenshots) as page:
        expect(page.locator('#startup p')).to_contain_text('无法读取')
        assert data.read_bytes() == b'invalid test fixture'


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--screenshots', type=Path)
    parser.add_argument('--exe', type=Path, default=EXE)
    args = parser.parse_args()
    EXE = args.exe.resolve(strict=True)
    if args.screenshots:
        args.screenshots.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='key-isle-test-', ignore_cleanup_errors=True) as tmp, sync_playwright() as pw:
        workflow(pw, Path(tmp), args.screenshots)
    print('Desktop workflow, group editing and transfers, live 2FA rollover, persistence and damaged-file handling passed.')
