"""Start the native desktop sign-in from the real switcher with a disposable account."""
import argparse
from pathlib import Path
import tempfile
import uuid

import psutil
from playwright.sync_api import expect, sync_playwright

import desktop_smoke as desktop
from agent_sessions import edit_account, stop_test_clients


def desktop_pids(profile):
    directory = '--user-data-dir=' + str(profile / 'UserData')
    result = set()
    for process in psutil.process_iter(['name']):
        if (process.info['name'] or '').lower() != 'antigravity.exe':
            continue
        try:
            args = process.cmdline()
            if directory in args and not any(arg.startswith('--type=') for arg in args):
                result.add(process.pid)
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            continue
    return result


def check(driver, root):
    with desktop.application(driver, root, None) as page:
        expect(page.locator('#startup')).to_be_hidden()
        desktop.action(page.locator('#library-nav'), 'navigate', id='agents').click()
        edit_account(page, 'Desktop login fixture', 'desktop-login@example.invalid', 'antigravity-app')
        row = page.locator('.session-row').filter(has_text='desktop-login@example.invalid')
        launch = desktop.action(row, 'launch-agent')
        identity = launch.get_attribute('data-id')
        profile = root / 'KeyIsle/AgentProfiles' / identity / 'antigravity-app'
        page.evaluate("""() => window.chrome.webview.addEventListener('message', ({data}) => {
          if (data?.value?.desktop) window.desktopLaunchResult = data.value.desktop;
        })""")
        launch.click()
        page.wait_for_function('() => Boolean(window.desktopLaunchResult)', timeout=45000)
        state = page.evaluate('window.desktopLaunchResult')
        print('Native desktop launch result:', state, flush=True)
        expected = {'signInStarted': '等待桌面登录返回', 'onboarding': '桌面首次设置', 'privacy': '待确认隐私选项'}
        assert state in expected, 'Expected a real sign-in request or a recognized native onboarding page'
        expect(row.locator('.auth-chip').first).to_have_text(expected[state])
        opened = desktop_pids(profile)
        assert opened and (profile / 'UserData').is_dir()
        assert (profile / 'desktop-profile').read_text(encoding='utf-8-sig') == 'base'
        assert not (profile / 'active-session').exists()
        # Emulate a subsequent quota authorization and check the launch still owns the same desktop.
        new_quota = str(uuid.uuid4())
        (profile / 'Sessions' / new_quota / 'Home').mkdir(parents=True)
        (profile / 'active-session').write_text(new_quota, encoding='utf-8')
        page.evaluate('delete window.desktopLaunchResult')
        launch.click()
        page.wait_for_function('() => Boolean(window.desktopLaunchResult)', timeout=30000)
        assert desktop_pids(profile) == opened, 'Reauthorizing the quota CLI must not replace the running desktop'
        assert not (profile / 'Sessions' / new_quota / 'UserData').exists()
        assert (profile / 'desktop-profile').read_text(encoding='utf-8-sig') == 'base'


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--exe', type=Path, default=desktop.EXE)
    desktop.EXE = parser.parse_args().exe.resolve(strict=True)
    with tempfile.TemporaryDirectory(prefix='key-isle-native-login-', ignore_cleanup_errors=True) as tmp, sync_playwright() as driver:
        root = Path(tmp)
        try:
            check(driver, root)
        finally:
            stop_test_clients(root)
    print('The native first-run screen was recognized without CLI authorization.')
