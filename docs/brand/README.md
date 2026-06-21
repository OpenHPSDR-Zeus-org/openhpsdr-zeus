# Zeus brand assets

Canonical master logo files. Keep the source-of-truth art here; each consumer
(the operator's manual, the web app, release art) copies or derives the variant
it needs rather than re-creating the logo.

| File | Use it on |
|------|-----------|
| `zeus-logo-black.png` | light / white backgrounds (e.g. the manual cover stamp, printed/light docs) |
| `zeus-logo-white.png` | dark backgrounds (e.g. dark UI chrome, the near-black panel gradient) |

Both are the ZEUS / Software Defined Radio emblem on a **transparent** background,
Adam7-interlaced PNG, 603×768. The black version is a single-ink "stamp"; the
white version is the same cutout with a light emblem.

## Where the derived copies live

- **Operator's manual cover** — `docs/manual/assets/zeus_manual_logo.png`
  (currently the black stamp; the manual inlines it as a base64 data URI in
  `assemble.mjs`).
- **Web app PWA icons** — `zeus-web/public/zeus-icon-*.png` (separate, square,
  maskable icons; not derived from these masters).

## Editing

To re-cut a variant from a source image with a solid background, build the alpha
from luminance and strip the box (ImageMagick):

```bash
# white emblem on transparency
magick source.jpg \( +clone -colorspace Gray -level 20%,45% \) \
  -alpha off -compose CopyOpacity -composite -strip -interlace PNG zeus-logo-white.png

# black "stamp" from the transparent white version (zero the colour, keep alpha)
magick zeus-logo-white.png -channel RGB -evaluate multiply 0 +channel \
  -strip -interlace PNG zeus-logo-black.png
```
