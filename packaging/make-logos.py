"""Draws ATOLL's logo and brand assets. Run it after changing the design; the output is committed.

    python packaging/make-logos.py

Needs Python 3.10+ with Pillow, numpy and fontTools, and Noto Sans (SIL Open Font License)
installed. The SVGs carry no font references: the wordmark and taglines are outlines.

The mark is an atoll seen from above, in the shape of the island: a turquoise lagoon inside a
sand-white reef that three channels cut into islets, on deep ocean. Below 40 px a simplified
mark closes the channels and thickens the reef so it stays legible in the tray.

Writes:
  docs/assets/brand/logo.svg, logo-512.png       app icon
  docs/assets/brand/banner-dark.svg, -light.svg   README header
  docs/assets/brand/social-preview.png            1280 x 640, for GitHub's social preview
  packaging/identity/Assets/*.png, Atoll.ico      MSIX logos; exe, installer and tray icon
"""
from __future__ import annotations

import io
import math
import os
import struct
from pathlib import Path

import numpy as np
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.ttLib import TTFont
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parent.parent
BRAND = ROOT / "docs" / "assets" / "brand"
IDENTITY = ROOT / "packaging" / "identity" / "Assets"

OCEAN = [(0.0, "#11405F"), (1.0, "#06121D")]          # tile, top to bottom (slightly skewed)
LAGOON = [(0.0, "#7EF5E0"), (0.55, "#2FD3E6"), (1.0, "#1A9BF0")]
DEEP, DEEP_ALPHA = "#0A6FC9", 0.55                     # darker water in the middle of the lagoon
REEF = "#F7F3E8"
SHALLOWS, SHALLOWS_ALPHA = "#C8FFF4", 0.45            # pale water just inside the reef
SURF_ALPHA = 0.09
CORNER = 0.22                                          # tile corner radius, fraction of size


def geometry(small: bool) -> dict:
    """The mark on a 1024 grid: the reef's centre line is a stadium (half-width a, radius r), w wide.
    Islets are (start, end) along that line in percent, from top centre, clockwise."""
    if small:
        return dict(a=380, r=164, w=60, surf=0, shallows=0, islets=None)
    return dict(a=352, r=140, w=40, surf=44, shallows=96, islets=[(6, 33), (38, 72), (76, 98)])


def reef_point(g: dict, pct: float) -> tuple[float, float]:
    """A point on the reef's centre line, matching the SVG path in svg_mark()."""
    a, r = g["a"], g["r"]
    straight = 2 * (a - r)
    arc = math.pi * r
    s = (pct % 100) / 100 * (2 * straight + 2 * arc)
    segments = [
        (straight / 2, lambda t: (512 + t, 512 - r)),
        (arc, lambda t: (512 + a - r + r * math.sin(t / r), 512 - r * math.cos(t / r))),
        (straight, lambda t: (512 + a - r - t, 512 + r)),
        (arc, lambda t: (512 - a + r - r * math.sin(t / r), 512 + r * math.cos(t / r))),
        (straight / 2, lambda t: (512 - a + r + t, 512 - r)),
    ]
    for length, f in segments:
        if s <= length:
            return f(s)
        s -= length
    return segments[-1][1](segments[-1][0])


# ---------------------------------------------------------------------------------------------
# Raster drawing (supersampled masks)

def hex_rgb(h: str) -> tuple[int, int, int]:
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def ramp(n: int, stops, p0, p1, box=(0, 0, 1, 1)) -> Image.Image:
    """Linear gradient p0 -> p1, both in the box's own 0..1 units (SVG objectBoundingBox)."""
    ys, xs = np.mgrid[0:n, 0:n].astype(np.float32) / n
    u, v = (xs - box[0]) / (box[2] - box[0]), (ys - box[1]) / (box[3] - box[1])
    dx, dy = p1[0] - p0[0], p1[1] - p0[1]
    t = np.clip(((u - p0[0]) * dx + (v - p0[1]) * dy) / (dx * dx + dy * dy), 0, 1)
    offs = np.array([s[0] for s in stops], np.float32)
    cols = np.array([hex_rgb(s[1]) for s in stops], np.float32)
    rgb = np.stack([np.interp(t, offs, cols[:, c]) for c in range(3)], -1)
    return Image.fromarray(np.concatenate([rgb, np.full((n, n, 1), 255, np.float32)], -1).astype(np.uint8))


