using OpenTK.Mathematics;

namespace AS.FBXReader.Core;

public static class MatrixUtil
{
    // AssimpNetter 6.x uses System.Numerics for its math types.
    public static Matrix4 ToOpenTk(System.Numerics.Matrix4x4 m)
        => new(
            m.M11, m.M12, m.M13, m.M14,
            m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34,
            m.M41, m.M42, m.M43, m.M44);

    public static Vector3 ToOpenTk(System.Numerics.Vector3 v)
        => new(v.X, v.Y, v.Z);

    public static Quaternion ToOpenTk(System.Numerics.Quaternion q)
        => new(q.X, q.Y, q.Z, q.W);
}
