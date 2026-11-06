"""Draws ATOLL's logo and brand assets. Run it after changing the design; the output is committed.

    python packaging/make-logos.py

Needs Python 3.10+ with Pillow, numpy and fontTools, and Noto Sans (SIL Open Font License)
installed. The SVGs carry no font references: the wordmark and taglines are outlines.

The mark: the island sits at the top edge of the tile, where it sits on a display, and ripples
spread down from it. Each ripple is wider apart, thinner and rounder than the last, the way a
ring of water relaxes from the shape of what made it toward a circle; the tile edge crops them.
One ink on warm paper, so it holds up printed in a single colour. Below 40 px a simplified mark
keeps the island and one ripple so it stays legible in the tray.

Writes:
  docs/assets/brand/logo.svg, logo-512.png       app icon
  docs/assets/brand/banner-dark.svg, -light.svg   README header
  docs/assets/brand/social-preview.png            1280 x 640, for GitHub's social preview
  packaging/identity/Assets/*.png, Atoll.ico      MSIX logos; exe, installer and tray icon
  src/Atoll.App/Theme/Logo.xaml                   the same mark as vector WPF drawings
"""
from __future__ import annotations

import io
import os
import struct
from pathlib import Path

import numpy as np
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.ttLib import TTFont
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
BRAND = ROOT / "docs" / "assets" / "brand"
IDENTITY = ROOT / "packaging" / "identity" / "Assets"
XAML = ROOT / "src" / "Atoll.App" / "Theme" / "Logo.xaml"

PAPER = "#F1EEE7"   # warm off-white: reads as a printed mark, not a screen glow
INK = "#0B0C0E"     # the island's own near-black (DESIGN.md)
ACCENT = "#0A84FF"  # the app's default accent, used only inside the banner's illustration
CORNER = 0.22       # tile corner radius, fraction of the side


def geometry(small: bool) -> dict:
    """The mark on a 1024 grid: the island (width, height, top) and its ripples as
    (distance from the island's edge, vertical stretch, stroke width)."""
    if small:
        return dict(pill=(600, 220, 120), rings=[(120, 1.0, 78)])
    return dict(pill=(430, 148, 70), rings=[(92, 1.0, 42), (232, 1.35, 30), (430, 1.62, 20)])


def shapes(small: bool):
    """Yields ("ring", x, y, w, h, radius, stroke) and ("pill", x, y, w, h, radius, 0) in drawing order."""
    g = geometry(small)
    pw, ph, top = g["pill"]
    cx, cy = 512, top + ph / 2
    for d, k, w in g["rings"]:
        ax, ay = pw / 2 + d, ph / 2 + d * k
        yield ("ring", cx - ax, cy - ay, 2 * ax, 2 * ay, min(ax, ay), w)
    yield ("pill", cx - pw / 2, top, pw, ph, ph / 2, 0)


# ---------------------------------------------------------------------------------------------
# Raster (supersampled masks)

def hex_rgb(h: str) -> tuple[int, int, int]:
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def _rounded(draw: ImageDraw.ImageDraw, k: float, x, y, w, h, r, fill):
    if w <= 0 or h <= 0:
        return
    draw.rounded_rectangle([x * k, y * k, (x + w) * k, (y + h) * k], radius=max(0, r) * k, fill=fill)


def draw_mark(size: int, small: bool | None = None) -> Image.Image:
    small = size < 40 if small is None else small
    ss = 4 if size >= 256 else 8
    n = size * ss
    k = n / 1024
    tile = Image.new("L", (n, n), 0)
    ImageDraw.Draw(tile).rounded_rectangle([0, 0, n - 1, n - 1], radius=CORNER * n, fill=255)
    ink = np.zeros((n, n), np.uint8)
    for kind, x, y, w, h, r, stroke in shapes(small):
        layer = Image.new("L", (n, n), 0)
        d = ImageDraw.Draw(layer)
        if kind == "ring":  # stroke centred on the outline, like the SVG
            s = stroke / 2
            _rounded(d, k, x - s, y - s, w + stroke, h + stroke, r + s, 255)
            _rounded(d, k, x + s, y + s, w - stroke, h - stroke, r - s, 0)
        else:
            _rounded(d, k, x, y, w, h, r, 255)
        ink = np.maximum(ink, np.asarray(layer))
    t = np.asarray(tile, np.float32) / 255
    rgba = np.zeros((n, n, 4), np.float32)
    on = ink.astype(np.float32) / 255
    rgba[..., :3] = np.array(hex_rgb(PAPER), np.float32) * (1 - on[..., None]) + np.array(hex_rgb(INK), np.float32) * on[..., None]
    rgba[..., 3] = 255 * t
    img = Image.fromarray(rgba.astype(np.uint8))
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
# SVG and XAML