def solid(n: int, color: str, alpha: float = 1.0) -> Image.Image:
    return Image.new("RGBA", (n, n), hex_rgb(color) + (round(255 * alpha),))


def paint(img: Image.Image, fill: Image.Image, mask: Image.Image, alpha: float = 1.0):
    """Composite fill over img through mask (straight alpha, so edges keep their colour)."""
    a = np.asarray(mask, np.float32) * np.asarray(fill.getchannel("A"), np.float32) / 255 * alpha
    layer = fill.copy()
    layer.putalpha(Image.fromarray(np.rint(a).astype(np.uint8)))
    img.alpha_composite(layer)


def stadium_mask(n: int, k: float, half_w: float, half_h: float) -> Image.Image:
    m = Image.new("L", (n, n), 0)
    ImageDraw.Draw(m).rounded_rectangle([(512 - half_w) * k, (512 - half_h) * k, (512 + half_w) * k,
                                         (512 + half_h) * k], radius=half_h * k, fill=255)
    return m


def draw_mark(size: int, small: bool | None = None) -> Image.Image:
    small = size < 40 if small is None else small
    g = geometry(small)
    ss = 4 if size >= 256 else 8
    n = size * ss
    k = n / 1024
    a, r, w = g["a"], g["r"], g["w"]
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))

    tile = Image.new("L", (n, n), 0)
    ImageDraw.Draw(tile).rounded_rectangle([0, 0, n - 1, n - 1], radius=CORNER * n, fill=255)
    paint(img, ramp(n, OCEAN, (0, 0), (0.4, 1)), tile)

    if g["surf"]:  # waves breaking on the outside of the reef
        surf = stadium_mask(n, k, a + (w + g["surf"]) / 2, r + (w + g["surf"]) / 2)
        paint(img, solid(n, "#FFFFFF"), surf, SURF_ALPHA)

    lagoon = stadium_mask(n, k, a, r)
    box = ((512 - a) / 1024, (512 - r) / 1024, (512 + a) / 1024, (512 + r) / 1024)
    paint(img, ramp(n, LAGOON, (0.1, 0.2), (0.9, 0.8), box), lagoon)
    ys, xs = np.mgrid[0:n, 0:n].astype(np.float32) / n
    u, v = (xs - box[0]) / (box[2] - box[0]), (ys - box[1]) / (box[3] - box[1])
    deep = np.clip(1 - np.hypot(u - 0.55, v - 0.55) / 0.6, 0, 1) * np.asarray(lagoon, np.float32) * DEEP_ALPHA
    paint(img, solid(n, DEEP), Image.fromarray(deep.astype(np.uint8)))
    if g["shallows"]:
        sh = g["shallows"] / 2
        band = np.asarray(lagoon, np.float32) * (1 - np.asarray(stadium_mask(n, k, a - sh, r - sh), np.float32) / 255)
        band = Image.fromarray(band.astype(np.uint8)).filter(ImageFilter.GaussianBlur(10 * k))
        band = Image.fromarray((np.asarray(band, np.float32) * np.asarray(lagoon, np.float32) / 255).astype(np.uint8))
        paint(img, solid(n, SHALLOWS), band, SHALLOWS_ALPHA)

    reef = Image.new("L", (n, n), 0)
    d = ImageDraw.Draw(reef)
    if g["islets"]:  # round-ended islets: stamp a disc along each stretch of the centre line
        step = 0.4 / k * 100 / (2 * (2 * (a - r)) + 2 * math.pi * r)
        rad = w / 2 * k
        for start, end in g["islets"]:
            p = start
            while p <= end:
                x, y = reef_point(g, p)
                d.ellipse([x * k - rad, y * k - rad, x * k + rad, y * k + rad], fill=255)
                p += step
    else:
        outer = stadium_mask(n, k, a + w / 2, r + w / 2)
        inner = stadium_mask(n, k, a - w / 2, r - w / 2)
        reef = Image.fromarray(np.clip(np.asarray(outer, np.int16) - np.asarray(inner, np.int16), 0, 255).astype(np.uint8))
    paint(img, solid(n, REEF), reef)
    return img.resize((size, size), Image.Resampling.LANCZOS)


