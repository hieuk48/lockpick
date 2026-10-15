"""Draws Lockpick's icon (a pick set: hook, rake, tension wrench), writes the Windows icon and PNGs, and the README
banner (icon + wordmark in Special Elite + tagline).

    python tools/branding/make_icon.py            (needs Pillow)

Geometry is on a 64-unit grid, the same as the branding board (variant C). Sizes of 32 px and up get the full set
(handles, thin shafts, the hook, the rake's teeth, the wrench's bend); 16 and 24 px get three plain picks, which stay
crisp in the taskbar. Colours: palette "Status screen" (ECG green on the dark ground).
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "src" / "Lockpick.App" / "Assets"
GROUND = (11, 15, 14, 255)       # #0B0F0E
RIM = (39, 50, 47, 255)          # #27322F
GREEN = (78, 195, 109, 255)      # #4EC36D
MUTED = (143, 163, 155, 255)     # #8FA39B
FONT = ASSETS / "Fonts" / "SpecialElite-Regular.ttf"

FULL = [  # (points, width) in 64ths
    ([(18, 58), (18, 42)], 6), ([(18, 42), (18, 12)], 2.6), ([(18, 12), (18.4, 9.4), (20, 8.4), (21.6, 9.0), (22, 11)], 2.6),
    ([(32, 58), (32, 42)], 6), ([(32, 42), (32, 22), (35, 18), (32, 14), (35, 10), (32, 6)], 2.6),
    ([(46, 58), (46, 42)], 6), ([(46, 42), (46, 14), (41, 14)], 2.6),
]
SIMPLE = [([(17, 56), (17, 9)], 9), ([(32, 56), (32, 7)], 9), ([(47, 56), (47, 14), (41, 14)], 9)]


def stroke(draw: ImageDraw.ImageDraw, points, width: float, scale: float) -> None:
    pts = [(x * scale, y * scale) for x, y in points]
    w = max(1, round(width * scale))
    draw.line(pts, fill=GREEN, width=w, joint="curve")
    for x, y in (pts[0], pts[-1]):          # round caps
        r = w / 2
        draw.ellipse((x - r, y - r, x + r, y + r), fill=GREEN)


def render(size: int) -> Image.Image:
    # Draw large and scale down for smooth edges.
    big = 1024 if size >= 32 else size * 16
    scale = big / 64
    image = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    radius = round(big * 13 / 64)
    draw.rounded_rectangle((0, 0, big - 1, big - 1), radius=radius, fill=GROUND, outline=RIM, width=max(1, big // 64))
    for points, width in FULL if size >= 32 else SIMPLE:
        stroke(draw, points, width, scale)
    return image.resize((size, size), Image.LANCZOS)


def banner() -> Image.Image:
    width, height = 1280, 320
    image = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle((0, 0, width - 1, height - 1), radius=28, fill=GROUND, outline=RIM, width=2)
    icon = render(208)
    image.paste(icon, (64, (height - 208) // 2), icon)
    wordmark = ImageFont.truetype(str(FONT), 132)
    tagline = ImageFont.truetype(str(FONT), 40)
    x = 64 + 208 + 56
    draw.text((x, 70), "LOCKPICK", font=wordmark, fill=GREEN)
    draw.text((x + 6, 214), "Master of Unlocking.", font=tagline, fill=MUTED)
    return image


def main() -> None:
    sizes = [16, 24, 32, 48, 64, 128, 256]
    images = [render(s) for s in sizes]
    ASSETS.mkdir(parents=True, exist_ok=True)
    images[-1].save(ASSETS / "lockpick.ico", sizes=[(s, s) for s in sizes], append_images=images[:-1])
    images[-1].save(ASSETS / "lockpick-256.png")
    render(512).save(ROOT / "docs" / "lockpick-icon.png")
    banner().save(ROOT / "docs" / "lockpick-banner.png")
    print("wrote", ASSETS / "lockpick.ico", ASSETS / "lockpick-256.png", ROOT / "docs" / "lockpick-icon.png",
          ROOT / "docs" / "lockpick-banner.png")


if __name__ == "__main__":
    main()
