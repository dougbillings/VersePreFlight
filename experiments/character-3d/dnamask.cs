// Tests whether the float block after the region names is a 5584 x 13 per-vertex region weight matrix.
var work = Path.Combine(Path.GetTempPath(), "claude", "c--Users-dougb-Nextcloud-Star-Citizen-TOOLS", "9bed0ac8-1943-47d2-a8fb-f6e0482a5336", "scratchpad", "chf3d");
var dna = File.ReadAllBytes(Path.Combine(work, @"extract\Data\Objects\Characters\Human\heads\male\pu\protos_human_male_face_t1_pu\protos_human_male_face_t1_pu_head.dna"));
const int Verts = 5584, Regions = 13;

// Region names and what precedes the float run
Console.WriteLine("Bytes 0x14F5A00..0x14F5B80:");
for (var i = 0x14F5A00; i < 0x14F5B80; i += 16)
    Console.WriteLine($"  {i:X8}  {Convert.ToHexString(dna, i, 16)}  {new string(dna.AsSpan(i, 16).ToArray().Select(c => c is >= 32 and < 127 ? (char)c : '.').ToArray())}");

foreach (var start in new[] { 0x14F5B74, 0x14F5B78, 0x14F5B7C, 0x14F5B80 })
{
    // Try both layouts: vertex-major [v][r] and region-major [r][v]
    foreach (var vertexMajor in new[] { true, false })
    {
        float W(int v, int r) => BitConverter.ToSingle(dna, start + 4 * (vertexMajor ? v * Regions + r : r * Verts + v));
        var sums = Enumerable.Range(0, Verts).Select(v => Enumerable.Range(0, Regions).Sum(r => W(v, r))).ToArray();
        var nearOne = sums.Count(s => Math.Abs(s - 1) < 0.01);
        var inRange = Enumerable.Range(0, Verts).All(v => Enumerable.Range(0, Regions).All(r => W(v, r) is >= -0.001f and <= 1.001f));
        Console.WriteLine($"start 0x{start:X} {(vertexMajor ? "vertex-major" : "region-major")}: sums~1 for {nearOne}/{Verts}, all in [0,1]: {inRange}, sum range {sums.Min():F3}..{sums.Max():F3}");
    }
}
