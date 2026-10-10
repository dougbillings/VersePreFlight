// Dumps the area after the head-name list, looking for a customizer id -> head mapping table.
using System.Text;
var work = Path.Combine(Path.GetTempPath(), "claude", "c--Users-dougb-Nextcloud-Star-Citizen-TOOLS", "9bed0ac8-1943-47d2-a8fb-f6e0482a5336", "scratchpad", "chf3d");
var dna = File.ReadAllBytes(Path.Combine(work, @"extract\Data\Objects\Characters\Human\heads\male\pu\protos_human_male_face_t1_pu\protos_human_male_face_t1_pu_head.dna"));

Console.WriteLine("Header 0x00..0x2D0 as u16:");
for (var i = 0; i < 0x2D0; i += 32)
    Console.WriteLine($"  {i:X4}: " + string.Join(" ", Enumerable.Range(0, 16).Select(k => BitConverter.ToUInt16(dna, i + k * 2).ToString().PadLeft(5))));

Console.WriteLine("\n0xC30..0xCA0 (after 60 names):");
for (var i = 0xC30; i < 0xCA0; i += 16) Console.WriteLine($"  {i:X4}: {Convert.ToHexString(dna, i, 16)}  {Encoding.ASCII.GetString(dna, i, 16).Replace('\0', '.')}");

// Per-head descriptor block: try to find a record stride by looking at where 1.0f values repeat
Console.WriteLine("\nRecord candidates after the tag names (u32/float view 0x1210..0x1300):");
for (var i = 0x1210; i < 0x1300; i += 16)
    Console.WriteLine($"  {i:X4}: " + string.Join(" ", Enumerable.Range(0, 4).Select(k => BitConverter.ToSingle(dna, i + k * 4).ToString("0.00").PadLeft(6))));
