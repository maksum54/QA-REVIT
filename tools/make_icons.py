"""Generate ribbon/window icons (PNG) for the QA Revit add-in. Run: python tools/make_icons.py"""
import math
import os
from PIL import Image, ImageDraw

OUT = os.path.join(os.path.dirname(__file__), "..", "src", "QARevit.Addin", "Resources")
S = 512  # draw large, downscale for anti-aliasing

NAVY = (15, 32, 66, 255)
BLUE = (37, 99, 235, 255)
TEAL = (20, 184, 166, 255)
GREEN = (22, 163, 74, 255)
AMBER = (245, 158, 11, 255)
WHITE = (255, 255, 255, 255)
PAPER = (241, 245, 249, 255)
LINE = (148, 163, 184, 255)


def canvas():
    return Image.new("RGBA", (S, S), (0, 0, 0, 0))


def sheet(d, x0, y0, x1, y1, fill=PAPER, fold=70):
    d.polygon([(x0, y0), (x1 - fold, y0), (x1, y0 + fold), (x1, y1), (x0, y1)], fill=fill, outline=NAVY, width=18)
    d.polygon([(x1 - fold, y0), (x1 - fold, y0 + fold), (x1, y0 + fold)], fill=LINE, outline=NAVY, width=14)


def lines(d, x0, x1, ys):
    for y in ys:
        d.rounded_rectangle([x0, y, x1, y + 22], 11, fill=LINE)


def badge(d, cx, cy, r, color):
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=color, outline=WHITE, width=20)


def check(d, cx, cy, r):
    d.line([(cx - r * .5, cy), (cx - r * .12, cy + r * .4), (cx + r * .55, cy - r * .4)], fill=WHITE, width=int(r * .26), joint="curve")


def dashboard():
    im = canvas(); d = ImageDraw.Draw(im)
    sheet(d, 70, 40, 380, 440)
    lines(d, 120, 320, [140, 200, 260, 320])
    badge(d, 360, 370, 115, GREEN)
    check(d, 360, 370, 115)
    return im


def settings():
    im = canvas(); d = ImageDraw.Draw(im)
    cx = cy = S / 2; teeth = 8
    pts = []
    for i in range(teeth * 2):
        a = math.pi * i / teeth
        r = 210 if i % 2 == 0 else 165
        for da in (-0.17, 0.17):
            pts.append((cx + r * math.cos(a + da), cy + r * math.sin(a + da)))
    d.polygon(pts, fill=BLUE, outline=NAVY)
    d.ellipse([cx - 160, cy - 160, cx + 160, cy + 160], fill=BLUE)
    d.ellipse([cx - 70, cy - 70, cx + 70, cy + 70], fill=WHITE)
    return im


def excel():
    im = canvas(); d = ImageDraw.Draw(im)
    sheet(d, 90, 40, 400, 440, fill=WHITE)
    for i in range(4):
        y = 140 + i * 60
        d.rectangle([130, y, 210, y + 40], fill=GREEN)
        d.rectangle([225, y, 350, y + 40], fill=(187, 247, 208, 255))
    badge(d, 380, 390, 95, GREEN)
    d.polygon([(380, 440), (330, 380), (362, 380), (362, 335), (398, 335), (398, 380), (430, 380)], fill=WHITE)
    return im


def ai():
    im = canvas(); d = ImageDraw.Draw(im)
    def star(cx, cy, r, color):
        p = []
        for i in range(8):
            a = math.pi / 4 * i - math.pi / 2
            rr = r if i % 2 == 0 else r * .28
            p.append((cx + rr * math.cos(a), cy + rr * math.sin(a)))
        d.polygon(p, fill=color)
    star(230, 280, 200, BLUE)
    star(400, 120, 90, TEAL)
    star(400, 410, 60, AMBER)
    return im


def brand():
    """Logo: rounded navy tile with sheet + check (window icon / header)."""
    im = canvas(); d = ImageDraw.Draw(im)
    d.rounded_rectangle([16, 16, S - 16, S - 16], 110, fill=NAVY)
    d.rounded_rectangle([130, 100, 340, 400], 26, fill=WHITE)
    lines(d, 165, 300, [160, 210, 260])
    badge(d, 345, 355, 95, TEAL)
    check(d, 345, 355, 95)
    return im


def save(im, name, sizes=(16, 32)):
    for s in sizes:
        im.resize((s, s), Image.LANCZOS).save(os.path.join(OUT, f"{name}{s}.png"))


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    save(dashboard(), "dashboard")
    save(settings(), "settings")
    save(excel(), "excel")
    save(ai(), "ai")
    save(brand(), "brand", sizes=(16, 32, 64, 256))
