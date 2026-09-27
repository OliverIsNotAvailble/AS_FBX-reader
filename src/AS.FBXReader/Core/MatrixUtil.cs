using OpenTK.Mathematics;

namespace AS.FBXReader.Core;

public static class MatrixUtil
{
    // AssimpNetter 6.x copies native aiMatrix4x4 fields directly into
    // System.Numerics.Matrix4x4. Native Assimp stores translation in the 4th
    // column (M14/M24/M34), while System.Numerics/OpenTK row-vector matrices
    // expect it in the 4th row (M41/M42/M43).
    //
    // Transpose ONCE at the boundary. After this, all viewer math can use normal
    // System.Numerics/OpenTK row-vector conventions consistently.
    public static System.Numerics.Matrix4x4 ToNumerics(Assimp.Matrix4x4 m)
        => System.Numerics.Matrix4x4.Transpose(m);

    public static Matrix4 ToOpenTk(System.Numerics.Matrix4x4 assimpMatrix)
    {
        var m = System.Numerics.Matrix4x4.Transpose(assimpMatrix);
        return new Matrix4(
            m.M11, m.M12, m.M13, m.M14,
            m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34,
            m.M41, m.M42, m.M43, m.M44);
    }

    public static Vector3 ToOpenTk(System.Numerics.Vector3 v)
        => new(v.X, v.Y, v.Z);

    public static Quaternion ToOpenTk(System.Numerics.Quaternion q)
        => new(q.X, q.Y, q.Z, q.W);
}
