// Checks whether preset head meshes share the base mesh's vertex order, by position and by UV.
using System.Numerics;
using System.Text;
using System.Text.Json;

var work = Path.Combine(Path.GetTempPath(), "claude", "c--Users-dougb-Nextcloud-Star-Citizen-TOOLS", "9bed0ac8-1943-47d2-a8fb-f6e0482a5336", "scratchpad", "chf3d");
var (bp, buv, bidx) = Read(Path.Combine(work, "male_head.glb"));
Console.WriteLine($"base: {bp.Length} verts, uv: {(buv is null ? "none" : "yes")}, first tri {bidx[0]},{bidx[1]},{bidx[2]}");
foreach (var name in new[] { "male23_t1", "parr_t1", "male_archetype_v001_t1", "silas_t1" })
{
    var (p, uv, idx) = Read(Path.Combine(work, "heads", name + "_head.glb"));
    var d = Enumerable.Range(0, bp.Length).Select(i => (p[i] - bp[i]).Length()).OrderBy(x => x).ToArray();
    var sameIdx = bidx.SequenceEqual(idx);
    var uvSame = uv is not null && buv is not null ? Enumerable.Range(0, bp.Length).Count(i => Vector2.Distance(uv[i], buv[i]) < 1e-5f) : -1;
    Console.WriteLine($"{name,-24} same index buffer: {sameIdx}; same-index distance median {d[d.Length / 2] * 1000:F1} mm, 95% {d[(int)(d.Length * 0.95)] * 1000:F1} mm, max {d[^1] * 1000:F1} mm; UV equal at same index: {uvSame}/{bp.Length}");
}

static (Vector3[] P, Vector2[]? Uv, int[] Idx) Read(string path)
{
    var b = File.ReadAllBytes(path);
    var jsonLen = BitConverter.ToInt32(b, 12);
    using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(b, 20, jsonLen));
    var bin = 20 + jsonLen + 8; var root = doc.RootElement;
    var prim = root.GetProperty("meshes")[0].GetProperty("primitives")[0];
    var attrs = prim.GetProperty("attributes");
    (int Off, int Count, int Stride, int Type) Acc(int i, int def)
    {
        var a = root.GetProperty("accessors")[i]; var v = root.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
        return (bin + (v.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0) + (a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0),
            a.GetProperty("count").GetInt32(), v.TryGetProperty("byteStride", out var s) ? s.GetInt32() : def, a.GetProperty("componentType").GetInt32());
    }
    var pa = Acc(attrs.GetProperty("POSITION").GetInt32(), 12);
    var p = Enumerable.Range(0, pa.Count).Select(i => new Vector3(BitConverter.ToSingle(b, pa.Off + i * pa.Stride), BitConverter.ToSingle(b, pa.Off + i * pa.Stride + 4), BitConverter.ToSingle(b, pa.Off + i * pa.Stride + 8))).ToArray();
    Vector2[]? uv = null;
    if (attrs.TryGetProperty("TEXCOORD_0", out var t))
    {
        var ua = Acc(t.GetInt32(), 8);
        uv = Enumerable.Range(0, ua.Count).Select(i => ua.Type == 5126
            ? new Vector2(BitConverter.ToSingle(b, ua.Off + i * ua.Stride), BitConverter.ToSingle(b, ua.Off + i * ua.Stride + 4))
            : new Vector2(BitConverter.ToUInt16(b, ua.Off + i * ua.Stride) / 65535f, BitConverter.ToUInt16(b, ua.Off + i * ua.Stride + 2) / 65535f)).ToArray();
    }
    var ia = Acc(prim.GetProperty("indices").GetInt32(), 0);
    var idx = Enumerable.Range(0, ia.Count).Select(i => ia.Type == 5125 ? (int)BitConverter.ToUInt32(b, ia.Off + i * 4) : BitConverter.ToUInt16(b, ia.Off + i * 2)).ToArray();
    return (p, uv, idx);
}
