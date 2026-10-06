"""Draws the mod's icon: a skull shouting through a loudspeaker, as a crayon sketch.

    python tools/Icon/make_icon.py icon.png [seed]

Drawn at four times the size and scaled down to 256 x 256, which is what Thunderstore asks for.
"""
import math
import random
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter

S = 1024          # working size
OUT = 256

PAPER = (252, 247, 234)
INK = (104, 58, 28)
BONE, BONE_HATCH = (255, 252, 244), (232, 222, 200)
HORN, HORN_HATCH = (255, 226, 160), (240, 158, 44)
BAND, BAND_HATCH = (190, 232, 226), (44, 150, 140)
BELL, BELL_HATCH = (214, 204, 244), (120, 96, 204)
SOCKET, SOCKET_HATCH = (120, 70, 40), (74, 40, 20)
SOUND = (226, 78, 44)
STAR_A, STAR_B = (60, 110, 220), (240, 140, 50)


def wobble(rng, n, amount):
    """A closed, smooth, slightly uneven ring of n radius factors around 1."""
    waves = [(rng.uniform(0, math.tau), k, rng.uniform(0.3, 1.0)) for k in (2, 3, 5)]
    total = sum(w for _, _, w in waves)
    return [1 + amount * sum(w * math.sin(k * math.tau * i / n + p) for p, k, w in waves) / total for i in range(n)]


def blob(rng, cx, cy, rx, ry, amount=0.025, n=72):
    r = wobble(rng, n, amount)
    return [(cx + rx * r[i] * math.cos(math.tau * i / n), cy + ry * r[i] * math.sin(math.tau * i / n)) for i in range(n)]


def turned(points, origin, degrees):
    a = math.radians(degrees)
    c, s = math.cos(a), math.sin(a)
    ox, oy = origin
    return [(ox + x * c - y * s, oy + x * s + y * c) for x, y in points]


def mask_of(*polygons):
    m = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(m)
    for p in polygons:
        d.polygon(p, fill=255)
    return m


def hatch(rng, base, colour, degrees, gap=26, width=15):
    """A square of crayon strokes: uneven lines over a lighter ground."""
    big = int(S * 1.5)
    layer = Image.new("RGB", (big, big), base)
    d = ImageDraw.Draw(layer)
    y = -20
    while y < big + 20:
        x = 0
        pts = []
        while x <= big:
            pts.append((x, y + rng.uniform(-5, 5)))
            x += 60
        d.line(pts, fill=colour, width=int(width * rng.uniform(0.7, 1.2)), joint="curve")
        y += gap * rng.uniform(0.8, 1.25)
    layer = layer.rotate(degrees, resample=Image.BICUBIC)
    off = (big - S) // 2
    return layer.crop((off, off, off + S, off + S))


def paint(img, rng, mask, base, colour, degrees, outline=13, gap=26, width=15):
    """Fills a shape with crayon strokes and puts a brown line round it."""
    edge = mask.filter(ImageFilter.MaxFilter(outline * 2 + 1))
    img.paste(INK, (0, 0), edge)
    img.paste(hatch(rng, base, colour, degrees, gap, width), (0, 0), mask)


def stroke(d, rng, points, colour, width):
    pts = [(x + rng.uniform(-2, 2), y + rng.uniform(-2, 2)) for x, y in points]
    d.line(pts, fill=colour, width=width, joint="curve")
    r = width / 2
    for x, y in (pts[0], pts[-1]):
        d.ellipse((x - r, y - r, x + r, y + r), fill=colour)


def star(d, rng, x, y, size, colour):
    for k in range(3):
        a = math.pi * k / 3 + rng.uniform(-0.15, 0.15)
        dx, dy = size * math.cos(a), size * math.sin(a)
        stroke(d, rng, [(x - dx, y - dy), (x + dx, y + dy)], colour, 9)


