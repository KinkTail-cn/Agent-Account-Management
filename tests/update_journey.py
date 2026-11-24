"""Run an older isolated updater, then verify the real packaged release opens its existing vault."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import tempfile
import time
from urllib.request import ProxyHandler, build_opener
import zipfile

from playwright.sync_api import expect, sync_playwright

ROOT = Path(__file__).resolve().parents[1]
EXE = 'agent账号切换器.exe'
HTTP = build_opener(ProxyHandler({}))


def test(archive, install, profile):
    install.mkdir()
    target = install / EXE
    compiler = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    references = ['System.Web.Extensions', 'System.Net.Http', 'System.Security', 'System.IO.Compression', 'System.IO.Compression.FileSystem']
    subprocess.run([str(compiler), '/nologo', '/platform:x64', '/codepage:65001', '/out:' + str(target),
                    *('/r:' + n + '.dll' for n in references), str(ROOT / 'native/ReleaseUpdates.cs'),
                    str(ROOT / 'tests/UpdateFixture.cs')], check=True)
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        port = listener.getsockname()[1]
    env = dict(os.environ, LOCALAPPDATA=str(profile), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=f'--remote-debugging-port={port}')
    version = re.search(r'AssemblyVersion\("(\d+\.\d+\.\d+)\.0"\)', (ROOT / 'native/Program.cs').read_text(encoding='utf-8')).group(1)
    subprocess.run([str(target), str(archive), 'v' + version], env=env, cwd=install, check=True, timeout=30)
    with zipfile.ZipFile(archive) as package:
        expected = hashlib.sha256(package.read(EXE)).digest()
    endpoint = f'http://127.0.0.1:{port}'
    deadline = time.monotonic() + 25
    while time.monotonic() < deadline:
        try:
            with HTTP.open(endpoint + '/json/version', timeout=.5) as response:
                if 'webSocketDebuggerUrl' in json.load(response):
                    break
        except OSError:
            time.sleep(.12)
    else:
        raise RuntimeError('New executable did not open its test window')
    assert hashlib.sha256(target.read_bytes()).digest() == expected
    with sync_playwright() as pw:
        browser = pw.chromium.connect_over_cdp(endpoint)
        ctx = browser.contexts[0]
        page = ctx.pages[0] if ctx.pages else ctx.wait_for_event('page')
        page.wait_for_url('https://keyisle.local/index.html')
        expect(page).to_have_title(target.stem)
        expect(page.locator('.account-row')).to_contain_text('upgrade@example.invalid')
        page.locator('[data-action=updates]').click()
        expect(page.locator('#installed-version')).to_have_text('当前版本 v' + version)
        expect(page.locator('#update-action')).to_be_visible()
    print('The real release replaced the older executable, relaunched, and displayed the original account.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--zip', type=Path, default=ROOT / 'release/Agent-Account-Management-windows-x64.zip')
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='update-journey-', ignore_cleanup_errors=True) as d:
        path = Path(d)
        install = path / '安装 目录'
        try:
            test(args.zip.resolve(), install, path / 'profile')
        finally:
            import psutil
            for proc in psutil.process_iter(['name']):
                if proc.info['name'] == EXE:
                    try:
                        if Path(proc.exe()) == install / EXE:
                            proc.terminate()
                            proc.wait(5)
                    except (psutil.NoSuchProcess, psutil.AccessDenied):
                        pass
