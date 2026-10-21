# Ember Multiplayer artwork

## EMP cover rebrand

The current `../images/cover.png` uses `emp-cover-source.png`: gold EMP initials and turquoise EMBER MULTIPLAYER text. Created with the built-in AI image generation tool. The original cover remains in `cover-source.png` for archival reference. The current package icon remains the CO-OP design from `icon-coop-source.png`, as requested.

### Rebrand prompt

Use case: text-localization
Asset type: updated landscape 16:9 mod cover for the renamed project EMP: Ember Multiplayer.
Input image: EDIT TARGET, existing cover. Preserve the beautiful pixel-art adventure scene.
Primary request: rebrand the title area completely. Remove ALL existing title lettering and its turquoise flame logo ornaments from the upper-left. Replace with an ORIGINAL title design using exactly these words:
First line: "EMP"
Second line: "EMBER MULTIPLAYER"
The initials EMP should be very large, bold, squared fantasy PIXEL BLOCK LETTERS, warm pale-gold to amber shading, thick deep-plum stepped outline, a few small ember pixels along the edges. Not the old carved Gothic stone logo; make a distinct clean identity. Beneath EMP, write EMBER MULTIPLAYER in substantial bright turquoise pixel block lettering, easy to read at 640px-wide preview. Comfortable margins, balanced title spacing. Let title dominate the left half, without obstructing the adventurers.
Preserve: exactly four adventurers, poses, clothing, weapons, staff, mossy cobblestone pathway, glowing teal portal, lanterns, cart, mushrooms, cavern ruins, the camera angle, detailed hard-edged pixel-art rendering, deep plum shadows and amber/teal lighting. Keep the scene as close as possible to original; edit title area only.
Constraints: no occurrence of the word Emberville, no developer logo, no official-game logo treatment, no extra text or watermarks. Only EMP and EMBER MULTIPLAYER. Full bleed opaque landscape image.


## CO-OP label revision

The package icon now uses `icon-coop-source.png`, edited with the built-in image generation tool. The prior text-free 256px icon is preserved as `icon-no-text-256.png`; its full-resolution source remains `icon-source.png`.

Use case: precise-object-edit
Asset type: square mod-list icon for Ember Multiplayer, displayed at 256x256 and at 64x64.
Input image: edit target, existing pixel-art two-hero icon.
Primary request: Add the exact text "CO-OP" to the icon, in large bold turquoise block PIXEL lettering with a thick dark plum outline and a subtle dark backing for legibility, centered across the bottom quarter. Make lettering large enough to read at a 64x64 thumbnail, five clear characters C O hyphen O P, one line. Use a clean block pixel font, no thin strokes.
Preserve: the two adventurers' faces, expressions, brown/auburn hair, green and slate-blue clothes, sword, teal-flamed staff, pixel-art rendering and dark dungeon colors. Keep both faces unobstructed and retain clear sword and staff silhouettes.
Layout change allowed: move the heroes slightly upward or reduce them modestly only as needed to create a dedicated bottom strip for the word. Let text cover lower belts/legs if needed. Keep the rest as close as possible to existing icon.
Constraints: exactly "CO-OP", no other text. Hard crisp pixel clusters, not smooth font. Turquoise text strongly contrasts with deep plum background. Square opaque full bleed, no watermark.


Promotional illustrations created with the built-in AI image generation tool. These are illustrations, not gameplay screenshots. The project's existing gameplay screenshots and title banner were used as visual references for Emberville's pixel art, character proportions, stone lettering, turquoise magic, and warm lantern light.

Deliverables:
- `../images/cover.png`: 1280 × 720 cover, displayed at the top of the repository README.
- `../../store/icon.png`: 256 × 256 PNG used by `scripts/store.py` for the package; checked at 64 × 64.
- `cover-source.png` and `icon-source.png`: original full-resolution generated outputs.

Final artwork is resized with nearest-neighbor interpolation to retain hard pixel edges. The previous `docs/images/banner.jpg` is preserved.
