"""Build the Windows portable release. The same package is consumed by the in-app updater."""
import argparse
import hashlib
import re
import sys
import zipfile
from pathlib import Path

from install_quota_cli import SHA512, URL, VERSION

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
import build

NAME = 'agent账号切换器.exe'
ARCHIVE = 'Agent-Account-Management-windows-x64.zip'


def component_installer():
    # The portable archive installs this optional vendor CLI on demand, using the same pin as the source installer.
    script = f'''@echo off
setlocal
set "AGENT_CLI_DIR=%LOCALAPPDATA%\\KeyIsle\\tools\\agy\\{VERSION}"
if not exist "%AGENT_CLI_DIR%" mkdir "%AGENT_CLI_DIR%"
set "AGENT_CLI_FILE=%AGENT_CLI_DIR%\\agy.exe"
set "AGENT_CLI_TEMP=%AGENT_CLI_DIR%\\agy-%RANDOM%-%RANDOM%.download"
powershell.exe -NoProfile -Command "if ((Test-Path -LiteralPath $env:AGENT_CLI_FILE) -and (Get-FileHash -Algorithm SHA512 -LiteralPath $env:AGENT_CLI_FILE).Hash -eq '{SHA512}') {{exit 0}}; exit 1"
if not errorlevel 1 goto done
echo Downloading the official Google Antigravity CLI {VERSION}...
curl.exe --fail --location --show-error --retry 2 --max-time 240 --output "%AGENT_CLI_TEMP%" "{URL}"
if errorlevel 1 goto failed
powershell.exe -NoProfile -Command "$ErrorActionPreference='Stop'; if ((Get-FileHash -Algorithm SHA512 -LiteralPath $env:AGENT_CLI_TEMP).Hash -ne '{SHA512}') {{exit 1}}; Move-Item -LiteralPath $env:AGENT_CLI_TEMP -Destination $env:AGENT_CLI_FILE -Force"
if errorlevel 1 goto failed
:done
echo Antigravity CLI is ready.
pause
exit /b 0
:failed
if exist "%AGENT_CLI_TEMP%" del "%AGENT_CLI_TEMP%"
echo Installation failed. Check the connection and try again.
pause
exit /b 1
'''
    return script.replace('\n', '\r\n').encode('ascii')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--tag', help='Require the source version to match this Git tag')
    args = parser.parse_args()
    source = (ROOT / 'native/Program.cs').read_text(encoding='utf-8')
    version = re.search(r'AssemblyVersion\("(\d+\.\d+\.\d+)\.0"\)', source).group(1)
    tag = 'v' + version
    if args.tag and args.tag != tag:
        parser.error(f'Tag {args.tag} does not match program version {tag}')
    build.main(ROOT / NAME)
    output = ROOT / 'release'
    output.mkdir(exist_ok=True)
    archive = output / ARCHIVE
    pending = archive.with_suffix('.tmp')
    with zipfile.ZipFile(pending, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as package:
        package.write(ROOT / NAME, NAME)
        package.write(ROOT / 'README.md', '使用说明.md')
        package.writestr('Install-Antigravity-CLI.cmd', component_installer())
        for notice in sorted((ROOT / 'licenses').glob('*.txt')):
            package.write(notice, 'licenses/' + notice.name)
    pending.replace(archive)
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    (output / 'SHA256SUMS.txt').write_text(f'{digest}  {ARCHIVE}\n', encoding='ascii')
    print(f'{tag}  {archive}  ({archive.stat().st_size:,} bytes)')


if __name__ == '__main__':
    main()
