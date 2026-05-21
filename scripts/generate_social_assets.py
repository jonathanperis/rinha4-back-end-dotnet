#!/usr/bin/env python3
"""Generate deterministic favicon and social preview assets for the Pages site."""
from __future__ import annotations

import math
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont, ImageFilter

ROOT = Path(__file__).resolve().parents[1]
PUBLIC = ROOT / "docs" / "public"

W, H = 1200, 630
BG_DEEP = (4, 18, 14)
BG_METAL = (7, 27, 20)
GREEN = (23, 134, 0)
MINT = (92, 255, 177)
GREEN_SOFT = (86, 184, 77)
TEXT = (223, 248, 220)
MUTED = (154, 203, 147)
AMBER = (255, 179, 0)
PINK = (255, 45, 85)
VIOLET = (125, 100, 255)
BLACKISH = (3, 12, 9)


def font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont:
    candidates = [
        "/usr/share/fonts/truetype/dejavu/DejaVuSansMono-Bold.ttf" if bold else "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
        "/usr/share/fonts/truetype/liberation2/LiberationMono-Bold.ttf" if bold else "/usr/share/fonts/truetype/liberation2/LiberationMono-Regular.ttf",
    ]
    for candidate in candidates:
        path = Path(candidate)
        if path.exists():
            return ImageFont.truetype(str(path), size=size)
    return ImageFont.load_default()


def lerp(a: int, b: int, t: float) -> int:
    return round(a + (b - a) * t)


def mix(c1: tuple[int, int, int], c2: tuple[int, int, int], t: float) -> tuple[int, int, int]:
    return tuple(lerp(a, b, t) for a, b in zip(c1, c2))


def draw_grid(draw: ImageDraw.ImageDraw, width: int, height: int, step: int, alpha: int = 38) -> None:
    color = (*MINT, alpha)
    for x in range(0, width + 1, step):
        draw.line([(x, 0), (x, height)], fill=color, width=1)
    for y in range(0, height + 1, step):
        draw.line([(0, y), (width, y)], fill=color, width=1)


