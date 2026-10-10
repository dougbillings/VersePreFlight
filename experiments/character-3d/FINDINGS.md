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
