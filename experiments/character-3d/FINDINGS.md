# Character 3D head: research findings (experiment)

Status: **experiment**, not part of any release. Everything here is read from the player's own
game install at run time; no game files are stored in this repository.

## Result

A character's head shape can be rebuilt from its `.chf` file plus the player's `Data.p4k`.
The reconstructions of four test characters are smooth and clearly distinct. Compared with in-game
screenshots, the overall features match (face width, brow, lips). Nose width looks somewhat narrower than
in game for two of the four characters; the cause is still open.

## How the game builds a head (male, LIVE 4.10.2)

1. **The `.chf` DNA** (decoded with StarBreaker's `starbreaker-chf`, MIT) lists, for each of 12 face
   parts (13 in newer files, with Neck), up to 4 `(head_id, weight)` pairs. Weights are u16/65535 and
   sum to ~1 per part. The body type is `protos_human_male_face_t1_pu`.
2. **`Data/Objects/Characters/Human/heads/male/pu/protos_human_male_face_t1_pu/protos_human_male_face_t1_pu_head.dna`**
   (CIG format, magic `DNA V1.6`, ~76 MB):
   - header: 13 regions, 61 head slots, 5584 vertices, 32178 indices. From `0x40` it holds a table of
     u64 section offsets; for example, it points to the region names at `0x14F5A00` and the mask at `0x14F5B78`.
   - **`0x2A8`: 61 head slots of 40 bytes each. A .chf `head_id` indexes these slots directly. Slot 0 is the
     base head (`protos_human_male_face_t1_pu`)**, slots 1-15 are `male_archetype_v001..015_t1`, 16 is
     `male16_t1`, 17 `silas_t1`, ... 60 `male48_t1`.
     Starting the list at `0x2D0` (one slot later) gives wrong faces. That was confirmed by comparing renders
     with starchar's per-id reference images, for example id 20 = `boss_t1`.
   - `0xC98`: per-head descriptors (ethnicity: AFRICAN/CAUCASIAN/HISPANIC; build: SKINNY/AVERAGE/HEAVY/MUSCULAR)
   - `0x14F5A00`: region names in 16-byte slots: BROW_L, BROW_R, EYE_L, EYE_R, NOSE_C, EAR_L, EAR_R,
     CHEEK_L, CHEEK_R, MOUTH_C, JAWLINE_C, CROWN_C, NECK_C. They are followed by an index table
     `0 1 3 2 4 6 5 8 7 9 10 11 12`, which may swap L/R between .chf parts and mask regions. This is
     unverified, because the test characters are symmetric.
   - **`0x14F5B78`: region mask**, float32, region-major `[13][5584]`. Each vertex's weights sum to
     exactly 1. The start was confirmed by edge coherence: 4.6% of edges cross regions at this offset,
     against about 15% at the neighbouring offsets.
   - `0x1300` to `0x14F5A00` (~22 MB) and the ~52 MB after the mask: **not decoded, and not needed**
3. **Preset head meshes**: `<name>_head.skinm` under `heads/male/pu/...` and `heads/male/npc/...`.
   - 59 of 60 exist. `imperator_t1` is missing, so it falls back to the base head.
   - All have the same topology as the base head: 5584 vertices, an identical index buffer and identical UVs.
   - Many are stored with a global offset (about (0, -14, -50) mm). Subtracting each head's mean
     offset from the base aligns them.
   - The base head's `vertexOrderRemapTables.json` is **not** needed: the mask uses the mesh vertex order.
4. **Blend**: `pos[v] = sum over regions r of mask[r][v] * (sum over k of w[r,k] * head[id[r,k]][v]) / (sum over k of w[r,k])`.
   Regions with no entries (Neck in 12-part files) use the base head.

5. **Related game data**: the DataCore record `EntityClassDefinition.CharacterCustomizer_PU` holds:
   - customizer settings
   - 48 skin base materials (the in-game "TEXTURE n/48" slider)
   - per-region shaping vertices

   It does not hold the head-id list.

## Not done yet

- Finding why noses look narrower than in game for some characters. Candidates: the L/R table,
  the mean-offset alignment, or the in-game camera perspective.
- Female heads (`heads/female/pu/protos_human_female_face_t1_pu`), which presumably work the same way
- Skin tone, eyes, brows, hair and beard from the `.chf` materials and loadout; textures
- Reading `Data.p4k` and `.skinm` in C# (StarBreaker's Rust code is MIT and could be ported), and a real-time viewer

## Scripts

Prototype scripts (C# file-based apps, run with `dotnet run <file>.cs`). Paths point to a local scratch folder.

- `blend.cs`: blends a character and software-renders the comparison images
- `maskshift.cs`: finds the mask start by edge coherence
- `idtest.cs` and `idtest_big.cs`: compare head-id hypotheses against reference images (`starcharsheet.cs` downloads
  them locally; they are not committed)
- `dnaprobe.cs`, `dnamap.cs`, `dnacount.cs`, `dnacounts.cs`, `dnastrings.cs`, `dnamask.cs`, `dnaheads.cs`, `dnaids.cs`, `order.cs`: format exploration

## Skin texture and hair colour (2026-10-10)

- Skin: the .chf HeadMaterial GUID is a DataCore id. In Game2.dcb instance data it sits in the 16 bytes before a
  4-byte table1 offset to `..._head_material.mtl`; that mtl's TexSlot1 is the DXT1 diffuse (2048², split mips).
  Rendered as texture × (BodyColor / texture mean) in linear light, so the tone stays the player's choice.
- Hair melanin: .chf BaseMelanin 0 = use the style mtl's BaseMelanin (e.g. m_hair_30 0.672, m_hair_24 0.928).
  The style mtls also carry their own DyeAmount/DyeColor/DyeFadeout; the .chf dye values replace them.
- Melanin → colour uses the physically based model (concentration = -ln(1-m); eu/pheo absorption,
  colour = exp(-sqrt(σ)·4)). The linear table made 0.67 mid-brown, which is far too light compared with the game.
- Dye: tip-side, as confirmed by gomabptu's beard (white dye 1, black dye 2, amount 0.28 → grey-white at the bottom tips)
  and redshirt (black dye 1, red dye 2, amount 0.11 → red twists at the top). A black colour means "none" when the other
  is set. The visible reach is wider than the raw amount; √amount matched the shots better.
- Still missing: scalp/stubble shadow on shaved areas, makeup, freckles; gomabptu's grey beard tips are faint.

## Closer to the game: full skin material, hair caps, per-pixel renderer (2026-10-10)

- Skin material (HumanSkin_V2) slots: TexSlot1 diffuse (DXT1 2048), TexSlot2 `_ddna` normal (signed BC5, green points
  up the texture) with gloss in a header-less sibling chain `_ddna.dds.a` / `.dds.Na` (DDS header without the magic),
  TexSlot4 `_spec` (DXT1, a reflectance mask; material Specular ≈ 0.032), TexSlot6 `skin_micro_detail` (uncompressed
  RGBA, tiled 30×; red cavity, green normal Y, blue gloss, alpha normal X — CryEngine detail-map layout),
  TexSlot12 `male_head_trans` (BC4 translucency). Wrinkle maps and head_mask aren't used.
- Skin tone: SourceAverageColor/FinalSkinTone (linear) exist per material, but texture × body/SourceAverage didn't
  match the shots. What matches: tone the texture so its mean equals the stored colour, desaturated 20% (linear),
  and light it so camera-facing skin lands a little under the stored colour. The booth light is cool and side-on.
- Hair materials (HairPBR): TexSlot1 `_opac` (BC5, R = G = opacity, 4096), TexSlot4 `_id` (BC4 strand ids),
  TexSlot6 `_dir` (BC7 flow, mostly +V). Params: Smoothness, SpecularMultiplier, OpacityMipScale (2.7–3.8: opacity is
  boosted at smaller mips), AmbientOcclusion, ShadowDensity, Base/Dye variation.
- Hair caps: sub-materials with `%HAIR_CAP` in StringGenMask carry a density map (BC7 caps repeat it in every channel;
  DXT5 caps are white with alpha). Beards use their own second submesh. Hair styles have no cap submesh: the
  separate `m_universal_scalp` mesh (one submesh, material id 1) wears the equipped hairstyle's material, so its
  sub-material 1 (`hair_scalp_m`). Characters saved before the scalp slot existed still get it.
- Density comes mostly from shadowing: skin under hair/beards is darkened by the density of strand vertices within
  1 cm; strands are darkened by their height above the skin. Without this, hair looks thin and see-through.
- BC7 decoder verified bit-exact against BCnEncoder.Net on a 256² game texture (test-only check, not a dependency).
- Renderer: visibility buffer + per-pixel skin shading (normal + detail, two GGX lobes, wrap/soft-normal scattering,
  translucency), hair/caps lit per vertex and blended front to back with early exit, ACES tone mapping.
  Release timings on a 16-thread desktop: 70–210 ms for a 2× supersampled 1200×1440 frame. The viewer draws a
  capped-resolution frame while dragging and renders the full-quality one on a background thread.

## Matching the in-game look with data (2026-10-10)

Method: `overlay_vs_ingame.cs` renders each test character head-on and aligns it to the user's in-game shot by the
pupils (in-game pupils at about (1311,711) and (1556,710) in the 3840×1600 shots, same booth camera for all), then
compares colours at the same face points. `fit_booth_lighting.cs` renders each light component on its own in linear
light and solves for intensities by least squares against the in-game patches (inverse of our ACES curve).

- Skin tone: the game's formula (skin colour × texture ÷ the material's SourceAverageColor) fits best (RMS 12.0
  sRGB levels with any rig, vs 13.7 for "texture mean = skin colour"). It's why gomab (male04_t2, a texture authored
  lighter than its SourceAverageColor) shows lighter in game. SourceDetail as a contrast exponent didn't help (12.7).
