using Assimp;
using OpenTK.Mathematics;

namespace AS.FBXReader.Core;

public static class MatrixUtil
{
    public static Matrix4 ToOpenTk(Assimp.Matrix4x4 m)
        => new(
            m.A1, m.A2, m.A3, m.A4,
            m.B1, m.B2, m.B3, m.B4,
            m.C1, m.C2, m.C3, m.C4,
            m.D1, m.D2, m.D3, m.D4);

    public static Vector3 ToOpenTk(Vector3D v) => new(v.X, v.Y, v.Z);

    public static Quaternion ToOpenTk(Assimp.Quaternion q)
        => new(q.X, q.Y, q.Z, q.W);
}
