"""
Generates the package icon (icon.svg and icon.png).

The icon follows the one of GenHTTP: a purple, rounded square with a
condensed white letter, cut by a narrow vertical stencil slit through
its center. Here, the letter is a "V" for "virtual", split at its tip.

Requires Pillow: python3 icon.py
"""

from pathlib import Path

from PIL import Image, ImageDraw

SIZE = 128
SCALE = 8  # supersampling for anti-aliased edges

BACKGROUND = (170, 85, 255, 255)  # #AA55FF, as in the GenHTTP icon
FOREGROUND = (255, 255, 255, 255)
RADIUS = 14

# the glyph spans the same box as the "G" of GenHTTP
TOP, BOTTOM = 24.0, 103.5
LEFT, RIGHT = 35.0, 93.0
CENTER = (LEFT + RIGHT) / 2

STROKE = 17.0  # horizontal thickness of an arm at the top
FOOT = 10.0  # width of the flat bottom of the V
NOTCH = 18.0  # height of the stencil cut, measured from the bottom
SLIT = 4.0  # width of the stencil cut, as in the "G"

# the arms taper towards the bottom, so that their inner edges meet the
# stencil cut close to the tip - parallel edges would meet far above it
# and the cut would turn the tip into the stem of a "Y"
cut = CENTER - SLIT / 2

LEFT_ARM = [
    (LEFT, TOP),
    (LEFT + STROKE, TOP),
    (cut, BOTTOM - NOTCH),
    (cut, BOTTOM),
    (CENTER - FOOT / 2, BOTTOM),
]

# the right arm is the mirror image of the left one
RIGHT_ARM = [(2 * CENTER - x, y) for (x, y) in reversed(LEFT_ARM)]


def svg() -> str:
    def path(points):
        return "M " + " L ".join(f"{x:.2f} {y:.2f}" for x, y in points) + " Z"

    return f"""<svg xmlns="http://www.w3.org/2000/svg" width="{SIZE}" height="{SIZE}" viewBox="0 0 {SIZE} {SIZE}">
  <rect width="{SIZE}" height="{SIZE}" rx="{RADIUS}" fill="#AA55FF"/>
  <path d="{path(LEFT_ARM)}" fill="#FFFFFF"/>
  <path d="{path(RIGHT_ARM)}" fill="#FFFFFF"/>
</svg>
"""


def png() -> Image.Image:
    size = SIZE * SCALE

    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    draw.rounded_rectangle((0, 0, size - 1, size - 1), radius=RADIUS * SCALE, fill=BACKGROUND)

    for arm in (LEFT_ARM, RIGHT_ARM):
        draw.polygon([(x * SCALE, y * SCALE) for x, y in arm], fill=FOREGROUND)

    return image.resize((SIZE, SIZE), Image.LANCZOS)


if __name__ == "__main__":
    here = Path(__file__).parent

    (here / "icon.svg").write_text(svg())
    png().save(here / "icon.png", optimize=True)
