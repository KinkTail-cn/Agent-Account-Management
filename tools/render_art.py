"""Render the original layered glass passes and app icon (NumPy and Pillow)."""
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter

OUT = Path(__file__).resolve().parents[1] / 'ui/assets'
SCALE = 1.65


def rounded(x, y, width, height, radius):
    qx, qy = np.abs(x) - width / 2 + radius, np.abs(y) - height / 2 + radius
    return np.hypot(np.maximum(qx, 0), np.maximum(qy, 0)) + np.minimum(np.maximum(qx, qy), 0) - radius


def key(x, y):
    head = rounded(x - 58, y, 83, 78, 28)
    hole = rounded(x - 60, y + 1, 33, 30, 10)
    head = np.maximum(head, -hole)
    bar = rounded(x + 35, y - 2, 142, 21, 6)
    tooth1 = rounded(x + 94, y - 12, 18, 27, 4)
    tooth2 = rounded(x + 67, y - 12, 17, 20, 3)
    return np.minimum.reduce([head, bar, tooth1, tooth2])


def blend(a, b, t):
    t = np.clip(t, 0, 1)[..., None]
    return np.asarray(a) * (1 - t) + np.asarray(b) * t


def image(colors, alpha):
    data = np.empty((*alpha.shape, 4), dtype=np.uint8)
    data[:, :, :3] = np.clip(colors, 0, 255).astype(np.uint8)
    data[:, :, 3] = np.clip(alpha * 255, 0, 255).astype(np.uint8)
    return Image.fromarray(data, 'RGBA')


def coverage(distance):
    return np.clip(.5 - distance * SCALE, 0, 1)


def render_passes():
    height, width = int(332 * SCALE), int(560 * SCALE)
    yy, xx = np.mgrid[:height, :width].astype(np.float32) / SCALE
    stage = Image.new('RGBA', (width, height))

    def coordinates(center, basis):
        (ax, ay), (bx, by) = basis
        x, y = xx - center[0], yy - center[1]
        det = ax * by - ay * bx
        return (x * by - y * bx) / det, (y * ax - x * ay) / det

    def cast(alpha, offset, blur, opacity):
        mask = Image.fromarray((alpha * opacity * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(blur * SCALE))
        shadow = Image.new('RGBA', stage.size, (71, 46, 143, 0))
        shadow.putalpha(mask)
        stage.alpha_composite(shadow, (int(offset[0] * SCALE), int(offset[1] * SCALE)))

    def extrude(distance, thickness, top, side, outline):
        alpha = coverage(distance)
        side_image = image(side, alpha)
        for i in np.linspace(1, 0, max(3, int(thickness * SCALE))):
            stage.alpha_composite(side_image, (round(thickness * .47 * i * SCALE), round(thickness * i * SCALE)))
        dy, dx = np.gradient(distance * SCALE)
        light = np.clip(-dx * .54 - dy * .84, -1, 1)
        bevel = np.exp(-((distance + 1.2) / 2.5) ** 2)
        top = top + (bevel * (light * 35 + outline))[..., None]
        stage.alpha_composite(image(top, alpha))

    def card(center, basis, front):
        x, y = coordinates(center, basis)
        distance = rounded(x, y, 320, 178, 24)
        cast(coverage(distance), (5, 17 if front else 24), 10 if front else 14, .23 if front else .18)
        t = (x + 150) / 300
        if front:
            top = blend([213, 204, 249], [159, 167, 245], t)
            top = blend(top, [255, 228, 221], .88 * np.exp(-((x + 112) / 108) ** 2 - ((y + 67) / 89) ** 2))
            top = blend(top, [166, 228, 250], .94 * np.exp(-((x - 147) / 95) ** 2 - ((y + 63) / 89) ** 2))
            top = blend(top, [248, 209, 248], .75 * np.exp(-((x - 88) / 86) ** 2 - ((y - 72) / 55) ** 2))
            top += (8 * np.sin((x + y * 1.3) / 52))[..., None]
            side = blend([143, 120, 210], [123, 153, 218], t)
        else:
            top = blend([137, 122, 226], [164, 177, 250], t)
            top = blend(top, [211, 183, 251], .72 * np.exp(-((x + 86) / 123) ** 2 - ((y + 74) / 62) ** 2))
            side = blend([105, 91, 188], [119, 133, 216], t)
        rim = np.exp(-((distance + 5) / .75) ** 2)
        top = blend(top, [255, 255, 255], rim * .57)
        if front:
            line = np.minimum(rounded(x + 100, y - 51, 60, 5, 2.5), rounded(x + 110, y - 64, 40, 4, 2))
            for dot in range(4):
                line = np.minimum(line, np.hypot(x - 71 - dot * 16, y - 55) - 3.8)
            top = blend(top, [122, 119, 192], coverage(line) * .52)
            contact = rounded(x + 116, y + 58, 28, 14, 7)
            top = blend(top, [255, 255, 255], coverage(contact) * .84)
        extrude(distance, 6 if front else 7, top, side, 13)

    card((315, 121), ((.93, -.30), (.42, .82)), False)
    card((270, 198), ((.98, -.16), (.35, .86)), True)
    x, y = coordinates((274, 160), ((1.03, -.37), (.49, .86)))
    distance = key(x, y)
    cast(coverage(distance), (5, 18), 7.5, .36)
    cast(coverage(distance), (3, 9), 3, .15)
    top = blend([235, 232, 255], [254, 250, 248], (x + 104) / 196)
    top = blend(top, [190, 187, 223], .55 * np.exp(-((x + .65 * y - 13) / 26) ** 2))
    top = blend(top, [255, 231, 216], .58 * np.exp(-((x + y - 81) / 38) ** 2))
    dy, dx = np.gradient(distance * SCALE)
    bevel = np.clip(1 + distance / 6, 0, 1)
    top += ((-.78 * dy - .40 * dx) * 42 * bevel)[..., None]
    rim = np.exp(-((distance + 5.3) / .75) ** 2)
    top += (rim * 12)[..., None]
    side = blend([126, 119, 170], [213, 207, 233], .52 + .38 * dx + .12 * np.cos(x / 14))
    extrude(distance, 9, top, side, 12)
    stage.save(OUT / 'glass-pass.webp', 'WEBP', quality=94, method=6)


def render_icon():
    side = 384
    yy, xx = np.mgrid[:side, :side].astype(np.float32) / side * 256 - 128
    square = rounded(xx, yy, 247, 247, 62)
    base = blend([151, 130, 253], [85, 59, 205], (xx * .46 + yy + 148) / 290)
    base = blend(base, [198, 190, 255], np.exp(-((xx + 87) / 98) ** 2 - ((yy + 120) / 83) ** 2) * .85)
    base += (np.exp(-((square + 2) / 2) ** 2) * 23)[..., None]
    icon = image(base, coverage(square))
    angle = np.deg2rad(-39)
    x, y = (xx * np.cos(angle) + yy * np.sin(angle)) / .79, (-xx * np.sin(angle) + yy * np.cos(angle)) / .79
    distance = key(x, y)
    icon.alpha_composite(image([77, 51, 176], coverage(key(x - 1, y - 4)) * .35))
    icon.alpha_composite(image(blend([255, 255, 255], [233, 232, 255], (y + 40) / 90), coverage(distance)))
    icon.resize((160, 160), Image.Resampling.LANCZOS).save(OUT / 'mark.png')
    icon.save(OUT / 'app.ico', sizes=[(n, n) for n in (16, 24, 32, 48, 64, 128, 256)])


if __name__ == '__main__':
    OUT.mkdir(parents=True, exist_ok=True)
    render_passes()
    render_icon()
