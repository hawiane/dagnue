from pathlib import Path
from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "Nexus.App" / "Assets"
SCALE = 4
CANVAS = 256 * SCALE


def scaled(value: float) -> int:
    return round(value * SCALE)


def make_mark() -> Image.Image:
    image = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image, "RGBA")

    # Dark armored tile with a cyan perimeter that remains distinct on the taskbar.
    draw.rounded_rectangle(
        (scaled(10), scaled(10), scaled(246), scaled(246)),
        radius=scaled(48), fill=(4, 15, 22, 255), outline=(0, 224, 245, 235), width=scaled(7)
    )
    draw.rounded_rectangle(
        (scaled(21), scaled(21), scaled(235), scaled(235)),
        radius=scaled(40), outline=(0, 105, 130, 220), width=scaled(3)
    )

    # A compact D-shaped neural circuit: strong silhouette first, fine telemetry second.
    cyan = (0, 229, 255, 255)
    pale = (174, 248, 255, 255)
    draw.line((scaled(67), scaled(55), scaled(67), scaled(201)), fill=cyan, width=scaled(13))
    draw.arc((scaled(39), scaled(48), scaled(209), scaled(208)), -88, 88, fill=cyan, width=scaled(13))
    draw.line((scaled(67), scaled(55), scaled(128), scaled(55)), fill=cyan, width=scaled(13))
    draw.line((scaled(67), scaled(201), scaled(128), scaled(201)), fill=cyan, width=scaled(13))

    # Core and circuit branches.
    for radius, alpha in ((38, 24), (29, 42), (21, 80)):
        draw.ellipse(
            (scaled(128-radius), scaled(128-radius), scaled(128+radius), scaled(128+radius)),
            fill=(0, 229, 255, alpha)
        )
    draw.ellipse((scaled(105), scaled(105), scaled(151), scaled(151)),
                 fill=(5, 25, 34, 255), outline=pale, width=scaled(6))
    draw.ellipse((scaled(119), scaled(119), scaled(137), scaled(137)), fill=cyan)

    branches = [
        ((150, 113), (183, 89), (204, 89)),
        ((151, 128), (190, 128), (211, 128)),
        ((150, 143), (183, 167), (204, 167)),
        ((108, 112), (88, 90), (88, 77)),
        ((108, 144), (88, 166), (88, 179)),
    ]
    for start, bend, end in branches:
        draw.line(tuple(scaled(v) for point in (start, bend, end) for v in point), fill=(0, 183, 215, 245), width=scaled(4))
        ex, ey = end
        draw.ellipse((scaled(ex-6), scaled(ey-6), scaled(ex+6), scaled(ey+6)),
                     fill=(4, 15, 22, 255), outline=cyan, width=scaled(3))

    # Small status bars reinforce the app's HUD visual language.
    draw.rounded_rectangle((scaled(46), scaled(219), scaled(112), scaled(226)), radius=scaled(3), fill=(0, 229, 255, 210))
    draw.rounded_rectangle((scaled(119), scaled(219), scaled(155), scaled(226)), radius=scaled(3), fill=(0, 151, 180, 190))
    draw.rounded_rectangle((scaled(162), scaled(219), scaled(181), scaled(226)), radius=scaled(3), fill=(0, 229, 255, 235))
    return image


def contain(mark: Image.Image, width: int, height: int, coverage: float = 0.88) -> Image.Image:
    output = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    target = max(1, round(min(width, height) * coverage))
    resized = mark.resize((target, target), Image.Resampling.LANCZOS)
    output.alpha_composite(resized, ((width - target) // 2, (height - target) // 2))
    return output


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    mark = make_mark()
    source = mark.resize((1024, 1024), Image.Resampling.LANCZOS)
    source.save(ASSETS / "DagnueIcon.png", optimize=True)

    ico_source = mark.resize((256, 256), Image.Resampling.LANCZOS)
    ico_source.save(
        ASSETS / "AppIcon.ico", format="ICO",
        sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
    )

    outputs = {
        "Square150x150Logo.scale-200.png": (300, 300, 0.90),
        "Square44x44Logo.scale-200.png": (88, 88, 0.92),
        "Square44x44Logo.targetsize-24_altform-unplated.png": (24, 24, 1.0),
        "Square44x44Logo.targetsize-48_altform-lightunplated.png": (48, 48, 1.0),
        "StoreLogo.png": (50, 50, 0.92),
        "LockScreenLogo.scale-200.png": (48, 48, 0.92),
        "Wide310x150Logo.scale-200.png": (620, 300, 0.82),
        "SplashScreen.scale-200.png": (1240, 600, 0.70),
    }
    for filename, (width, height, coverage) in outputs.items():
        contain(mark, width, height, coverage).save(ASSETS / filename, optimize=True)


if __name__ == "__main__":
    main()
