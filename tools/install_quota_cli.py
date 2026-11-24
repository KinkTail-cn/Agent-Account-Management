"""Install the pinned Google CLI used by agent账号切换器 for Antigravity's /usage report."""
import hashlib
import os
from pathlib import Path
import subprocess
import tempfile

VERSION = '1.5.16'
URL = 'https://storage.googleapis.com/antigravity-public/antigravity-cli/1.5.16-5250855658717184/windows-x64/cli_windows_x64.exe'
SHA512 = '058a85911c40a0beba24d0ae4e3227ea5060388060d4746aa6b48722ff43516c16fbffe6d30e31e2c266d5b47495a7886f072cc4e0143b26eb3cd9a007578c83'


def checksum(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha512').hexdigest()


def main():
    directory = Path(os.environ['LOCALAPPDATA']) / 'KeyIsle/tools/agy' / VERSION
    directory.mkdir(parents=True, exist_ok=True)
    target = directory / 'agy.exe'
    if target.exists() and checksum(target) == SHA512:
        print(target)
        return
    with tempfile.NamedTemporaryFile(dir=directory, delete=False, suffix='.download') as temporary:
        staging = Path(temporary.name)
    try:
        subprocess.run(['curl.exe', '--fail', '--location', '--silent', '--show-error', '--retry', '2',
                        '--max-time', '210', '--output', str(staging), URL], check=True)
        if checksum(staging) != SHA512:
            raise ValueError('The downloaded CLI did not match the Google release manifest.')
        staging.replace(target)
        print(target)
    finally:
        staging.unlink(missing_ok=True)


if __name__ == '__main__':
    main()
