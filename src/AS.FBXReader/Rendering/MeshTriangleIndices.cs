using Assimp;

namespace AS.FBXReader.Rendering;

/// <summary>
/// Triangulates Assimp FBX face lists in managed memory. The VLote queue
/// avoids native aiApplyPostProcessing, whose error cleanup can double-free
/// scenes in AssimpNetter 6.0.5.
/// </summary>
public static class MeshTriangleIndices
{
    public static uint[] FromMesh(Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return FromFaces(mesh.Faces.Select(face =>
            (IReadOnlyList<int>)face.Indices), mesh.VertexCount);
    }

    public static uint[] FromFaces(IEnumerable<IReadOnlyList<int>> faces, int vertexCount)
    {
        ArgumentNullException.ThrowIfNull(faces);
        var output = new List<uint>();
        foreach (var face in faces)
        {
            if (face is null || face.Count < 3)
                continue;

            var first = face[0];
            if ((uint)first >= (uint)vertexCount)
                continue;

            // Preserve existing triangles, triangulate larger polygons by fan.
            // Invalid/degenerate triangles never reach the GPU.
            for (var i = 1; i + 1 < face.Count; i++)
            {
                var second = face[i];
                var third = face[i + 1];
                if ((uint)second >= (uint)vertexCount ||
                    (uint)third >= (uint)vertexCount ||
                    first == second || first == third || second == third)
                    continue;

                output.Add((uint)first);
                output.Add((uint)second);
                output.Add((uint)third);
            }
        }
        return output.ToArray();
    }
}
