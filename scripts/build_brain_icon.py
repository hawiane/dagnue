from pathlib import Path

from PIL import Image, ImageEnhance, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "tmp" / "imagegen" / "dagnue-brain-transparent.png"
ASSETS = ROOT / "src" / "Nexus.App" / "Assets"


def fitted_icon(size: int, padding_ratio: float = 0.045) -> Image.Image:
    source = Image.open(SOURCE).convert("RGBA")
    alpha_box = source.getchannel("A").getbbox()
    if alpha_box is None:
        raise RuntimeError("Generated brain icon has no visible pixels")

    source = source.crop(alpha_box)
    usable = max(1, round(size * (1 - padding_ratio * 2)))
    source.thumbnail((usable, usable), Image.Resampling.LANCZOS)
    if size <= 48:
        source = ImageEnhance.Contrast(source).enhance(1.12)
        source = source.filter(ImageFilter.UnsharpMask(radius=0.65, percent=145, threshold=2))

    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    canvas.alpha_composite(source, ((size - source.width) // 2, (size - source.height) // 2))
    return canvas


master = fitted_icon(1024)
master.save(ASSETS / "DagnueBrainIcon.png", optimize=True)
master.save(ASSETS / "DagnueIcon.png", optimize=True)

ico_sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48),
             (64, 64), (96, 96), (128, 128), (256, 256)]
master.save(ASSETS / "AppIcon.ico", format="ICO", sizes=ico_sizes)

fitted_icon(88).save(ASSETS / "Square44x44Logo.scale-200.png", optimize=True)
fitted_icon(24, 0.02).save(
    ASSETS / "Square44x44Logo.targetsize-24_altform-unplated.png", optimize=True)
fitted_icon(48, 0.025).save(
    ASSETS / "Square44x44Logo.targetsize-48_altform-lightunplated.png", optimize=True)
fitted_icon(300).save(ASSETS / "Square150x150Logo.scale-200.png", optimize=True)
fitted_icon(100).save(ASSETS / "StoreLogo.png", optimize=True)
