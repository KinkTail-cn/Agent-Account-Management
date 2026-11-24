"""Exercise profile binding and launch through the desktop UI with disposable accounts."""
import argparse
import os
from pathlib import Path
from urllib.parse import parse_qs, urlsplit
import tempfile
import time

import psutil
from playwright.sync_api import expect, sync_playwright

import desktop_smoke as desktop


def edit_account(page, name, login, client=None):
    page.locator('.add-account').click()
    sheet = page.locator('#sheet')
    sheet.locator('#account-name').fill(name)
    sheet.locator('#account-login').fill(login)
    if client:
        picker = sheet.locator('.client-picker')
        if picker.get_attribute('open') is None:
            picker.locator('summary').click()
        sheet.locator('#agent-client').select_option(client)
    sheet.get_by_role('button', name='保存账号').click()
    expect(sheet.locator('#sheet-title')).to_have_text('账号详情')
    page.keyboard.press('Escape')
    expect(sheet).to_be_hidden()


def wait_for_created(path, timeout=22):
    deadline = time.monotonic() + timeout
    while not path.exists() and time.monotonic() < deadline:
        time.sleep(.12)
    assert path.exists(), 'Client did not initialize its assigned directory: ' + str(path)


def stop_test_clients(profile):
    # Only processes carrying this test's newly generated profile path are eligible.
    marker = os.path.normcase(str(profile))
    owned = {}
    for process in psutil.process_iter(['name']):
        if (process.info['name'] or '').lower() not in {
            'chatgpt.exe', 'codex.exe', 'antigravity.exe', 'antigravity ide.exe', 'chrome.exe', 'msedge.exe'
        }:
            continue
        try:
            if any(marker in os.path.normcase(arg) for arg in process.cmdline()):
                owned[process.pid] = process
                owned.update({child.pid: child for child in process.children(recursive=True)})
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            continue
    for process in owned.values():
        try:
            process.terminate()
        except psutil.NoSuchProcess:
            pass
    psutil.wait_procs(list(owned.values()), timeout=5)


def check_web_persistence(playwright, profiles):
    candidates = [
        Path(os.environ['LOCALAPPDATA']) / 'Google/Chrome/Application/chrome.exe',
        Path(os.environ['ProgramFiles']) / 'Google/Chrome/Application/chrome.exe',
        Path(os.environ['ProgramFiles(x86)']) / 'Microsoft/Edge/Application/msedge.exe',
    ]
    browser = next(path for path in candidates if path.is_file())
    for revisit in (False, True):
        for idx, directory in enumerate(profiles):
            with playwright.chromium.launch_persistent_context(str(directory), executable_path=str(browser),
                                                               headless=True, args=['--no-first-run']) as context:
                page = context.new_page()
                page.route('https://fixture.example.invalid/**',
                           lambda route: route.fulfill(content_type='text/html', body='<title>Login fixture</title>'))
                page.goto('https://fixture.example.invalid/')
                previous = page.evaluate("localStorage.getItem('test-account')")
                if revisit:
                    assert previous == str(idx), 'Wrong identity after reopening profile'
                else:
                    assert previous is None, 'Fresh profiles unexpectedly shared state'
                    page.evaluate("(id) => localStorage.setItem('test-account', id)", str(idx))


def check_google_first_login(page, root, label, client, email):
    row = page.locator('.session-row').filter(has_text=label)
    button = desktop.action(row, 'launch-agent')
    id_ = button.get_attribute('data-id')
    button.click()
    sheet = page.locator('#sheet')
    expect(sheet.locator('#sheet-title')).to_have_text('登录 Antigravity')
    expect(sheet.locator('#google-code-box')).to_be_visible(timeout=25000)
    folder = root / 'KeyIsle/AgentProfiles' / id_ / client
    browsers = list(folder.glob('Sessions/*/GoogleSignIn'))
    assert len(browsers) == 1
    wait_for_created(browsers[0] / 'Local State')
    expected = '--user-data-dir=' + str(browsers[0])
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        for proc in psutil.process_iter(['name']):
            if (proc.info['name'] or '').lower() not in ('chrome.exe', 'msedge.exe'):
                continue
            try:
                args = proc.cmdline()
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                continue
            if expected not in args:
                continue
            for arg in args:
                if not arg.startswith('https://accounts.google.com/'):
                    continue
                params = parse_qs(urlsplit(arg).query)
                assert params['login_hint'] == [email]
                assert 'select_account' in params['prompt'][0].split()
                assert params['response_type'] == ['code']
                return folder, proc.pid
        time.sleep(.15)
    raise AssertionError('The expected account chooser was not launched in its own browser')


