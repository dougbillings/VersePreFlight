using System.Numerics;

namespace VersePreflight.Core.Characters;

/// <summary>
/// Moves meshes that sit on the face (hair, beards, eyebrows, the scalp cap) with a character's face shape.
/// They're authored on the base head; in the game they're skinned to the facial skeleton, which the head DNA
/// moves per character. This follows the same result geometrically: each vertex moves by how much the
/// face around it moved from the base head to the character's blend, fading out with distance from the skin.
/// </summary>
public sealed class FaceFollower
{
    private const float Cell = 0.02f;      // grid cell (m)
    private const int Neighbours = 6;      // face vertices averaged per point
    private const float FullFollow = 0.03f; // points this close to the skin follow it fully...
    private const float NoFollow = 0.12f;   // ...fading to not at all this far away

    private readonly Vector3[] _base;
    private readonly Dictionary<(int, int, int), List<int>> _grid = [];

    public FaceFollower(Vector3[] baseHead)
    {
        _base = baseHead;
        for (var i = 0; i < baseHead.Length; i++)
        {
            var k = Key(baseHead[i]);
            if (!_grid.TryGetValue(k, out var list)) _grid[k] = list = [];
            list.Add(i);
        }
    }

    private static (int, int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));

    /// <summary>The points moved with the face, from <see cref="_base"/> to <paramref name="blendedHead"/>.</summary>
    public Vector3[] Follow(Vector3[] points, Vector3[] blendedHead)
    {
        if (blendedHead.Length != _base.Length) throw new ArgumentException("Head vertex count doesn't match.", nameof(blendedHead));
        var result = new Vector3[points.Length];
        Parallel.For(0, (points.Length + 1023) / 1024, chunk =>
        {
            Span<int> nearest = stackalloc int[Neighbours];
            Span<float> distances = stackalloc float[Neighbours];
            for (var i = chunk * 1024; i < Math.Min(points.Length, (chunk + 1) * 1024); i++)
                result[i] = points[i] + Displacement(points[i], blendedHead, nearest, distances);
        });
        return result;
    }

    private Vector3 Displacement(Vector3 p, Vector3[] blended, Span<int> nearest, Span<float> distances)
    {
        // The nearest base-head vertices, searching outward ring by ring
        var found = 0;
        var (kx, ky, kz) = Key(p);
        var maxRing = (int)MathF.Ceiling(NoFollow / Cell);
        for (var ring = 0; ring <= maxRing; ring++)
        {
            for (var dx = -ring; dx <= ring; dx++)
                for (var dy = -ring; dy <= ring; dy++)
                    for (var dz = -ring; dz <= ring; dz++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) != ring) continue; // shell only
                        if (!_grid.TryGetValue((kx + dx, ky + dy, kz + dz), out var list)) continue;
                        foreach (var v in list)
                        {
                            var d = Vector3.Distance(p, _base[v]);
                            if (found < Neighbours) { nearest[found] = v; distances[found] = d; found++; }
                            else
                            {
                                var worst = 0;
                                for (var j = 1; j < Neighbours; j++) if (distances[j] > distances[worst]) worst = j;
                                if (d < distances[worst]) { nearest[worst] = v; distances[worst] = d; }
                            }
                        }
                    }
            // Anything in a further ring is at least ring * Cell away
            if (found == Neighbours)
            {
                var worstDistance = 0f;
                for (var j = 0; j < Neighbours; j++) worstDistance = MathF.Max(worstDistance, distances[j]);
                if (worstDistance <= ring * Cell) break;
            }
        }
        if (found == 0) return Vector3.Zero;

        float closest = float.MaxValue, total = 0;
        var move = Vector3.Zero;
        for (var j = 0; j < found; j++)
        {
            closest = MathF.Min(closest, distances[j]);
            var w = 1 / (distances[j] * distances[j] + 1e-4f); // 1 cm softening
            move += (blended[nearest[j]] - _base[nearest[j]]) * w;
            total += w;
        }
        var t = Math.Clamp((NoFollow - closest) / (NoFollow - FullFollow), 0, 1);
        return move / total * (t * t * (3 - 2 * t));
    }
}