def draw(seed):
    rng = random.Random(seed)
    img = Image.new("RGB", (S, S), PAPER)
    d = ImageDraw.Draw(img)

    # ----- skull: a round cranium on a narrower jaw
    cranium = blob(rng, 330, 430, 232, 214, 0.02)
    jaw = blob(rng, 338, 640, 142, 132, 0.025)
    paint(img, rng, mask_of(cranium, jaw), BONE, BONE_HATCH, 28)
    d = ImageDraw.Draw(img)
    for cx, cy, tilt in ((238, 452, -8), (418, 444, 8)):
        socket = turned(blob(rng, 0, 0, 62, 72, 0.05), (cx, cy), tilt)
        d.polygon(socket, fill=SOCKET)
        img.paste(hatch(rng, SOCKET, SOCKET_HATCH, -35, 20, 10), (0, 0), mask_of(socket))
    d = ImageDraw.Draw(img)
    d.polygon([(330, 520), (300, 582), (328, 572), (356, 584)], fill=SOCKET)   # nose
    # teeth: a line across and the gaps between them
    stroke(d, rng, [(222, 668), (270, 676), (330, 678), (392, 672)], INK, 11)
    for x in (252, 292, 332, 372):
        stroke(d, rng, [(x, 640), (x + 2, 712)], INK, 10)
    # a crack, so it is a skull that has seen something
    stroke(d, rng, [(250, 236), (272, 284), (254, 312), (282, 350)], INK, 9)

    # ----- loudspeaker, held to the jaw and pointing up and away
    origin, tilt = (430, 668), -19
    def horn(points):
        return turned(points, origin, tilt)
    cone = horn([(40, -42), (300, -168), (300, 168), (40, 42)])
    mouthpiece = horn([(-34, -34), (48, -40), (48, 40), (-34, 34)])
    grip = horn([(96, 60), (150, 86), (128, 196), (72, 186)])
    paint(img, rng, mask_of(grip), HORN, HORN_HATCH, 70)
    paint(img, rng, mask_of(cone, mouthpiece), HORN, HORN_HATCH, -40)
    band = horn([(118, -80), (166, -103), (166, 103), (118, 80)])
    img.paste(hatch(rng, BAND, BAND_HATCH, 50, 22, 13), (0, 0), mask_of(band))
    d = ImageDraw.Draw(img)
    for x, h in ((118, 80), (166, 103)):
        stroke(d, rng, horn([(x, -h), (x, h)]), INK, 9)
    stroke(d, rng, horn([(48, -40), (48, 40)]), INK, 9)
    bell = horn(blob(rng, 300, 0, 46, 172, 0.015))
    paint(img, rng, mask_of(bell), BELL, BELL_HATCH, 20, outline=12, gap=22, width=12)
    d = ImageDraw.Draw(img)

    # ----- the shout
    cx, cy = horn([(318, 0)])[0]
    for radius, spread, width in ((112, 30, 22), (176, 34, 22), (240, 36, 22)):
        arc = [(cx + radius * math.cos(math.radians(tilt + a)), cy + radius * math.sin(math.radians(tilt + a)))
               for a in range(-spread, spread + 1, 4)]
        stroke(d, rng, arc, SOUND, width)

    for x, y, size, colour in ((132, 150, 20, STAR_B), (640, 150, 18, STAR_A), (120, 850, 18, STAR_A),
                               (700, 905, 20, STAR_B), (880, 820, 18, STAR_A), (860, 170, 16, STAR_B)):
        star(d, rng, x, y, size, colour)

    # ----- rounded corners, then down to size
    corner = Image.new("L", (S, S), 0)
    ImageDraw.Draw(corner).rounded_rectangle((0, 0, S - 1, S - 1), radius=92, fill=255)
    out = img.convert("RGBA")
    out.putalpha(corner)
    return out.resize((OUT, OUT), Image.LANCZOS)


if __name__ == "__main__":
    path = sys.argv[1] if len(sys.argv) > 1 else "icon.png"
    draw(int(sys.argv[2]) if len(sys.argv) > 2 else 7).save(path)
    print("wrote", path)
