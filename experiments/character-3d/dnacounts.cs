// Looks for structural count fields (u32) inside the DNA file to find record boundaries.
var work = Path.Combine(Path.GetTempPath(), "claude", "c--Users-dougb-Nextcloud-Star-Citizen-TOOLS", "9bed0ac8-1943-47d2-a8fb-f6e0482a5336", "scratchpad", "chf3d");
var dna = File.ReadAllBytes(Path.Combine(work, @"extract\Data\Objects\Characters\Human\heads\male\pu\protos_human_male_face_t1_pu\protos_human_male_face_t1_pu_head.dna"));

uint[] targets = [5584, 738, 663, 1186, 521, 1341, 714, 1146, 696, 61, 198, 179, 37438, 32178, 10253, 780, 495, 1288, 328];
foreach (var t in targets)
{
    var hits = new List<int>();
    for (var i = 0; i + 4 <= dna.Length; i += 4) if (BitConverter.ToUInt32(dna, i) == t) hits.Add(i);
    var gaps = hits.Zip(hits.Skip(1), (a, b) => b - a).GroupBy(g => g).OrderByDescending(g => g.Count()).Take(3)
        .Select(g => $"{g.Key}x{g.Count()}");
    Console.WriteLine($"u32 {t,6}: {hits.Count,6} hits; first: {string.Join(" ", hits.Take(6).Select(h => $"0x{h:X}"))}; common gaps: {string.Join(" ", gaps)}");
}

// Also u16 occurrences of the vertex count, which often prefix index lists
var u16 = new List<int>();
for (var i = 0; i + 2 <= 0x14F5A00; i += 2) if (BitConverter.ToUInt16(dna, i) == 5584) u16.Add(i);
Console.WriteLine($"u16 5584 in big block: {u16.Count} hits; first: {string.Join(" ", u16.Take(10).Select(h => $"0x{h:X}"))}");
