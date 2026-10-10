// Maps the DNA file: entropy per block, plus a look at the start of the big data block (read-only).
var work = Path.Combine(Path.GetTempPath(), "claude", "c--Users-dougb-Nextcloud-Star-Citizen-TOOLS", "9bed0ac8-1943-47d2-a8fb-f6e0482a5336", "scratchpad", "chf3d");
var dna = File.ReadAllBytes(Path.Combine(work, @"extract\Data\Objects\Characters\Human\heads\male\pu\protos_human_male_face_t1_pu\protos_human_male_face_t1_pu_head.dna"));

double Entropy(int start, int len)
{
    var counts = new int[256];
    for (var i = start; i < start + len && i < dna.Length; i++) counts[dna[i]]++;
    return -counts.Where(c => c > 0).Sum(c => { var p = c / (double)len; return p * Math.Log2(p); });
}

Console.WriteLine("Entropy (bits/byte, 8 = random/compressed) per 2 MB:");
for (var off = 0; off < dna.Length; off += 2 << 20)
    Console.WriteLine($"  0x{off:X8}  {Entropy(off, Math.Min(2 << 20, dna.Length - off)):F2}");

Console.WriteLine("\nHeader 0x1100..0x1400 (after tags):");
for (var i = 0x1100; i < 0x1400; i += 16)
    Console.WriteLine($"  {i:X8}  {Convert.ToHexString(dna, i, 16)}");

// Zstd / zlib signatures anywhere?
int Count(byte[] sig) { var n = 0; for (var i = 0; i + sig.Length <= dna.Length; i++) if (dna.AsSpan(i, sig.Length).SequenceEqual(sig)) n++; return n; }
Console.WriteLine($"\nzstd frames: {Count([0x28, 0xB5, 0x2F, 0xFD])}, zlib(78 9C): {Count([0x78, 0x9C])}, zlib(78 DA): {Count([0x78, 0xDA])}");