def svg_mark(x: float, y: float, size: float, uid: str, small: bool = False, edge: str | None = None) -> tuple[str, str]:
    """(defs, body) for the app icon drawn at (x, y), size px square. `edge` outlines the tile
    with a hairline, for light backgrounds where the paper would otherwise disappear."""
    r = CORNER * 1024
    defs = f'<clipPath id="{uid}t"><rect width="1024" height="1024" rx="{r:g}"/></clipPath>'
    parts = []
    for kind, sx, sy, w, h, rad, stroke in shapes(small):
        geo = f'x="{sx:g}" y="{sy:g}" width="{w:g}" height="{h:g}" rx="{rad:g}"'
        parts.append(f'<rect {geo} fill="none" stroke="{INK}" stroke-width="{stroke:g}"/>' if kind == "ring"
                     else f'<rect {geo} fill="{INK}"/>')
    hair = 1.2 * 1024 / size  # about 1.2 px at the drawn size
    outline = (f'<rect x="{hair / 2:g}" y="{hair / 2:g}" width="{1024 - hair:g}" height="{1024 - hair:g}" rx="{r - hair / 2:g}" '
               f'fill="none" stroke="{edge}" stroke-width="{hair:g}"/>') if edge else ""
    body = (f'<g transform="translate({x:g} {y:g}) scale({size / 1024:g})"><g clip-path="url(#{uid}t)">'
            f'<rect width="1024" height="1024" fill="{PAPER}"/>{"".join(parts)}</g>{outline}</g>')
    return defs, body


def svg_icon() -> str:
    defs, body = svg_mark(0, 0, 1024, "a")
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024" width="1024" height="1024" '
            f'role="img" aria-label="ATOLL"><title>ATOLL</title><defs>{defs}</defs>{body}</svg>\n')


def xaml_logo() -> str:
    r = CORNER * 1024

    def drawing(key: str, small: bool) -> str:
        items = []
        for kind, x, y, w, h, rad, stroke in shapes(small):
            geo = f'<RectangleGeometry Rect="{x:g},{y:g},{w:g},{h:g}" RadiusX="{rad:g}" RadiusY="{rad:g}" />'
            if kind == "ring":
                items.append(f'                <GeometryDrawing><GeometryDrawing.Pen><Pen Brush="{INK}" Thickness="{stroke:g}" /></GeometryDrawing.Pen>'
                             f'<GeometryDrawing.Geometry>{geo}</GeometryDrawing.Geometry></GeometryDrawing>')
            else:
                items.append(f'                <GeometryDrawing Brush="{INK}"><GeometryDrawing.Geometry>{geo}</GeometryDrawing.Geometry></GeometryDrawing>')
        return f'''    <DrawingImage x:Key="{key}">
        <DrawingImage.Drawing>
            <DrawingGroup>
                <DrawingGroup.ClipGeometry><RectangleGeometry Rect="0,0,1024,1024" RadiusX="{r:g}" RadiusY="{r:g}" /></DrawingGroup.ClipGeometry>
                <GeometryDrawing Brush="{PAPER}"><GeometryDrawing.Geometry><RectangleGeometry Rect="0,0,1024,1024" /></GeometryDrawing.Geometry></GeometryDrawing>
{chr(10).join(items)}
            </DrawingGroup>
        </DrawingImage.Drawing>
    </DrawingImage>'''

    return f'''<!-- Generated by packaging/make-logos.py; edit the script, not this file. -->
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- The app icon, for 40 px and up. -->
{drawing("Atoll.Logo", False)}
    <!-- The simplified mark, for smaller sizes. -->
{drawing("Atoll.LogoSmall", True)}
</ResourceDictionary>
'''


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
    "dark": dict(bg="#0B0C0E", fg="#F2F2F4", sub="#A3A8B0", dim="#7C828B", screen="#1A1F24",
                 frame="#2A2E35", win="#0E1116", winline="#262B32"),
    "light": dict(bg="#F7F6F2", edge="#C9C4BA", fg="#111214", sub="#3F434A", dim="#62666D", screen="#DCE1E5",
                  frame="#CFCBC2", win="#FBFAF7", winline="#E4E1DA"),
}