def workflow(playwright, root, live, screenshots):
    ids = []
    with desktop.application(playwright, root, screenshots) as page:
        expect(page.locator('#startup')).to_be_hidden()
        edit_account(page, '工作 ChatGPT', 'work@example.invalid')
        desktop.action(page.locator('#library-nav'), 'navigate', id='agents').click()
        desktop.action(page, 'bind-agent').click()
        sheet = page.locator('#sheet')
        sheet.locator('#agent-client').select_option('chatgpt-web')
        sheet.get_by_role('button', name='绑定客户端').click()
        expect(sheet).to_be_hidden()
        edit_account(page, '个人 ChatGPT', 'private@example.invalid', 'chatgpt-web')
        for name in ('工作 ChatGPT', '个人 ChatGPT'):
            row = page.locator('.session-row').filter(has_text=name)
            ids.append(desktop.action(row, 'launch-agent').get_attribute('data-id'))
        assert len(set(ids)) == 2
        desktop.action(page, 'agent-provider', value='claude').click()
        expect(page.locator('.session-row')).to_have_count(0)
        edit_account(page, 'Claude 终端', 'claude@example.invalid', 'claude-code')
        desktop.action(page, 'agent-provider', value='all').click()
        expect(page.locator('.session-row')).to_have_count(3)
        if live:
            for id_ in ids:
                desktop.action(page.locator('#agent-section'), 'launch-agent', id=id_).click()
                directory = root / 'KeyIsle/AgentProfiles' / id_ / 'chatgpt-web/UserData'
                wait_for_created(directory / 'Local State')
            desktop.screenshot(page, screenshots, 'agent-switcher')
        desktop.action(page, 'agent-provider', value='antigravity').click()
        for name, client in [('AG 工作', 'antigravity-ide'), ('AG 桌面', 'antigravity-app')]:
            edit_account(page, name, name + '@example.invalid', client)
        desktop.action(page, 'agent-provider', value='chatgpt').click()
        edit_account(page, 'GPT 桌面', 'desktop@example.invalid', 'chatgpt-desktop')
        if live:
            for provider, label, client in [('chatgpt', 'GPT 桌面', 'chatgpt-desktop'),
                                             ('antigravity', 'AG 桌面', 'antigravity-app'),
                                             ('antigravity', 'AG 工作', 'antigravity-ide')]:
                desktop.action(page, 'agent-provider', value=provider).click()
                if provider == 'antigravity':
                    folder, pid = check_google_first_login(page, root, label, client, label + '@example.invalid')
                    assert not (folder / 'active-session').exists()
                    # Keep the first browser open for the second first-login attempt.
                    if client == 'antigravity-app':
                        first_pid = pid
                        sheet.get_by_role('button', name='关闭', exact=True).first.click()
                    else:
                        assert first_pid != pid and psutil.pid_exists(first_pid)
                        desktop.action(sheet, 'google-cancel').click()
                        expect(sheet.locator('#google-login-status')).to_contain_text('取消')
                        sheet.get_by_role('button', name='关闭', exact=True).first.click()
                    expect(sheet).to_be_hidden()
                    print('Real Google chooser in dedicated profile verified:', client, flush=True)
                    continue
                launch = desktop.action(page.locator('.session-row').filter(has_text=label), 'launch-agent')
                id_ = launch.get_attribute('data-id')
                launch.click()
                directory = root / 'KeyIsle/AgentProfiles' / id_ / client
                wait_for_created(directory / 'UserData/Local State')
                print('Real desktop initialized its managed profile:', client, flush=True)
    if live:
        stop_test_clients(root)
        directories = [root / 'KeyIsle/AgentProfiles' / id_ / 'chatgpt-web/UserData' for id_ in ids]
        check_web_persistence(playwright, directories)
    with desktop.application(playwright, root, screenshots) as page:
        expect(page.locator('#startup')).to_be_hidden()
        desktop.action(page.locator('#library-nav'), 'navigate', id='agents').click()
        expect(page.locator('.session-row')).to_have_count(6)
        for id_ in ids:
            launch = desktop.action(page.locator('#agent-section'), 'launch-agent', id=id_)
            expect(launch).to_have_text('切换到账号' if live else '首次打开')
        desktop.action(page, 'detect-agents').click()
        desktop.screenshot(page, screenshots, 'agent-switcher')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--exe', type=Path, default=desktop.EXE)
    parser.add_argument('--live', action='store_true', help='Open installed clients using empty test profiles')
    parser.add_argument('--screenshots', type=Path)
    args = parser.parse_args()
    desktop.EXE = args.exe.resolve(strict=True)
    if args.screenshots:
        args.screenshots.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='key-isle-agents-', ignore_cleanup_errors=True) as temp, sync_playwright() as driver:
        root = Path(temp)
        try:
            workflow(driver, root, args.live, args.screenshots)
        finally:
            if args.live:
                stop_test_clients(root)
    print('Agent bindings, filters and persisted profiles passed.')