def write_ico(path: Path, sizes: list[int]):
    blobs = []
    for s in sizes:
        buf = io.BytesIO()
        draw_mark(s).save(buf, "PNG")
        blobs.append((s, buf.getvalue()))
    offset = 6 + 16 * len(blobs)
    entries, data = b"", b""
    for s, png in blobs:  # PNG-compressed entries (Windows Vista and later)
        dim = 0 if s >= 256 else s
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(png), offset + len(data))
        data += png
    path.write_bytes(struct.pack("<HHH", 0, 1, len(blobs)) + entries + data)


# ---------------------------------------------------------------------------------------------
# SVG

def svg_stops(stops) -> str:
    return "".join(f'<stop offset="{o}" stop-color="{c}"/>' for o, c in stops)


def svg_mark(x: float, y: float, size: float, uid: str) -> tuple[str, str]:
    """(defs, body) for the app icon drawn at (x, y), size px square."""
    g = geometry(False)
    a, r, w = g["a"], g["r"], g["w"]
    L, R, T, B = 512 - a + r, 512 + a - r, 512 - r, 512 + r
    stad = f"M512 {T}H{R}A{r} {r} 0 0 1 {R} {B}H{L}A{r} {r} 0 0 1 {L} {T}Z"
    isl = g["islets"]
    dashes = []
    for i, (s, e) in enumerate(isl):
        nxt = isl[(i + 1) % len(isl)][0] + (100 if i == len(isl) - 1 else 0)
        dashes += [e - s, nxt - e]
    defs = (f'<linearGradient id="{uid}o" x1="0" y1="0" x2="0.4" y2="1">{svg_stops(OCEAN)}</linearGradient>'
            f'<linearGradient id="{uid}l" x1="0.1" y1="0.2" x2="0.9" y2="0.8">{svg_stops(LAGOON)}</linearGradient>'
            f'<radialGradient id="{uid}d" cx="0.55" cy="0.55" r="0.6"><stop offset="0" stop-color="{DEEP}" '
            f'stop-opacity="{DEEP_ALPHA}"/><stop offset="1" stop-color="{DEEP}" stop-opacity="0"/></radialGradient>'
            f'<clipPath id="{uid}c"><path d="{stad}"/></clipPath>'
            f'<filter id="{uid}f" x="-0.1" y="-0.3" width="1.2" height="1.6"><feGaussianBlur stdDeviation="10"/></filter>')
    body = (f'<g transform="translate({x} {y}) scale({size / 1024})">'
            f'<rect width="1024" height="1024" rx="{CORNER * 1024:g}" fill="url(#{uid}o)"/>'
            f'<path d="{stad}" fill="none" stroke="#FFFFFF" stroke-opacity="{SURF_ALPHA}" stroke-width="{w + g["surf"]}"/>'
            f'<path d="{stad}" fill="url(#{uid}l)"/><path d="{stad}" fill="url(#{uid}d)"/>'
            f'<g clip-path="url(#{uid}c)"><path d="{stad}" fill="none" stroke="{SHALLOWS}" stroke-opacity="{SHALLOWS_ALPHA}" '
            f'stroke-width="{g["shallows"]}" filter="url(#{uid}f)"/></g>'
            f'<path d="{stad}" pathLength="100" fill="none" stroke="{REEF}" stroke-width="{w}" stroke-linecap="round" '
            f'stroke-dasharray="{" ".join(f"{v:g}" for v in dashes)}" stroke-dashoffset="{-isl[0][0]}"/></g>')
    return defs, body


