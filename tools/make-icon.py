"""Draws the M-Dir application icon (src/MDir/Assets/M-Dir.ico) and a preview sheet.

A folder (the "file explorer") with a bold navy "M" on its front. Sizes up to 32 px use a chunkier drawing
(no shadow, a larger M) so the letter stays readable at the taskbar and Explorer list sizes.

    python tools/make-icon.py            # needs Pillow
"""
import io
import os
import struct

from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, 'src', 'MDir', 'Assets')
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
SUPER = 8  # supersampling factor

AMBER_DARK = (214, 140, 24, 255)
AMBER_TOP = (255, 214, 110, 255)
AMBER_BOTTOM = (245, 170, 40, 255)
NAVY = (22, 52, 130, 255)
WHITE = (255, 255, 255, 255)


def vertical_gradient(size, box, top, bottom):
    x0, y0, x1, y1 = box
    layer = Image.new('RGBA', size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    height = max(1, y1 - y0)
    for y in range(y0, y1):
        t = (y - y0) / height
        color = tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(4))
        draw.line([(x0, y), (x1, y)], fill=color)
    return layer


def m_polygon(small):
    # Coordinates on a 256 x 256 canvas. apex = where the V notch of the M reaches down, d = thickness of the V.
    if small:
        left, right, top, bottom, stem, apex, d = 36, 220, 88, 214, 44, 160, 42
    else:
        left, right, top, bottom, stem, apex, d = 52, 204, 100, 204, 28, 160, 36
    cx = (left + right) / 2
    return [
        (left, bottom), (left, top), (left + stem, top), (cx, apex), (right - stem, top), (right, top),
        (right, bottom), (right - stem, bottom), (right - stem, top + d), (cx, apex + d), (left + stem, top + d), (left + stem, bottom),
    ]


def draw_icon(px):
    small = px <= 32
    s = px * SUPER
    k = s / 256
    img = Image.new('RGBA', (s, s), (0, 0, 0, 0))

    def scaled(points):
        return [(x * k, y * k) for x, y in points]

    def rrect(layer_draw, box, radius, fill):
        layer_draw.rounded_rectangle([c * k for c in box], radius=radius * k, fill=fill)

    # Soft shadow under the folder (large sizes only).
    if not small:
        shadow = Image.new('RGBA', (s, s), (0, 0, 0, 0))
        rrect(ImageDraw.Draw(shadow), (16, 88, 246, 236), 20, (0, 0, 0, 90))
        img.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(5 * k)))

    draw = ImageDraw.Draw(img)
    # Back of the folder with its tab.
    rrect(draw, (10, 30, 100, 84), 14, AMBER_DARK)
    rrect(draw, (10, 52, 246, 222), 18, AMBER_DARK)

    # Front of the folder: gradient clipped to a rounded rectangle.
    front_box = (10, 78, 246, 224)
    mask = Image.new('L', (s, s), 0)
    rrect(ImageDraw.Draw(mask), front_box, 20, 255)
    gradient = vertical_gradient((s, s), tuple(round(c * k) for c in front_box), AMBER_TOP, AMBER_BOTTOM)
    front = Image.new('RGBA', (s, s), (0, 0, 0, 0))
    front.paste(gradient, (0, 0), mask)
    img.alpha_composite(front)

    # Highlight line along the top edge of the front.
    if not small:
        edge = ImageDraw.Draw(img)
        edge.rounded_rectangle([14 * k, 80 * k, 242 * k, 94 * k], radius=8 * k, outline=(255, 240, 200, 150), width=max(1, round(2 * k)))

    # The M: a white outline first so it stands out on any background, then navy.
    polygon = scaled(m_polygon(small))
    letter = Image.new('L', (s, s), 0)
    ImageDraw.Draw(letter).polygon(polygon, fill=255)
    grow = max(3, round((4 if small else 7) * k))
    outline = letter.filter(ImageFilter.MaxFilter(grow * 2 + 1))
    img.paste(WHITE, (0, 0), outline)
    img.paste(NAVY, (0, 0), letter)

    return img.resize((px, px), Image.LANCZOS)


def write_ico(path, images):
    entries = []
    for image in images:
        buffer = io.BytesIO()
        image.save(buffer, format='PNG')
        entries.append((image.width, buffer.getvalue()))

    header = struct.pack('<HHH', 0, 1, len(entries))
    offset = 6 + 16 * len(entries)
    directory = b''
    data = b''
    for size, png in entries:
        directory += struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(png), offset + len(data))
        data += png
    with open(path, 'wb') as file:
        file.write(header + directory + data)


def main():
    os.makedirs(OUT, exist_ok=True)
    images = [draw_icon(px) for px in SIZES]
    write_ico(os.path.join(OUT, 'M-Dir.ico'), images)

    # Preview sheet on light and dark backgrounds (not part of the build).
    sheet = Image.new('RGBA', (sum(SIZES) + 20 * (len(SIZES) + 1), 2 * 280), (255, 255, 255, 255))
    ImageDraw.Draw(sheet).rectangle([0, 280, sheet.width, sheet.height], fill=(32, 32, 32, 255))
    for row in range(2):
        x = 20
        for image in images:
            sheet.alpha_composite(image, (x, row * 280 + 10))
            x += image.width + 20
    sheet.convert('RGB').save(os.path.join(ROOT, 'tools', 'icon-preview.png'))
    print('wrote', os.path.join(OUT, 'M-Dir.ico'))


if __name__ == '__main__':
    main()
