// Groups the strings in the DNA file by prefix to reveal its sections (read-only).
using System.Text;
using System.Text.RegularExpressions;

var work = Path.Combine(Path.GetTempPath(), "claude", "c--Users-dougb-Nextcloud-Star-Citizen-TOOLS", "9bed0ac8-1943-47d2-a8fb-f6e0482a5336", "scratchpad", "chf3d");
var dna = File.ReadAllBytes(Path.Combine(work, @"extract\Data\Objects\Characters\Human\heads\male\pu\protos_human_male_face_t1_pu\protos_human_male_face_t1_pu_head.dna"));

var strings = new List<(int Offset, string Text)>(); var sb = new StringBuilder();
for (var i = 0; i < dna.Length; i++)
{
    var c = dna[i];
    if (c is >= 32 and < 127) sb.Append((char)c);
    else { if (sb.Length >= 4) strings.Add((i - sb.Length, sb.ToString())); sb.Clear(); }
}

// Where do strings live? Bucket by 1 MB
Console.WriteLine("String density per MB (first 10 MB):");
foreach (var g in strings.GroupBy(s => s.Offset / (1 << 20)).Take(10)) Console.WriteLine($"  MB {g.Key}: {g.Count()}");

// Group meaningful (identifier-like) strings by prefix
var idents = strings.Where(s => Regex.IsMatch(s.Text, @"^[A-Za-z][A-Za-z0-9_./]{5,}$")).ToList();
Console.WriteLine($"\nIdentifier-like strings: {idents.Count}, spanning 0x{idents.First().Offset:X} - 0x{idents.Last().Offset:X}");
foreach (var g in idents.GroupBy(s => Regex.Match(s.Text, @"^[A-Za-z]+_?[A-Za-z]*").Value).OrderByDescending(g => g.Count()).Take(25))
    Console.WriteLine($"  {g.Count(),5}  {g.Key,-28} e.g. {string.Join(", ", g.Take(3).Select(x => x.Text))}   [0x{g.First().Offset:X}]");

// Sections: show transitions where the prefix family changes, after the head list
Console.WriteLine("\nSection starts:");
string? last = null;
foreach (var (off, text) in idents)
{
    var fam = Regex.Match(text, @"^[A-Za-z]+").Value;
    if (fam != last) { Console.WriteLine($"  0x{off:X8} {text}"); last = fam; }
}
