using OpenTK.Mathematics;

namespace AS.FBXReader.Rendering;

/// <summary>
/// Manual corrections for an individual SpriteSkin mesh. Operates only on
/// in-memory preview/export coordinates, never on FBX assets or UVs.
/// </summary>
public static class MeshPoseCorrection
{
    /// <summary>
    /// Flatten a tilted SpriteSkin mesh onto its parent's original XY plane.
    /// The parent rest matrix retains authored scale, horizontal mirroring
    /// and rotation. A mesh's own X/Y tilt is intentionally removed, but its
    /// original translation is kept via the full mesh rest origin.
    ///
    /// This is essential for FBXs such as cg2, whose root has 530x scale and
    /// a horizontal mirror: using raw vertices without that parent matrix
    /// makes a head appear normal by itself but ~530x smaller alongside eyes.
    /// </summary>
    public static Vector3[] Undeform(
        IReadOnlyList<Vector3> localVertices, Matrix4 restGlobal,
        Matrix4 parentRestGlobal, Vector3 animatedTranslation)
    {
        var result = new Vector3[localVertices.Count];
        if (result.Length == 0)
            return result;

        // TransformPosition uses OpenTK's row-vector convention. The mesh
        // origin includes its own local translation, while its 2D axes use
        // the parent plane instead of the out-of-plane mesh-local rotation.
        var origin = Vector3.TransformPosition(Vector3.Zero, restGlobal);
        var parentOrigin = Vector3.TransformPosition(Vector3.Zero, parentRestGlobal);
        for (var i = 0; i < result.Length; i++)
        {
            var planar = Vector3.TransformPosition(localVertices[i], parentRestGlobal)
                - parentOrigin;
            result[i] = origin + planar + animatedTranslation;
        }
        return result;
    }

    /// <summary>
    /// Horizontal mirror of the drawable around its own center. Only X
    /// changes: Y, Z, UV coordinates, mesh topology, animation and bones are
    /// untouched. Applying it twice restores the previous positions.
    /// </summary>
    public static void FlipHorizontal(Vector3[] points)
    {
        if (points.Length == 0)
            return;

        var centerX = 0f;
        foreach (var point in points)
            centerX += point.X;
        centerX /= points.Length;

        for (var i = 0; i < points.Length; i++)
            points[i].X = 2f * centerX - points[i].X;
    }

    public static void RotateAroundCenter(Vector3[] points, float degrees)
    {
        if (points.Length == 0 || !float.IsFinite(degrees) ||
            MathF.Abs(degrees % 360f) < 0.00001f)
            return;
        var center = Vector3.Zero;
        foreach (var p in points)
            center += p;
        center /= points.Length;
        var radians = degrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        for (var i = 0; i < points.Length; i++)
        {
            var dx = points[i].X - center.X;
            var dy = points[i].Y - center.Y;
            points[i] = new Vector3(
                center.X + cos * dx - sin * dy,
                center.Y + sin * dx + cos * dy,
                points[i].Z);
        }
    }
}
