using OpenTK.Mathematics;

namespace AS.FBXReader.Rendering;

/// <summary>
/// Manual corrections for an individual SpriteSkin mesh. Operates only on
/// in-memory preview/export coordinates, never on FBX assets or UVs.
/// </summary>
public static class MeshPoseCorrection
{
    /// <summary>
    /// Keep the original local XY silhouette and follow only XY translation
    /// from the dominant animated bone. Rotations, shear and weighted skinning
    /// cannot distort the source artwork while Reset deform is enabled.
    /// </summary>
    public static Vector3[] Undeform(
        IReadOnlyList<Vector3> localVertices, Matrix4 restGlobal,
        Vector3 animatedTranslation)
    {
        var result = new Vector3[localVertices.Count];
        if (result.Length == 0)
            return result;
        var center = Vector3.Zero;
        foreach (var p in localVertices)
            center += p;
        center /= result.Length;
        var pivot = Vector3.TransformPosition(center, restGlobal);
        for (var i = 0; i < result.Length; i++)
        {
            var p = localVertices[i];
            result[i] = new Vector3(
                pivot.X + p.X - center.X + animatedTranslation.X,
                pivot.Y + p.Y - center.Y + animatedTranslation.Y,
                pivot.Z + animatedTranslation.Z);
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