def svg_icon() -> str:
    defs, body = svg_mark(0, 0, 1024, "a")
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024" width="1024" height="1024" '
            f'role="img" aria-label="ATOLL"><title>ATOLL</title><defs>{defs}</defs>{body}</svg>\n')


def find_font(name: str) -> Path:
    for d in (Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts",
              Path(os.environ.get("LOCALAPPDATA", "")) / "Microsoft/Windows/Fonts",
              Path("/usr/share/fonts"), Path.home() / ".fonts"):
        hits = list(d.rglob(name)) if d.exists() else []
        if hits:
            return hits[0]
    raise SystemExit(f"Font {name} not found. Install Noto Sans (https://fonts.google.com/noto/specimen/Noto+Sans).")


class Outliner:
    """Text as SVG path data (no kerning; tracking is set per line)."""

    def __init__(self, file: str):
        self.file = find_font(file)
        self.font = TTFont(self.file)
        self.glyphs = self.font.getGlyphSet()
        self.cmap = self.font.getBestCmap()
        self.upm = self.font["head"].unitsPerEm

    def path(self, text: str, x: float, baseline: float, size: float, tracking: float = 0) -> str:
        s = size / self.upm
        pen = SVGPathPen(self.glyphs, lambda v: f"{v:.1f}".rstrip("0").rstrip("."))
        for c in text:
            gname = self.cmap[ord(c)]
            self.glyphs[gname].draw(TransformPen(pen, (s, 0, 0, -s, x, baseline)))
            x += self.glyphs[gname].width * s + tracking * size
        return pen.getCommands()


THEMES = {
    "dark": dict(bg="#0B0C0E", fg="#F2F2F4", sub="#A3A8B0", dim="#6E747D", screen0="#11405F", screen1="#0A1A28",
                 frame="#24282E", win="#0E1116", winline="#20252C"),
    "light": dict(bg="#F5F5F7", fg="#111214", sub="#4A4F57", dim="#80858D", screen0="#9EDCEB", screen1="#DCEBF5",
                  frame="#D5D8DE", win="#FBFBFD", winline="#E3E6EA"),
}


