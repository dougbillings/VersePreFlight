// Finds the exact start of the region mask by minimising region changes across mesh edges.
using System.Text;
using System.Text.Json;
var work = Path.Combine(Path.GetTempPath(), "claude", "c--Users-dougb-Nextcloud-Star-Citizen-TOOLS", "9bed0ac8-1943-47d2-a8fb-f6e0482a5336", "scratchpad", "chf3d");
var dna = File.ReadAllBytes(Path.Combine(work, @"extract\Data\Objects\Characters\Human\heads\male\pu\protos_human_male_face_t1_pu\protos_human_male_face_t1_pu_head.dna"));
const int Verts = 5584, Regions = 13;
var idx = ReadIndices(Path.Combine(work, "male_head.glb"));
var edges = new HashSet<(int, int)>();
for (var t = 0; t + 2 < idx.Length; t += 3)
    foreach (var (a, b) in new[] { (idx[t], idx[t + 1]), (idx[t + 1], idx[t + 2]), (idx[t + 2], idx[t]) })
        edges.Add(a < b ? (a, b) : (b, a));
Console.WriteLine($"{edges.Count} unique edges");

double Score(int start)
{
    if (start < 0 || start + 4L * Regions * Verts > dna.Length) return double.MaxValue;
    var arg = new int[Verts];
    for (var v = 0; v < Verts; v++)
    {
        var best = 0; var bw = float.MinValue;
        for (var r = 0; r < Regions; r++) { var w = BitConverter.ToSingle(dna, start + 4 * (r * Verts + v)); if (w > bw) { bw = w; best = r; } }
        arg[v] = best;
    }
    return edges.Count(e => arg[e.Item1] != arg[e.Item2]) / (double)edges.Count;
}

var results = Enumerable.Range(-64, 129).Select(k => (k, s: Score(0x14F5B74 + 4 * k))).OrderBy(x => x.s).Take(8).ToList();
foreach (var (k, s) in results) Console.WriteLine($"shift {k,4} floats (start 0x{0x14F5B74 + 4 * k:X}): {s:P2} of edges cross regions");

static int[] ReadIndices(string path)
{
    var b = File.ReadAllBytes(path);
    var jsonLen = BitConverter.ToInt32(b, 12);
    using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(b, 20, jsonLen));
    var bin = 20 + jsonLen + 8; var root = doc.RootElement;
    var a = root.GetProperty("accessors")[root.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("indices").GetInt32()];
    var v = root.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
    var off = bin + (v.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0) + (a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
    var n = a.GetProperty("count").GetInt32(); var type = a.GetProperty("componentType").GetInt32();
    return Enumerable.Range(0, n).Select(i => type == 5125 ? (int)BitConverter.ToUInt32(b, off + i * 4) : BitConverter.ToUInt16(b, off + i * 2)).ToArray();
}
