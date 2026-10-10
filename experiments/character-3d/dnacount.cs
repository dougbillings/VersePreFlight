// Counts non-zero region/vertex pairs in the mask and compares with the size of the big data block.
var work = Path.Combine(Path.GetTempPath(), "claude", "c--Users-dougb-Nextcloud-Star-Citizen-TOOLS", "9bed0ac8-1943-47d2-a8fb-f6e0482a5336", "scratchpad", "chf3d");
var dna = File.ReadAllBytes(Path.Combine(work, @"extract\Data\Objects\Characters\Human\heads\male\pu\protos_human_male_face_t1_pu\protos_human_male_face_t1_pu_head.dna"));
const int Verts = 5584, Regions = 13, Heads = 61, MaskStart = 0x14F5B74;
float W(int r, int v) => BitConverter.ToSingle(dna, MaskStart + 4 * (r * Verts + v));

var perRegion = Enumerable.Range(0, Regions).Select(r => Enumerable.Range(0, Verts).Count(v => W(r, v) > 0)).ToArray();
var total = perRegion.Sum();
Console.WriteLine($"Non-zero vertices per region: {string.Join(", ", perRegion)}");
Console.WriteLine($"Total region-vertex pairs: {total}; vertices touched by >1 region: {Enumerable.Range(0, Verts).Count(v => Enumerable.Range(0, Regions).Count(r => W(r, v) > 0) > 1)}");

const int BlockStart = 0x1300, BlockEnd = 0x14F5A00;
var block = BlockEnd - BlockStart;
Console.WriteLine($"Big block: {block:N0} bytes");
foreach (var (label, bytesPer) in new[] { ("pairs x heads x 12 (float3)", total * Heads * 12.0), ("pairs x heads x 6 (int16x3)", total * Heads * 6.0),
                                           ("verts x heads x 12", Verts * Heads * 12.0), ("verts x heads x regions x 12", Verts * Heads * Regions * 12.0) })
    Console.WriteLine($"  {label,-30} = {bytesPer,14:N0}  (block / this = {block / bytesPer:F3})");
