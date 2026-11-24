"""Submit a synthetic code through the shipped UI and Google's real CLI; no Google account required."""
import argparse
from pathlib import Path
import tempfile

from playwright.sync_api import expect, sync_playwright
import desktop_smoke as desktop
from agent_sessions import edit_account, stop_test_clients


def check(driver, root):
    with desktop.application(driver, root, None) as page:
        expect(page.locator('#startup')).to_be_hidden()
        desktop.action(page.locator('#library-nav'), 'navigate', id='agents').click()
        edit_account(page, '授权码测试', 'code-test@example.invalid', 'antigravity-app')
        row = page.locator('.session-row').filter(has_text='code-test@example.invalid')
        launch = desktop.action(row, 'launch-agent')
        record = launch.get_attribute('data-id')
        launch.click()
        sheet = page.locator('#sheet')
        expect(sheet.locator('#google-code-box')).to_be_visible(timeout=18000)
        sheet.locator('#google-code-box summary').click()
        sheet.locator('#google-code').fill('4/keyisle-deliberately-invalid-test-fixture')
        sheet.locator('#google-submit').click()
        expect(sheet.locator('#google-login-status')).to_contain_text('授权码已送达')
        expect(sheet.locator('#google-code-box')).to_be_hidden()
        expect(sheet.locator('#google-login-status')).to_contain_text('Google 未接受', timeout=20000)
        expect(sheet.locator('#google-retry')).to_be_visible()
        expect(sheet.locator('#google-open')).to_be_hidden()
        profile = root / 'KeyIsle/AgentProfiles' / record / 'antigravity-app'
        assert not (profile / 'active-session').exists()
        sheet.locator('#google-retry').click()
        expect(sheet.locator('#google-code-box')).to_be_visible(timeout=18000)
        expect(sheet.locator('#google-code')).to_have_value('')
        assert len(list(profile.glob('Sessions/*/Home'))) == 2
        assert len(list(profile.glob('Sessions/*/GoogleSignIn'))) == 1
        desktop.action(sheet, 'google-cancel').click()
        expect(sheet.locator('#google-login-status')).to_contain_text('取消')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--exe', type=Path, default=desktop.EXE)
    desktop.EXE = parser.parse_args().exe.resolve(strict=True)
    with tempfile.TemporaryDirectory(prefix='key-isle-code-', ignore_cleanup_errors=True) as tmp, sync_playwright() as driver:
        folder = Path(tmp)
        try:
            check(driver, folder)
        finally:
            stop_test_clients(folder)
    print('Windows desktop submission reached the real validation endpoint, rejection was displayed and retry opened a new request.')
