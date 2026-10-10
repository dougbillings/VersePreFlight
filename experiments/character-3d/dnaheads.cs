// Lists the 61 head names in the DNA file and finds a matching head mesh in the p4k listing for each.
using System.Text;
var work = Path.Combine(Path.GetTempPath(), "claude", "c--Users-dougb-Nextcloud-Star-Citizen-TOOLS", "9bed0ac8-1943-47d2-a8fb-f6e0482a5336", "scratchpad", "chf3d");
var dna = File.ReadAllBytes(Path.Combine(work, @"extract\Data\Objects\Characters\Human\heads\male\pu\protos_human_male_face_t1_pu\protos_human_male_face_t1_pu_head.dna"));
// Head names are fixed 40-byte slots starting at 0x2D0
var names = Enumerable.Range(0, 61).Select(i => Encoding.ASCII.GetString(dna, 0x2D0 + i * 40, 40).TrimEnd('\0')).ToList();
var listing = File.ReadAllLines(Path.Combine(work, "human-sized.txt")).Select(l => l.Split('\t')[0]).ToList();
var found = 0;
var paths = new List<string>();
for (var i = 0; i < names.Count; i++)
{
    var n = names[i];
    var mesh = listing.FirstOrDefault(p => p.EndsWith($"/{n}_head.skinm", StringComparison.OrdinalIgnoreCase));
    if (mesh is not null) { found++; paths.Add(mesh); }
    Console.WriteLine($"{i,2} {n,-28} {(mesh is null ? "-- no mesh" : mesh.Replace("Data/Objects/Characters/Human/heads/", ""))}");
}
Console.WriteLine($"\n{found}/61 heads have a matching *_head.skinm");
File.WriteAllLines(Path.Combine(work, "head-mesh-paths.txt"), paths);