def svg_banner(theme: str, bold: Outliner, regular: Outliner) -> str:
    t = THEMES[theme]
    W, H = 1280, 400
    mdefs, mbody = svg_mark(80, 56, 112, "b")
    word = bold.path("ATOLL", 78, 256, 84, tracking=0.08)
    tag = regular.path("A Dynamic Island for Windows", 81, 302, 30)
    sub1 = regular.path("Music, timers, coding agents, notifications, calendar", 82, 340, 19)
    sub2 = regular.path("and weather, in a pill at the top of your screen.", 82, 366, 19)

    # The illustration: a desktop's top edge, the island open on a now-playing panel, two side bubbles.
    sx, sy, sw, sh = 640, 40, 576, 320
    pill_w, pill_h = 404, 188
    px, py = sx + (sw - pill_w) / 2, sy + 22
    bars = []
    heights = [14, 26, 18, 34, 22, 30, 12, 24, 16]
    for i, h in enumerate(heights):
        x = px + pill_w - 34 - (len(heights) - i) * 9
        bars.append(f'<rect class="v v{i % 4}" x="{x:.1f}" y="{py + 56 - h / 2:.1f}" width="5" height="{h}" rx="2.5" '
                    f'fill="url(#acc)" style="transform-origin:{x + 2.5:.1f}px {py + 56:.1f}px"/>')
    art = f'<rect x="{px + 24}" y="{py + 24}" width="64" height="64" rx="14" fill="url(#art)"/>'
    lines = (f'<rect x="{px + 104}" y="{py + 34}" width="132" height="12" rx="6" fill="#F2F2F4"/>'
             f'<rect x="{px + 104}" y="{py + 58}" width="88" height="10" rx="5" fill="#8E949C"/>')
    prog_y, frac = py + 116, 0.42
    progress = (f'<rect x="{px + 24}" y="{prog_y}" width="{pill_w - 48}" height="6" rx="3" fill="#2A2E35"/>'
                f'<rect x="{px + 24}" y="{prog_y}" width="{(pill_w - 48) * frac:.0f}" height="6" rx="3" fill="url(#acc)"/>'
                f'<circle cx="{px + 24 + (pill_w - 48) * frac:.0f}" cy="{prog_y + 3}" r="7" fill="#F2F2F4"/>')
    cy_, mid = py + 156, px + pill_w / 2
    controls = (f'<path d="M{mid - 70} {cy_ - 9}v18l-14-9zM{mid - 84} {cy_ - 9}v18l-14-9z" fill="#F2F2F4"/>'
                f'<rect x="{mid - 11}" y="{cy_ - 11}" width="7" height="22" rx="2" fill="#F2F2F4"/>'
                f'<rect x="{mid + 4}" y="{cy_ - 11}" width="7" height="22" rx="2" fill="#F2F2F4"/>'
                f'<path d="M{mid + 70} {cy_ - 9}v18l14-9zM{mid + 84} {cy_ - 9}v18l14-9z" fill="#F2F2F4"/>')
    bl, br, by = px - 50, px + pill_w + 50, py + 22
    bubbles = (f'<circle cx="{bl}" cy="{by}" r="22" fill="#0B0C0E"/>'
               f'<circle cx="{bl}" cy="{by}" r="11" fill="none" stroke="#3A3F47" stroke-width="4"/>'
               f'<path d="M{bl} {by - 11}A11 11 0 1 1 {bl - 10.5} {by + 3.4}" fill="none" stroke="#FF9F0A" '
               f'stroke-width="4" stroke-linecap="round"/>'
               f'<circle cx="{br}" cy="{by}" r="22" fill="#0B0C0E"/>'
               f'<circle class="pulse" cx="{br}" cy="{by}" r="7" fill="#30D158"/>')
    wx, wy, ww = sx + 70, sy + 236, sw - 140
    window = (f'<rect x="{wx}" y="{wy}" width="{ww}" height="140" rx="10" fill="{t['win']}" stroke="{t['frame']}"/>'
              f'<rect x="{wx + 18}" y="{wy + 16}" width="96" height="8" rx="4" fill="{t['winline']}"/>'
              + "".join(f'<rect x="{wx + ww - 76 + i * 22}" y="{wy + 14}" width="12" height="12" rx="3" fill="{t['winline']}"/>'
                        for i in range(3))
              + f'<rect x="{wx}" y="{wy + 38}" width="{ww}" height="1" fill="{t['frame']}"/>'
              f'<rect x="{wx + 18}" y="{wy + 56}" width="180" height="8" rx="4" fill="{t['winline']}"/>'
              f'<rect x="{wx + 18}" y="{wy + 74}" width="128" height="8" rx="4" fill="{t['winline']}"/>')
    style = ("<style>"
             ".v{animation:eq 1.1s ease-in-out infinite alternate}"
             ".v1{animation-delay:-.3s}.v2{animation-delay:-.6s}.v3{animation-delay:-.85s}"
             "@keyframes eq{from{transform:scaleY(.35)}to{transform:scaleY(1)}}"
             ".pulse{animation:pulse 1.6s ease-in-out infinite}"
             "@keyframes pulse{50%{opacity:.35}}"
             "@media (prefers-reduced-motion:reduce){.v,.pulse{animation:none}}"
             "</style>")
    return f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" role="img" aria-label="ATOLL: a Dynamic Island for Windows">
  <title>ATOLL</title>
  <defs>
    {mdefs}
    <linearGradient id="screen" x1="0" y1="0" x2="0.3" y2="1"><stop offset="0" stop-color="{t['screen0']}"/><stop offset="1" stop-color="{t['screen1']}"/></linearGradient>
    <linearGradient id="acc" x1="0" y1="0" x2="1" y2="0">{svg_stops(LAGOON)}</linearGradient>
    <linearGradient id="art" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#FF8A6B"/><stop offset="0.55" stop-color="#C46BFF"/><stop offset="1" stop-color="#4C6BFF"/></linearGradient>
    <clipPath id="scr"><rect x="{sx}" y="{sy}" width="{sw}" height="{sh}" rx="16"/></clipPath>
  </defs>
  {style}
  <rect width="{W}" height="{H}" rx="24" fill="{t['bg']}"/>
  {mbody}
  <path fill="{t['fg']}" d="{word}"/>
  <path fill="{t['sub']}" d="{tag}"/>
  <path fill="{t['dim']}" d="{sub1}"/>
  <path fill="{t['dim']}" d="{sub2}"/>
  <g clip-path="url(#scr)">
    <rect x="{sx}" y="{sy}" width="{sw}" height="{sh}" fill="url(#screen)"/>
    {window}
    <rect x="{px}" y="{py}" width="{pill_w}" height="{pill_h}" rx="44" fill="#0B0C0E"/>
    {art}{lines}{''.join(bars)}{progress}{controls}{bubbles}
  </g>
  <rect x="{sx + 0.5}" y="{sy + 0.5}" width="{sw - 1}" height="{sh - 1}" rx="16" fill="none" stroke="{t['frame']}"/>