def glow_line(base: Image.Image, points: list[tuple[int, int]], color: tuple[int, int, int], width: int = 5) -> None:
    glow = Image.new("RGBA", base.size, (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    gd.line(points, fill=(*color, 130), width=width * 4, joint="curve")
    glow = glow.filter(ImageFilter.GaussianBlur(width * 1.8))
    base.alpha_composite(glow)
    ImageDraw.Draw(base).line(points, fill=(*color, 255), width=width, joint="curve")


def draw_mark(draw: ImageDraw.ImageDraw, x: int, y: int, scale: float = 1.0) -> None:
    def p(px: float, py: float) -> tuple[int, int]:
        return (round(x + px * scale), round(y + py * scale))

    # Hex/vector hull inspired by title-mark.svg.
    hull = [p(30, 14), p(124, 14), p(170, 72), p(124, 130), p(30, 130), p(0, 72), p(30, 14)]
    draw.line(hull, fill=(*MINT, 245), width=max(2, round(4 * scale)), joint="curve")
    draw.line([p(18, 47), p(55, 47), p(68, 66), p(86, 26), p(112, 111), p(128, 78), p(164, 78)], fill=(*MINT, 255), width=max(3, round(5 * scale)))
    draw.line([p(10, 72), p(34, 72)], fill=(*AMBER, 255), width=max(3, round(5 * scale)))
    draw.line([p(162, 72), p(190, 72)], fill=(*AMBER, 255), width=max(3, round(5 * scale)))
    r = max(4, round(7 * scale))
    draw.ellipse([p(65, 66)[0]-r, p(65,66)[1]-r, p(65,66)[0]+r, p(65,66)[1]+r], outline=(*MINT,255), width=max(2,round(4*scale)), fill=(*BG_DEEP,255))
    draw.ellipse([p(112, 111)[0]-r, p(112,111)[1]-r, p(112,111)[0]+r, p(112,111)[1]+r], outline=(*PINK,255), width=max(2,round(4*scale)), fill=(*BG_DEEP,255))
    f_big = font(round(54 * scale), bold=True)
    f_small = font(round(22 * scale), bold=True)
    draw.text(p(42, 68), "R4", font=f_big, fill=(*TEXT, 255), anchor="ls")
    draw.text(p(135, 87), "p99", font=f_small, fill=(*AMBER, 255), anchor="ls")


def rounded_box(draw: ImageDraw.ImageDraw, xy: tuple[int, int, int, int], outline: tuple[int, int, int], fill: tuple[int, int, int, int], width: int = 2, radius: int = 10) -> None:
    draw.rounded_rectangle(xy, radius=radius, fill=fill, outline=(*outline, 180), width=width)


def generate_social() -> Image.Image:
    img = Image.new("RGBA", (W, H), BG_DEEP + (255,))
    pix = img.load()
    for yy in range(H):
        t = yy / (H - 1)
        row = mix(BG_METAL, BG_DEEP, t)
        for xx in range(W):
            radial = math.hypot((xx - 980) / 920, (yy - 30) / 620)
            lift = max(0.0, 1.0 - radial) * 0.16
            pix[xx, yy] = (*mix(row, VIOLET, lift), 255)
    draw = ImageDraw.Draw(img, "RGBA")
    draw_grid(draw, W, H, 40, alpha=16)
    for y in range(0, H, 6):
        draw.line([(0, y), (W, y)], fill=(0, 0, 0, 34), width=1)

    # Glowing mark capsule.
    halo = Image.new("RGBA", img.size, (0, 0, 0, 0))
    hd = ImageDraw.Draw(halo, "RGBA")
    hd.rounded_rectangle((710, 70, 1108, 330), radius=22, fill=(23, 134, 0, 34), outline=(*GREEN, 120), width=2)
    halo = halo.filter(ImageFilter.GaussianBlur(10))
    img.alpha_composite(halo)
    rounded_box(draw, (704, 64, 1114, 336), GREEN, (4, 18, 14, 212), width=2, radius=22)
    draw_mark(draw, 780, 104, 1.42)

    # Top labels and title.
    draw.text((72, 76), "RINHA 2026 // .NET 10 NATIVEAOT", font=font(28, True), fill=AMBER + (255,))
    draw.text((72, 145), "FRAUD SIGNAL", font=font(80, True), fill=GREEN_SOFT + (255,))
    draw.text((78, 150), "FRAUD SIGNAL", font=font(80, True), fill=(*PINK, 190))
    draw.text((72, 224), "UNDER LOAD", font=font(80, True), fill=TEXT + (255,))
    draw.text((74, 306), "official result synced // CI reports archived // source-backed proof", font=font(25), fill=MUTED + (255,))

    # Evidence row.
    cards = [
        (72, 402, 324, 526, "OFFICIAL", "1.12ms", "p99 // issue #5000", AMBER),
        (344, 402, 596, 526, "SCORE", "5950.41", "0% failures", GREEN_SOFT),
        (616, 402, 868, 526, "RUNTIME", "NativeAOT", "raw HTTP // UDS", MINT),
        (888, 402, 1114, 526, "TOPOLOGY", "2 API + LB", "1 CPU / 350MB", VIOLET),
    ]
    for x1, y1, x2, y2, label, value, sub, accent in cards:
        rounded_box(draw, (x1, y1, x2, y2), accent, (3, 12, 9, 190), width=2, radius=12)
        draw.text((x1 + 18, y1 + 18), label, font=font(18, True), fill=accent + (255,))
        draw.text((x1 + 18, y1 + 58), value, font=font(33, True), fill=TEXT + (255,))
        draw.text((x1 + 18, y1 + 96), sub, font=font(17), fill=MUTED + (255,))

    # Bottom command strip.
    draw.rectangle((72, 560, 1114, 594), fill=(0, 0, 0, 92), outline=(*GREEN, 120), width=1)
    draw.text((92, 584), "> verify https://jonathanperis.github.io/rinha4-back-end-dotnet/", font=font(20), fill=GREEN_SOFT + (255,), anchor="ls")
    return img.convert("RGB")


def generate_icon(size: int) -> Image.Image:
    img = Image.new("RGBA", (size, size), BG_DEEP + (255,))
    draw = ImageDraw.Draw(img, "RGBA")
    for y in range(size):
        t = y / max(1, size - 1)
        draw.line([(0, y), (size, y)], fill=mix(BG_METAL, BG_DEEP, t) + (255,))
    step = max(8, size // 6)
    draw_grid(draw, size, size, step, alpha=34)
    pad = max(4, size // 12)
    draw.rounded_rectangle((pad, pad, size - pad, size - pad), radius=max(3, size // 12), outline=(*GREEN, 210), width=max(1, size // 32), fill=(4, 18, 14, 80))
    # Simplified R4 mark for small sizes.
    f = font(max(12, round(size * 0.36)), True)
    draw.text((size * 0.22, size * 0.58), "R4", font=f, fill=TEXT + (255,), anchor="lm")
    wave = [(round(size * p[0]), round(size * p[1])) for p in [(0.18,0.40),(0.35,0.40),(0.43,0.30),(0.55,0.68),(0.65,0.48),(0.82,0.48)]]
    draw.line(wave, fill=MINT + (255,), width=max(2, size // 20))
    draw.line([(round(size*.11), round(size*.50)), (round(size*.22), round(size*.50))], fill=AMBER + (255,), width=max(2, size // 20))
    draw.line([(round(size*.78), round(size*.50)), (round(size*.90), round(size*.50))], fill=AMBER + (255,), width=max(2, size // 20))
    return img


def main() -> None:
    PUBLIC.mkdir(parents=True, exist_ok=True)
    social = generate_social()
    social.save(PUBLIC / "social-preview.png", optimize=True)
    social.resize((600, 315), Image.Resampling.LANCZOS).save(PUBLIC / "thumbnail.png", optimize=True)

    icon256 = generate_icon(256)
    icon256.convert("RGB").save(PUBLIC / "favicon.png", optimize=True)
    generate_icon(32).convert("RGB").save(PUBLIC / "favicon-32x32.png", optimize=True)
    generate_icon(180).convert("RGB").save(PUBLIC / "apple-touch-icon.png", optimize=True)
    sizes = [16, 24, 32, 48, 64, 128, 256]
    frames = [generate_icon(s).convert("RGBA") for s in sizes]
    frames[-1].save(PUBLIC / "favicon.ico", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
    print(f"wrote assets to {PUBLIC}")


if __name__ == "__main__":
    main()
