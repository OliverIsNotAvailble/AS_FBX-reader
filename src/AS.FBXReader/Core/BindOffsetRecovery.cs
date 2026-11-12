using OpenTK.Mathematics;

namespace AS.FBXReader.Core;

/// <summary>
/// Validate Assimp skin offsets: offset * boneBind * inverse(meshBind)
/// must be identity at rest (OpenTK row-vector convention).
/// SpriteSkin FBXs may contain huge, finite, but invalid cluster offsets.
/// </summary>
public static class BindOffsetRecovery
{
    public static bool NeedsRepair(Matrix4 offset, Matrix4 meshBind, Matrix4 boneBind)
    {
        if (!IsFinite(offset) || !IsFinite(meshBind) || !IsFinite(boneBind))
            return true;
        var inverseMesh = meshBind.Inverted();
        if (!IsFinite(inverseMesh))
            return true;
        var m = offset * boneBind * inverseMesh;
        if (!IsFinite(m))
            return true;
        const float t = 0.1f;
        return MathF.Abs(m.M11 - 1f) > t || MathF.Abs(m.M22 - 1f) > t ||
               MathF.Abs(m.M33 - 1f) > t || MathF.Abs(m.M44 - 1f) > t ||
               MathF.Abs(m.M12) > t || MathF.Abs(m.M13) > t ||
               MathF.Abs(m.M14) > t || MathF.Abs(m.M21) > t ||
               MathF.Abs(m.M23) > t || MathF.Abs(m.M24) > t ||
               MathF.Abs(m.M31) > t || MathF.Abs(m.M32) > t ||
               MathF.Abs(m.M34) > t || MathF.Abs(m.M41) > t ||
               MathF.Abs(m.M42) > t || MathF.Abs(m.M43) > t;
    }

    public static bool TryRepair(Matrix4 meshBind, Matrix4 boneBind,
        out Matrix4 repaired)
    {
        repaired = Matrix4.Identity;
        if (!IsFinite(meshBind) || !IsFinite(boneBind))
            return false;
        var inverted = boneBind.Inverted();
        if (!IsFinite(inverted))
            return false;
        var offset = meshBind * inverted;
        if (!IsFinite(offset))
            return false;
        repaired = offset;
        return true;
    }

    public static bool IsFinite(Matrix4 m)
        => float.IsFinite(m.M11) && float.IsFinite(m.M12) &&
           float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
           float.IsFinite(m.M21) && float.IsFinite(m.M22) &&
           float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
           float.IsFinite(m.M31) && float.IsFinite(m.M32) &&
           float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
           float.IsFinite(m.M41) && float.IsFinite(m.M42) &&
           float.IsFinite(m.M43) && float.IsFinite(m.M44);
}
