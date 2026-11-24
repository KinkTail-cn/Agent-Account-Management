"""Build the self-contained Windows host with the .NET Framework compiler."""
import base64
import hashlib
import os
from pathlib import Path
import subprocess
import zipfile


ROOT = Path(__file__).resolve().parent
CACHE = ROOT / '.build'
SDK = '1.0.3537.50'
SDK_HASH = 'QWnduPtmvtQjH07KtLRQgyYa857P1/cyM8stgy052d53P6WoN4mngtL0bGhs+0DyivdYVirm0GOfaRSM7bcc6w=='


def sdk_files():
    CACHE.mkdir(exist_ok=True)
    archive = CACHE / f'webview2-{SDK}.nupkg'
    if not archive.exists():
        url = f'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/{SDK}/microsoft.web.webview2.{SDK}.nupkg'
        temporary = archive.with_suffix('.download')
        subprocess.run(['curl.exe', '--fail', '--silent', '--show-error', '--location',
                        '--retry', '2', '--output', str(temporary), url], check=True)
        temporary.replace(archive)
    if hashlib.sha512(archive.read_bytes()).digest() != base64.b64decode(SDK_HASH):
        raise ValueError('WebView2 SDK checksum mismatch')
    with zipfile.ZipFile(archive) as package:
        for source in ['lib/net462/Microsoft.Web.WebView2.Core.dll',
                       'lib/net462/Microsoft.Web.WebView2.WinForms.dll',
                       'runtimes/win-x64/native/WebView2Loader.dll']:
            library = CACHE / Path(source).name
            contents = package.read(source)
            if not library.exists() or library.read_bytes() != contents:
                library.write_bytes(contents)
        (ROOT / 'licenses' / 'WebView2.txt').write_bytes(package.read('LICENSE.txt'))


def main(output=ROOT / 'agent账号切换器.exe'):
    sdk_files()
    compiler = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    command = [str(compiler), '/nologo', '/target:winexe', '/platform:x64', '/optimize+',
               '/codepage:65001', '/utf8output', '/win32manifest:native/app.manifest',
               '/win32icon:ui/assets/app.ico', f'/out:{output}']
    for reference in ['System.Windows.Forms.dll', 'System.Drawing.dll', 'System.Security.dll',
                      'System.Web.Extensions.dll', 'System.Web.dll', 'System.Management.dll', 'System.Net.Http.dll',
                      'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll',
                      str(compiler.parent / 'WPF/UIAutomationClient.dll'), str(compiler.parent / 'WPF/UIAutomationTypes.dll'),
                      str(compiler.parent / 'WPF/WindowsBase.dll'), '.build/Microsoft.Web.WebView2.Core.dll',
                      '.build/Microsoft.Web.WebView2.WinForms.dll']:
        command.append('/reference:' + reference)
    for library in sorted(CACHE.glob('*.dll')):
        command.append(f'/resource:{library},library/{library.name}')
    for asset in sorted((ROOT / 'ui').rglob('*')):
        if asset.is_file() and asset.suffix != '.ico':
            command.append(f'/resource:{asset},site/{asset.relative_to(ROOT / "ui").as_posix()}')
    for license_file in sorted((ROOT / 'licenses').glob('*.txt')):
        command.append(f'/resource:{license_file},license/{license_file.name}')
    command.extend(str(f) for f in sorted((ROOT / 'native').glob('*.cs')))
    subprocess.run(command, cwd=ROOT, check=True)
    print(output)


if __name__ == '__main__':
    main()
