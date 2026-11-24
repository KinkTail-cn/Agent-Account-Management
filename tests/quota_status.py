"""Verify account status, web quota import and profile isolation in the built desktop app."""
import argparse
from pathlib import Path
import tempfile

from playwright.sync_api import expect, sync_playwright

import desktop_smoke as desktop
from agent_sessions import edit_account


def workflow(driver, root, shots):
    with desktop.application(driver, root, shots) as page:
        expect(page.locator('#startup')).to_be_hidden()
        desktop.action(page.locator('#library-nav'), 'navigate', id='agents').click()
        edit_account(page, '网页订阅', 'one@example.invalid', 'claude-web')
        edit_account(page, '第二个账号', 'two@example.invalid', 'claude-web')
        first = page.locator('.session-row').filter(has_text='one@example.invalid')
        second = page.locator('.session-row').filter(has_text='two@example.invalid')
        sheet = page.locator('#sheet')
        desktop.action(first, 'edit-account').click()
        sheet.locator('#account-status').select_option('issue')
        sheet.get_by_role('button', name='保存账号').click()
        expect(sheet.locator('.record-badge')).to_have_text('异常')
        page.keyboard.press('Escape')
        expect(sheet).to_be_hidden()
        expect(first.locator('.record-badge')).to_have_text('异常')
        desktop.action(first, 'quota-import').click()
        sheet.locator('#quota-text').fill('Context 50% used')
        sheet.get_by_role('button', name='保存摘录').click()
        expect(sheet.locator('.form-error')).to_contain_text('没有识别')
        sheet.locator('#quota-text').fill('Current session\n23% used\nWeekly Sonnet\n0% remaining')
        sheet.get_by_role('button', name='保存摘录').click()
        expect(sheet).to_be_hidden()
        expect(first.locator('.quota-details summary')).to_contain_text('77%')
        expect(first).to_contain_text('有额度已用完')
        first.locator('.quota-details summary').click()
        expect(first.locator('.quota-bucket')).to_have_count(2)
        expect(first.locator('.quota-source')).to_contain_text('手动摘录')
        expect(second.locator('.quota-bucket')).to_have_count(0)
        expect(first.locator('.auth-chip').first).to_contain_text('网页确认')
        desktop.screenshot(page, shots, 'quota-status')
        edit_account(page, 'Codex 测试', 'fresh@example.invalid', 'codex-cli')
        row = page.locator('.session-row').filter(has_text='fresh@example.invalid')
        expect(row.locator('.auth-chip').first).to_have_text('尚未登录')
        identity = row.locator('[data-quota-id]').get_attribute('data-quota-id')
        (root / 'KeyIsle/AgentProfiles' / identity / 'codex-cli/CodexHome').mkdir(parents=True)
        desktop.action(row, 'quota-refresh').click()
        expect(row.locator('.auth-chip').first).to_have_text('未登录', timeout=18000)
        expect(row.locator('.quota-bucket')).to_have_count(0)
        edit_account(page, 'Claude 测试', 'fresh-claude@example.invalid', 'claude-code')
        row = page.locator('.session-row').filter(has_text='fresh-claude@example.invalid')
        identity = row.locator('[data-quota-id]').get_attribute('data-quota-id')
        (root / 'KeyIsle/AgentProfiles' / identity / 'claude-code/ClaudeConfig').mkdir(parents=True)
        desktop.action(row, 'quota-refresh').click()
        expect(row.locator('.auth-chip').first).to_have_text('未登录', timeout=14000)
    with desktop.application(driver, root, shots) as page:
        expect(page.locator('#startup')).to_be_hidden()
        expect(page.locator('.account-row').filter(has_text='one@example.invalid').locator('.record-badge')).to_have_text('异常')
        desktop.action(page.locator('#library-nav'), 'navigate', id='agents').click()
        first = page.locator('.session-row').filter(has_text='one@example.invalid')
        expect(first.locator('.quota-details summary')).to_contain_text('77%')
        expect(page.locator('.session-row').filter(has_text='two@example.invalid').locator('.quota-bucket')).to_have_count(0)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--exe', type=Path, default=desktop.EXE)
    parser.add_argument('--screenshots', type=Path)
    args = parser.parse_args()
    desktop.EXE = args.exe.resolve(strict=True)
    if args.screenshots:
        args.screenshots.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='key-isle-quota-', ignore_cleanup_errors=True) as temp, sync_playwright() as driver:
        workflow(driver, Path(temp), args.screenshots)
    print('Account status, live signed-out checks, quota imports, account isolation and persistence passed.')
