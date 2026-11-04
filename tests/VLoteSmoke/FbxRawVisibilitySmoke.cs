using AS.FBXReader.Core;
using OpenTK.Mathematics;

internal static class FbxRawVisibilitySmoke
{
    // Tiny FBX 7.3 fixture with two animation stacks, one mesh with
    // independently keyed scale=0/1/0, and another mesh with NO scale
    // animation. Test the original compressed KeyTime/KeyValueFloat format.
    private const string Fixture = "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";

    public static void Run(Action<bool, string> check)
    {
        var file = Path.Combine(Path.GetTempPath(),
            "fbx_authored_scale_" + Guid.NewGuid().ToString("N") + ".fbx");
        try
        {
            File.WriteAllBytes(file, Convert.FromBase64String(Fixture));
            var tracks = FbxRawScaleVisibility.Read(file);
            check(tracks.AnimationCount == 2 && tracks.TrackCount == 2,
                "read two original animation stacks with independently linked scale curves");
            var one = Vector3.One;
            check(tracks.TrySampleScale(0, "SpriteA", 0, one, out var at0) &&
                  Math.Abs(at0.X) < 0.001f &&
                  Math.Abs(at0.Y) < 0.001f,
                "first take starts with hidden SpriteA");
            check(tracks.TrySampleScale(0, "SpriteA", 0.5, one, out var atHalf) &&
                  Math.Abs(atHalf.X) < 0.001f,
                "visibility remains hidden until authored switch: no ghost interpolation");
            check(tracks.TrySampleScale(0, "SpriteA", 1.0, one, out var at1) &&
                  Math.Abs(at1.X - 1f) < 0.001f,
                "first take enables SpriteA exactly at 1 second");
            check(tracks.TrySampleScale(0, "SpriteA", 2.0, one, out var at2) &&
                  Math.Abs(at2.X) < 0.001f,
                "first take hides SpriteA again at 2 seconds");
            check(tracks.TrySampleScale(1, "SpriteA", 0, one, out var other) &&
                  Math.Abs(other.X - 1f) < 0.001f,
                "second animation's visibility is independent from the first");
            check(!tracks.TrySampleScale(0, "SpriteB", 0, one, out _),
                "mesh without authored keys is not given invented 0-scale curves");
            check(tracks.TrySampleScale(1, "SpriteA", 0, one, out var named, "Take One") &&
                  Math.Abs(named.X) < 0.001f,
                "authored take name overrides a reordered Assimp animation index");
        }
        finally
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }
}