</svg>
"""


def social_preview(bold_path: Path, regular_path: Path) -> Image.Image:
    W, H = 1280, 640
    img = Image.new("RGBA", (W, H), hex_rgb("#0B0C0E") + (255,))
    glow = Image.new("L", (W, H), 0)
    ImageDraw.Draw(glow).ellipse([W / 2 - 520, -380, W / 2 + 520, 300], fill=80)
    glow = glow.filter(ImageFilter.GaussianBlur(120))
    img.paste(Image.new("RGBA", (W, H), hex_rgb("#11405F") + (255,)), (0, 0), glow)
    img.alpha_composite(draw_mark(240), ((W - 240) // 2, 80))
    d = ImageDraw.Draw(img)
    word, f_word = "ATOLL", ImageFont.truetype(str(bold_path), 104)
    track = 0.08 * 104
    total = sum(d.textlength(c, font=f_word) for c in word) + track * (len(word) - 1)
    x = (W - total) / 2
    for c in word:
        d.text((x, 420), c, font=f_word, fill="#F2F2F4", anchor="ls")
        x += d.textlength(c, font=f_word) + track
    d.text((W / 2, 500), "A Dynamic Island for Windows", font=ImageFont.truetype(str(regular_path), 38),
           fill="#A3A8B0", anchor="ms")
    return img


def main():
    BRAND.mkdir(parents=True, exist_ok=True)
    IDENTITY.mkdir(parents=True, exist_ok=True)
    bold, regular = Outliner("NotoSans-Bold.ttf"), Outliner("NotoSans-Regular.ttf")

    (BRAND / "logo.svg").write_text(svg_icon(), encoding="utf-8")
    for theme in THEMES:
        (BRAND / f"banner-{theme}.svg").write_text(svg_banner(theme, bold, regular), encoding="utf-8")
    draw_mark(512).save(BRAND / "logo-512.png", optimize=True)
    social_preview(bold.file, regular.file).convert("RGB").save(BRAND / "social-preview.png", optimize=True)

    for name, size in (("StoreLogo.png", 50), ("Square44x44Logo.png", 44), ("Square150x150Logo.png", 150)):
        draw_mark(size).save(IDENTITY / name, optimize=True)
    write_ico(IDENTITY / "Atoll.ico", [16, 20, 24, 32, 40, 48, 64, 256])
    print(f"Brand assets written to {BRAND} and {IDENTITY}")


if __name__ == "__main__":
    main()