- The .chf head material GUID is the customizer skin texture (DataCore record; byte order differs from StarBreaker's
  display); the head material also carries freckles, sun spots, makeup and tattoo parameters (StarBreaker `chf to-json`
  names them).
- Booth lighting, fitted with white lights + one colour balance (RMS 11.2): large softbox on the right (az 60–90°,
  el −30…30°), small light above the camera, faint left fill, soft light from below, skin highlights ~6× a single
  GGX lobe (a broad sheen), and a cool balance (R 0.75, G 1, B 1.14). Free per-channel light colours fit a little
  better (9.8) but gave unnatural greenish skin, so they were rejected.
- Preset heads are already registered to the base head (necks within ~1 mm); the old mean-offset alignment moved
  them by up to 5 mm, so they're now used as stored.
- Lips: our lower lips read fuller than in game. With the eyes aligned, the mouth is in the right place; in plain
  geometry the lips are closed but full, and the DNA mouth region maps correctly (MOUTH_C = chf Mouth). The DNA file
  also holds a full facial rig (≈179 joints with per-preset data, "FACIAL_C_FacialRoot", ~48 MB of expression /
  pose-space shapes "BS_CTL_PSD", interleaved per region). The in-game difference most likely comes from that rig or
  the booth's idle expression; decoding it would be a sizeable reverse-engineering job.

## Beards (2026-10-10)

`beard_vs_ingame.cs` measures moustache, beside-mouth and chin patches against the in-game shots (eye-aligned).
- What made beards look thick: the skin shading under facial hair. At full strength (the setting that makes short
  scalp hair read dense) skin under beards went near black (BACKHAND moustache 9 vs 34 in game, chin 11 vs 25), so
  beards read as solid blocks. Facial hair now shades the skin at 0.15 of the scalp-hair strength; skin shows through
  as in game. The beard cap layer and the strand opacity boost made little difference.
- Hair, beards, eyebrows and the scalp are skinned meshes (`.skin`/`.skinm`) in game, bound to the head skeleton
  that the head DNA moves per character; there is no hair DNA. They're authored on the base head, so on a blended
  face they floated off or sank in (gomab's beard bulged past his jaw). `FaceFollower` moves each vertex by the
  blended face's displacement around it (6 nearest base-head vertices, inverse-square weights), fully within 3 cm of
  the skin, fading out by 12 cm. Beard outlines now line up with the in-game shots.
- Still different: in-game beards look more neutral/near-black; ours are a little warmer and browner.
