using System.Numerics;

namespace VersePreflight.App.Rendering;

/// <summary>
/// A small CPU rasterizer for one head mesh: perspective camera orbiting the face, z-buffer,
/// smooth (Gouraud) shading with a warm key light, a cool fill and a cyan HUD rim light.
/// Running on the CPU avoids GPU driver issues and works the same on every platform.
/// </summary>
public sealed class HeadRenderer
{
    private Vector3[] _positions = [];
    private int[] _indices = [];
    private Vector3[] _normals = [];
    private Vector3 _focus;
    private float _radius = 0.2f;
    private Vector3[] _shade = [];
    private Vector3[] _screen = [];
    private float _winding = 1; // -1 when the mesh's triangles wind inwards

    public bool HasMesh => _positions.Length > 0;

    /// <summary>Sets the mesh (Z-up, face looking towards +Y, as in the game files).</summary>
    public void SetMesh(Vector3[] positions, int[] indices)
    {
        _positions = positions;
        _indices = indices;
        if (positions.Length == 0 || indices.Length == 0) { _positions = []; _indices = []; return; }
        (_normals, _winding) = ComputeNormals(positions, indices);
        _shade = new Vector3[positions.Length];
        _screen = new Vector3[positions.Length];

        // Frame the head rather than the shoulders: centre on the top 25 cm of the mesh
        var top = positions.Max(p => p.Z);
        var head = positions.Where(p => p.Z > top - 0.25f).ToArray();
        var min = new Vector3(head.Min(p => p.X), head.Min(p => p.Y), head.Min(p => p.Z));
        var max = new Vector3(head.Max(p => p.X), head.Max(p => p.Y), head.Max(p => p.Z));
        _focus = (min + max) / 2;
        _radius = (max - min).Length() / 2;
    }

    /// <summary>Renders into a premultiplied BGRA buffer; empty pixels stay transparent.</summary>
    public void Render(uint[] pixels, float[] depth, int width, int height, float yaw, float pitch, float zoom)
    {
        Array.Clear(pixels, 0, width * height);
        Array.Fill(depth, float.MaxValue, 0, width * height);
        if (!HasMesh) return;

        // Orbit camera around the focus point; yaw turns around Z (up), pitch tilts
        var forwardFromFocus = new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Cos(yaw) * MathF.Cos(pitch), MathF.Sin(pitch));
        const float fov = 28f * MathF.PI / 180f;
        var distance = _radius / MathF.Sin(fov / 2) * 0.9f / zoom;
        var camera = _focus + forwardFromFocus * distance;
        var f = Vector3.Normalize(_focus - camera);
        var right = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitZ));
        var up = Vector3.Cross(right, f);
        var focal = height / (2 * MathF.Tan(fov / 2));

        // Lights are fixed relative to the camera so the face always reads well
        var key = Vector3.Normalize(-f * 0.8f + up * 0.55f - right * 0.45f);
        var fill = Vector3.Normalize(-f * 0.6f - up * 0.1f + right * 0.8f);
        var clay = new Vector3(0.80f, 0.76f, 0.72f);
        var rimColor = new Vector3(0.25f, 0.85f, 0.95f);

        for (var i = 0; i < _positions.Length; i++)
        {
            var rel = _positions[i] - camera;
            var z = Vector3.Dot(rel, f);
            _screen[i] = new Vector3(width / 2f + Vector3.Dot(rel, right) / z * focal, height / 2f - Vector3.Dot(rel, up) / z * focal, z);

            var n = _normals[i];
            var view = Vector3.Normalize(-rel);
            var diffuse = 0.16f + 0.78f * MathF.Max(0, Vector3.Dot(n, key)) + 0.22f * MathF.Max(0, Vector3.Dot(n, fill));
            var rim = MathF.Pow(1 - MathF.Max(0, Vector3.Dot(n, view)), 3) * 0.55f;
            _shade[i] = Vector3.Min(clay * diffuse + rimColor * rim, Vector3.One);
        }

        for (var t = 0; t + 2 < _indices.Length; t += 3)
        {
            int a = _indices[t], b = _indices[t + 1], c = _indices[t + 2];
            var faceNormal = Vector3.Cross(_positions[b] - _positions[a], _positions[c] - _positions[a]);
            if (_winding * Vector3.Dot(faceNormal, camera - _positions[a]) <= 0) continue; // back face
            Raster(pixels, depth, width, height, _screen[a], _screen[b], _screen[c], _shade[a], _shade[b], _shade[c]);
        }
    }

    private static void Raster(uint[] px, float[] depth, int w, int h, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 c0, Vector3 c1, Vector3 c2)
    {
        if (p0.Z <= 0 || p1.Z <= 0 || p2.Z <= 0) return;
        var area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
        if (MathF.Abs(area) < 1e-8f) return;
        var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X))));
        var maxX = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(p0.X, MathF.Max(p1.X, p2.X))));
        var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y))));
        var maxY = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y))));
        var inv = 1 / area;

        for (var y = minY; y <= maxY; y++)
        {
            var py = y + 0.5f;
            var row = y * w;
            for (var x = minX; x <= maxX; x++)
            {
                var pxf = x + 0.5f;
                var w0 = ((p1.X - pxf) * (p2.Y - py) - (p2.X - pxf) * (p1.Y - py)) * inv;
                var w1 = ((p2.X - pxf) * (p0.Y - py) - (p0.X - pxf) * (p2.Y - py)) * inv;
                var w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                var z = w0 * p0.Z + w1 * p1.Z + w2 * p2.Z;
                if (z >= depth[row + x]) continue;
                depth[row + x] = z;
                var col = w0 * c0 + w1 * c1 + w2 * c2;
                px[row + x] = 0xFF000000u | (uint)(col.X * 255) << 16 | (uint)(col.Y * 255) << 8 | (uint)(col.Z * 255);
            }
        }
    }

    internal static (Vector3[] Normals, float Winding) ComputeNormals(Vector3[] pos, int[] idx)
    {
        var n = new Vector3[pos.Length];
        for (var t = 0; t + 2 < idx.Length; t += 3)
        {
            var fn = Vector3.Cross(pos[idx[t + 1]] - pos[idx[t]], pos[idx[t + 2]] - pos[idx[t]]); // area-weighted
            n[idx[t]] += fn; n[idx[t + 1]] += fn; n[idx[t + 2]] += fn;
        }
        // If the mesh winding points normals inwards, flip them (centre-to-vertex test)
        var centre = pos.Aggregate(Vector3.Zero, (a, b) => a + b) / pos.Length;
        var outward = 0f;
        for (var i = 0; i < pos.Length; i++) outward += Vector3.Dot(n[i], pos[i] - centre);
        var sign = outward < 0 ? -1f : 1f;
        for (var i = 0; i < n.Length; i++) n[i] = n[i] == Vector3.Zero ? Vector3.UnitY : Vector3.Normalize(n[i] * sign);
        return (n, sign);
    }
}
