"""Generate original TurboTick Workshop artwork. Developer tool only: requires Pillow."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
import os
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
ABOUT = ROOT / "About"
ABOUT.mkdir(exist_ok=True)


def font(size, bold=False):
    names = (["segoeuib.ttf", "DejaVuSans-Bold.ttf"] if bold else ["segoeui.ttf", "DejaVuSans.ttf"])
    candidates = [Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts" / names[0],
                  Path("/usr/share/fonts/truetype/dejavu") / names[1]]
    for candidate in candidates:
        if candidate.exists():
            return ImageFont.truetype(str(candidate), size)
    return ImageFont.load_default(size=size)


def emblem(draw, cx, cy, radius):
    teal = (74, 220, 199)
    for dx, dy in [(-1, -0.6), (1, -0.6), (0, 1)]:
        x, y = cx + dx * radius, cy + dy * radius
        draw.line((cx, cy, x, y), fill=(58, 117, 139), width=max(2, int(radius / 13)))
        r = radius * 0.15
        draw.ellipse((x-r, y-r, x+r, y+r), fill=teal)
    r = radius * 0.57
    draw.ellipse((cx-r, cy-r, cx+r, cy+r), fill=(25, 58, 76), outline=teal, width=max(2, int(radius / 13)))
    draw.line((cx-r*0.45, cy, cx-r*0.05, cy+r*0.37, cx+r*0.48, cy-r*0.39), fill=(234, 247, 244), width=max(2, int(radius / 10)))


image = Image.new("RGB", (640, 360))
draw = ImageDraw.Draw(image)
for y in range(360):
    ratio = y / 359
    draw.line((0, y, 640, y), fill=(int(11+7*ratio), int(23+13*ratio), int(38+17*ratio)))
for x in range(32, 641, 32):
    draw.line((x, 0, x, 360), fill=(20, 37, 54))
for y in range(8, 360, 32):
    draw.line((0, y, 640, y), fill=(20, 37, 54))
draw.rounded_rectangle((30, 25, 196, 54), radius=5, fill=(33, 61, 78))
draw.text((42, 29), "RIMWORLD 1.7", font=font(15, True), fill=(117, 231, 213))
emblem(draw, 520, 161, 68)
draw.text((38, 88), "TurboTick", font=font(54, True), fill=(242, 248, 252))
draw.text((41, 163), "Dedicated multiplayer project", font=font(20), fill=(160, 188, 207))
draw.text((41, 212), "ROOMS  /  SPEED SYNC  /  SNAPSHOT CACHE", font=font(13, True), fill=(96, 218, 196))
draw.line((40, 272, 600, 272), fill=(48, 82, 100), width=1)
version = ET.parse(ROOT / "Directory.Build.props").findtext("./PropertyGroup/Version")
draw.text((41, 294), f"RELEASE {version}", font=font(16, True), fill=(237, 193, 113))
try:
    zh_font = ImageFont.truetype(str(Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts" / "msyh.ttc"), 13)
except Exception:
    zh_font = font(13)
text = "TurboTick  |  丿和光同尘"
w = zh_font.getlength(text)
draw.text((int(600 - w), 296), text, font=zh_font, fill=(141, 167, 186))
image.save(ABOUT / "Preview.png", optimize=True)
icon = Image.new("RGB", (256, 256), (15, 32, 49))
emblem(ImageDraw.Draw(icon), 128, 117, 86)
icon.resize((64, 64), Image.Resampling.LANCZOS).save(ABOUT / "ModIcon.png", optimize=True)
for name in ("Preview.png", "ModIcon.png"):
    path = ABOUT / name
    print(f"{name}: {Image.open(path).size}, {path.stat().st_size} bytes")
