"""Fetch the two distributable fonts and their notices. The app uses local copies."""
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / 'ui/assets'
BOOTSTRAP = 'https://cdn.jsdelivr.net/npm/bootstrap-icons@1.13.1/'
INTER = 'https://cdn.jsdelivr.net/npm/@fontsource-variable/inter@5.2.5/'
ICONS = '''plus-lg search x-lg chevron-right chevron-down layout-sidebar-inset grid grid-fill list-ul
    collection collection-fill star star-fill inbox layers folder-plus folder2-open folder2
    shield-lock shield-check sliders2 briefcase person leaf controller book wallet2 heart
    check-lg arrow-down-up clock-history sort-alpha-down pencil trash3 eye eye-slash copy
    box-arrow-up-right key person-vcard key-fill exclamation-circle arrow-repeat
    brightness-high moon-stars circle-half arrow-left lock three-dots arrow-return-left
    person-badge fingerprint journal-text globe2 bookmark folders check-circle-fill
    display folder-fill window-stack arrow-right plus-circle file-earmark-lock'''.split()


def fetch(url):
    return subprocess.check_output(['curl.exe', '--fail', '--location', '--silent', '--show-error', '--retry', '2', url])


def main():
    ASSETS.mkdir(parents=True, exist_ok=True)
    licenses = ROOT / 'licenses'
    licenses.mkdir(exist_ok=True)
    css = fetch(BOOTSTRAP + 'font/bootstrap-icons.css').decode()
    codepoints = dict(re.findall(r'\.bi-([\w-]+)::before\s*\{\s*content:\s*"(\\[a-f\d]+)";', css))
    used = {name: codepoints[name] for name in ICONS if name in codepoints}
    (ASSETS / 'icons.css').write_text('\n'.join(f'.i-{name}::before {{ content: "{code}"; }}' for name, code in used.items()) + '\n', encoding='utf-8')
    (ASSETS / 'icons.woff2').write_bytes(fetch(BOOTSTRAP + 'font/fonts/bootstrap-icons.woff2'))
    (ASSETS / 'inter.woff2').write_bytes(fetch(INTER + 'files/inter-latin-wght-normal.woff2'))
    (licenses / 'Bootstrap-Icons.txt').write_bytes(fetch(BOOTSTRAP + 'LICENSE'))
    (licenses / 'Inter.txt').write_bytes(fetch(INTER + 'LICENSE'))
    print('Unavailable icon names:', sorted(set(ICONS) - used.keys()))


if __name__ == '__main__':
    main()
