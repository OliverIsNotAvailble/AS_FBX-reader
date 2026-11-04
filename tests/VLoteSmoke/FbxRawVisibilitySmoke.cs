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
            var authored = FbxAuthoredVisibility.Read(file);
            check(!authored["__spriteskin_Animated"] &&
                  !authored["__spriteskin_Static"],
                "both imported skinned meshes start with authored zero-scale rest pose");
            check(tracks.HasAnyScaleCurve("__spriteskin_Animated") &&
                  !tracks.HasAnyScaleCurve("__spriteskin_Static"),
                "only the animated SpriteSkin has 0/1 curves across the takes");
            check(tracks.IsNeverKeyedCollapsedSpriteSkin("__spriteskin_Static", true),
                "never-keyed collapsed body SpriteSkin is recoverable");
            check(!tracks.IsNeverKeyedCollapsedSpriteSkin("__spriteskin_Animated", true),
                "animated alternative must keep its explicit visibility switches");
            check(!tracks.IsNeverKeyedCollapsedSpriteSkin("__spriteskin_Static", false) &&
                  !tracks.IsNeverKeyedCollapsedSpriteSkin("__sprite_Static", true),
                "do not force healthy transforms or non-skinned overlays");
            var one = Vector3.One;
            check(tracks.TrySampleScale(0, "__spriteskin_Animated", 0, one, out var at0) &&
                  Math.Abs(at0.X) < 0.001f &&
                  Math.Abs(at0.Y) < 0.001f,
                "first take starts with hidden SpriteA");
            check(tracks.TrySampleScale(0, "__spriteskin_Animated", 0.5, one, out var atHalf) &&
                  Math.Abs(atHalf.X) < 0.001f,
                "visibility remains hidden until authored switch: no ghost interpolation");
            check(tracks.TrySampleScale(0, "__spriteskin_Animated", 1.0, one, out var at1) &&
                  Math.Abs(at1.X - 1f) < 0.001f,
                "first take enables SpriteA exactly at 1 second");
            check(tracks.TrySampleScale(0, "__spriteskin_Animated", 2.0, one, out var at2) &&
                  Math.Abs(at2.X) < 0.001f,
                "first take hides SpriteA again at 2 seconds");
            check(tracks.TrySampleScale(1, "__spriteskin_Animated", 0, one, out var other) &&
                  Math.Abs(other.X - 1f) < 0.001f,
                "second animation's visibility is independent from the first");
            check(!tracks.TrySampleScale(0, "__spriteskin_Static", 0, one, out _),
                "mesh without authored keys is not given invented 0-scale curves");
            check(tracks.TrySampleScale(1, "__spriteskin_Animated", 0, one, out var named, "Take One") &&
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