def svg_banner(theme: str, bold: Outliner, regular: Outliner) -> str:
    t = THEMES[theme]
    W, H = 1280, 400
    mdefs, mbody = svg_mark(80, 56, 112, "b", edge=t.get("edge"))
    word = bold.path("ATOLL", 78, 256, 84, tracking=0.04)
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
                    f'fill="{ACCENT}" style="transform-origin:{x + 2.5:.1f}px {py + 56:.1f}px"/>')
    # Album art: a flat colour block with a record, not a gradient swatch.
    art = (f'<rect x="{px + 24}" y="{py + 24}" width="64" height="64" rx="14" fill="#E8743B"/>'
           f'<circle cx="{px + 56}" cy="{py + 56}" r="20" fill="#0B0C0E"/><circle cx="{px + 56}" cy="{py + 56}" r="5" fill="#E8743B"/>')
    lines = (f'<rect x="{px + 104}" y="{py + 34}" width="132" height="12" rx="6" fill="#F2F2F4"/>'
             f'<rect x="{px + 104}" y="{py + 58}" width="88" height="10" rx="5" fill="#8E949C"/>')
    prog_y, frac = py + 116, 0.42
    progress = (f'<rect x="{px + 24}" y="{prog_y}" width="{pill_w - 48}" height="6" rx="3" fill="#2A2E35"/>'
                f'<rect x="{px + 24}" y="{prog_y}" width="{(pill_w - 48) * frac:.0f}" height="6" rx="3" fill="{ACCENT}"/>'
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
               f'<circle cx="{br}" cy="{by}" r="7" fill="#30D158"/>')
    wx, wy, ww = sx + 70, sy + 236, sw - 140
    window = (f'<rect x="{wx}" y="{wy}" width="{ww}" height="140" rx="10" fill="{t['win']}" stroke="{t['frame']}"/>'
              f'<rect x="{wx + 18}" y="{wy + 16}" width="96" height="8" rx="4" fill="{t['winline']}"/>'
              + "".join(f'<rect x="{wx + ww - 76 + i * 22}" y="{wy + 14}" width="12" height="12" rx="3" fill="{t['winline']}"/>'
                        for i in range(3))
              + f'<rect x="{wx}" y="{wy + 38}" width="{ww}" height="1" fill="{t['frame']}"/>'
              f'<rect x="{wx + 18}" y="{wy + 56}" width="180" height="8" rx="4" fill="{t['winline']}"/>'
              f'<rect x="{wx + 18}" y="{wy + 74}" width="128" height="8" rx="4" fill="{t['winline']}"/>')
    # Only the visualizer moves (it is the one thing that moves in the real island), and not at all
    # for people who ask for reduced motion.
    style = ("<style>"
             ".v{animation:eq 1.1s ease-in-out infinite alternate}"
             ".v1{animation-delay:-.3s}.v2{animation-delay:-.6s}.v3{animation-delay:-.85s}"
             "@keyframes eq{from{transform:scaleY(.35)}to{transform:scaleY(1)}}"
             "@media (prefers-reduced-motion:reduce){.v{animation:none}}"
             "</style>")
    return f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" role="img" aria-label="ATOLL: a Dynamic Island for Windows">
  <title>ATOLL</title>
  <defs>
    {mdefs}
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
    <rect x="{sx}" y="{sy}" width="{sw}" height="{sh}" fill="{t['screen']}"/>
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
    img.alpha_composite(draw_mark(240), ((W - 240) // 2, 80))
    d = ImageDraw.Draw(img)
    word, f_word = "ATOLL", ImageFont.truetype(str(bold_path), 104)
    track = 0.04 * 104
    total = sum(d.textlength(c, font=f_word) for c in word) + track * (len(word) - 1)
    x = (W - total) / 2
    for c in word:
        d.text((x, 440), c, font=f_word, fill="#F2F2F4", anchor="ls")
        x += d.textlength(c, font=f_word) + track
    d.text((W / 2, 516), "A Dynamic Island for Windows", font=ImageFont.truetype(str(regular_path), 38),
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
    XAML.write_text(xaml_logo(), encoding="utf-8")
    print(f"Brand assets written to {BRAND}, {IDENTITY} and {XAML}")


if __name__ == "__main__":
    main()
