using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Reflection;
using AS.FBXReader.Core;
using AS.FBXReader.Models;
using AS.FBXReader.Batch;
using AS.FBXReader.Rendering;
using System.Drawing;

static void Check(bool result, string description)
{
    if (!result)
        throw new Exception("VLote smoke FAILED: " + description);
    Console.WriteLine("PASS: " + description);
}
// Crash regression: the VLote queue imports with no native postprocessing.
// AssimpNetter 6.0.5 can double-free a scene when its validator fails.
// Renderer/picker triangulate the original FBX polygons in managed memory.
var singleTri = MeshTriangleIndices.FromFaces(
    new IReadOnlyList<int>[] { new[] { 0, 1, 2 } }, 3);
Check(singleTri.SequenceEqual(new uint[] { 0, 1, 2 }),
    "safe FBX renderer preserves original triangles");
var quadTri = MeshTriangleIndices.FromFaces(
    new IReadOnlyList<int>[] { new[] { 0, 1, 2, 3 } }, 4);
Check(quadTri.SequenceEqual(new uint[] { 0, 1, 2, 0, 2, 3 }),
    "safe FBX renderer triangulates a quad without Assimp native postprocessing");
var mixedTri = MeshTriangleIndices.FromFaces(
    new IReadOnlyList<int>[] {
        new[] { 0, 1, 2, 3, 4 },
        new[] { 7, 1, 2 },
        new[] { 0, 0, 1 },
        new[] { 1, 2 },
        Array.Empty<int>() }, 5);
Check(mixedTri.SequenceEqual(new uint[] { 0, 1, 2, 0, 2, 3, 0, 3, 4 }),
    "safe FBX renderer rejects invalid, degenerate and non-polygon faces");

// Regression: opening FBX B after selecting take 8 in FBX A (12 takes)
 // must never try to read take 8 from B when B has only 3 takes.
Check(AnimationPlayer.NormalizeAnimationIndex(8, 3, 3) == -1,
    "switching from 12-take FBX to 3-take FBX drops stale selected index");
Check(AnimationPlayer.NormalizeAnimationIndex(2, 3, 3) == 2,
    "valid selected take remains intact");
Check(AnimationPlayer.NormalizeAnimationIndex(2, 3, 1) == -1,
    "mismatched imported/indexed animation catalogs are safe");
Check(AnimationPlayer.NormalizeAnimationIndex(-1, 3, 3) == -1,
    "rest pose stays valid when switching FBX");
Check(AnimationPlayer.NormalizeAnimationIndex(0, 0, 0) == -1,
    "zero-animation FBX is safe");

using (var emptySession = new FbxSession())
{
    var newPlayer = new AnimationPlayer(emptySession);
    newPlayer.SelectAnimation(8);
    Check(newPlayer.AnimationIndex == -1,
        "selecting a stale take against an empty model stays in rest pose");
    newPlayer.Playing = true;
    newPlayer.Rebuild();
    Check(newPlayer.AnimationIndex == -1 && !newPlayer.Playing,
        "rebuilding a model clears stale take and playback state");
}

// The same center-crop geometry must be used for the live OpenGL preview
// and all offscreen export resolutions. No distortion, no letterbox.
var imageSquare = BackgroundImageLayout.Cover(1024, 1024, 1024, 1024);
Check(Math.Abs(imageSquare.MinU) < 0.00001f &&
      Math.Abs(imageSquare.MaxU - 1f) < 0.00001f &&
      Math.Abs(imageSquare.MinV) < 0.00001f &&
      Math.Abs(imageSquare.MaxV - 1f) < 0.00001f,
      "square background uses the full source image");
var imageWide = BackgroundImageLayout.Cover(1920, 1080, 720, 1280);
Check(imageWide.MinU > 0.3f && imageWide.MaxU < 0.7f &&
      imageWide.MinV == 0f && imageWide.MaxV == 1f,
      "portrait output crops widescreen background horizontally");
var imageTall = BackgroundImageLayout.Cover(720, 1280, 1920, 1080);
Check(imageTall.MinV > 0.3f && imageTall.MaxV < 0.7f &&
      imageTall.MinU == 0f && imageTall.MaxU == 1f,
      "landscape output crops portrait background vertically");
var imageSameAspect = BackgroundImageLayout.Cover(3840, 2160, 1280, 720);
Check(Math.Abs(imageSameAspect.MinU) < 0.00001f &&
      Math.Abs(imageSameAspect.MaxV - 1f) < 0.00001f,
      "matching background and export aspect ratios use the full image");
var transparentOpts = new AS.FBXReader.Export.ExportSettings();
Check(transparentOpts.TransparentBackground &&
      transparentOpts.BackgroundImagePath is null,
      "legacy transparent output defaults preserved");

// Actual FBX sample (DesertFox_Dead_Character) encodes mutually exclusive
// eyes/mouth/limbs as 0/1 XY scale keys, while Z is reserved for draw order.
// Regression: never interpolate a half-visible ghost between animation keys.
var visibilityType = typeof(AnimationPlayer);
var detectSwitch = visibilityType.GetMethod("IsVisibilityCurve",
    BindingFlags.Static | BindingFlags.NonPublic)!;
var sampleSwitch = visibilityType.GetMethod("SampleSteppedVector",
    BindingFlags.Static | BindingFlags.NonPublic)!;
var scaleVisible = visibilityType.GetMethod("IsVisibleScale",
    BindingFlags.Static | BindingFlags.NonPublic)!;
var spriteKeys = new List<Assimp.VectorKey>
{
    new(0, new System.Numerics.Vector3(0f, 0f, 0.042f)),
    new(10, new System.Numerics.Vector3(1f, 1f, 0.043f)),
    new(20, new System.Numerics.Vector3(0f, 0f, 0.044f))
};
bool VisibleAt(double ticks)
{
    var sample = (OpenTK.Mathematics.Vector3)sampleSwitch.Invoke(null,
        [spriteKeys, ticks, OpenTK.Mathematics.Vector3.One])!;
    return (bool)scaleVisible.Invoke(null, [sample])!;
}
Check((bool)detectSwitch.Invoke(null, [spriteKeys])!,
    "SpriteRenderer visibility keys are recognized even with varying Z layers");
Check(!VisibleAt(0) && !VisibleAt(9.9) && VisibleAt(10) &&
      VisibleAt(19.9) && !VisibleAt(20),
    "SpriteRenderer visibility switches at keys without ghost interpolation");
Check((bool)scaleVisible.Invoke(null,
        [new OpenTK.Mathematics.Vector3(1f, 1f, 0f)])!,
    "sprite Z=0 is valid depth and must not hide its flat XY geometry");
var debugPart = new ScenePart { Id = "test", Name = "test",
    NodeName = "test", MeshIndex = 0 };
Check(!debugPart.ShouldRender("clip", 0, sourceVisible: false),
    "auto mode respects hidden animation state");
debugPart.ForceVisible = true;
Check(debugPart.ShouldRender("clip", 0, sourceVisible: false),
    "force visible continues to work as an explicit debugging override");

// Generated FBX 7.3 binary fixture. Test the authored Model defaults,
// independently from Assimp's first-frame imported node transforms.
var visibilityFixturePath = Path.Combine(Path.GetTempPath(),
    "fbx_visibility_fixture_" + Guid.NewGuid().ToString("N") + ".fbx");
try
{
    File.WriteAllBytes(visibilityFixturePath, Convert.FromBase64String(
        "S2F5ZGFyYSBGQlggQmluYXJ5ICAAGgCEHAAAqgIAAAAAAAAAAAAAB09iamVjdHNnAAAAAwAAACYAAAAFTW9kZWxMahAAAAAAAABTDwAAAE1lc2hCb2R5AAFNb2RlbFMEAAAATWVzaC0BAAADAAAALQAAAAVNb2RlbExrEAAAAAAAAFMWAAAATWVzaEhpZGRlblNjYWxlAAFNb2RlbFMEAAAATWVzaCABAAAAAAAAAAAAAAxQcm9wZXJ0aWVzNzATAQAABwAAAEYAAAABUFMLAAAATGNsIFNjYWxpbmdTCwAAAExjbCBTY2FsaW5nUwAAAABTAQAAAEFEAAAAAAAAAABEAAAAAAAAAABEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAN0BAAADAAAAKwAAAAVNb2RlbExsEAAAAAAAAFMUAAAATWVzaEhpZGRlblZpcwABTW9kZWxTBAAAAE1lc2jQAQAAAAAAAAAAAAAMUHJvcGVydGllczcwwwEAAAUAAAAyAAAAAVBTCgAAAFZpc2liaWxpdHlTCgAAAFZpc2liaWxpdHlTAAAAAFMBAAAAQUQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAnQIAAAMAAAAnAAAABU1vZGVsTG0QAAAAAAAAUxAAAABNZXNoRmxhdFoAAU1vZGVsUwQAAABNZXNokAIAAAAAAAAAAAAADFByb3BlcnRpZXM3MIMCAAAHAAAARgAAAAFQUwsAAABMY2wgU2NhbGluZ1MLAAAATGNsIFNjYWxpbmdTAAAAAFMBAAAAQUQAAAAAAADwP0QAAAAAAADwP0QAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="));
    var authored = FbxAuthoredVisibility.Read(visibilityFixturePath);
    Check(authored.Count == 4, "read four authored FBX model default states");
    Check(authored["MeshBody"], "missing scale/visibility means shown by default");
    Check(!authored["MeshHiddenScale"], "authored scale zero hides sprite");
    Check(!authored["MeshHiddenVis"], "authored Visibility 0 hides sprite");
    Check(authored["MeshFlatZ"], "draw-layer Z=0 must not hide XY sprite");
}
finally
{
    if (File.Exists(visibilityFixturePath))
        File.Delete(visibilityFixturePath);
}

FbxRawVisibilitySmoke.Run(Check);

// Match Live2D v0.1.10: no automatic output path, no folder until
// encoding starts, and every FBX uses its filename (not UI display name).
var rootOut = Path.Combine(Path.GetTempPath(), "fbx_fixed_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(rootOut);
try
{
    Check(AS.FBXReader.Batch.FixedOutputFolder.ModelFolderName(
        @"C:\\models\\애니메이션01.fbx") == "애니메이션01",
        "fixed output preserves Korean FBX filename");
    Check(AS.FBXReader.Batch.FixedOutputFolder.ModelFolderName("Scene_A.FBX") == "Scene_A",
        "FBX model folder uses source filename stem case-insensitively");
    var pathOut = AS.FBXReader.Batch.FixedOutputFolder.ForModel(rootOut, "Lina Character.fbx");
    Check(pathOut == Path.Combine(rootOut, "Lina Character"),
        "fixed root exports into exact model-FBX named subfolder");
    Check(!Directory.Exists(pathOut),
        "preview never creates output folders before encoding");
    var blankFails = false;
    try { AS.FBXReader.Batch.FixedOutputFolder.ForModel(" ", "A.fbx"); }
    catch (ArgumentException e) { blankFails = e.Message.Contains("não definida"); }
    Check(blankFails, "blank fixed output requires explicit selection");
    var missingFails = false;
    try { AS.FBXReader.Batch.FixedOutputFolder.ForModel(
        Path.Combine(rootOut, "missing"), "A.fbx"); }
    catch (DirectoryNotFoundException) { missingFails = true; }
    Check(missingFails, "non-existing fixed root is rejected");
    Directory.CreateDirectory(pathOut);
    Check(AS.FBXReader.Batch.FixedOutputFolder.AvailableDestination(pathOut) ==
        pathOut + " (1)", "existing model outputs are not silently overwritten");
}
finally { if (Directory.Exists(rootOut)) Directory.Delete(rootOut, true); }


var root = Path.Combine(Path.GetTempPath(), "fbx_vlote_smoke_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var alpha = Path.Combine(root, "Alpha");
    Directory.CreateDirectory(Path.Combine(alpha, "nested"));
    File.WriteAllText(Path.Combine(alpha, "one.fbx"), "dummy");
    File.WriteAllText(Path.Combine(alpha, "nested", "TWO.FBX"), "dummy");
    File.WriteAllText(Path.Combine(alpha, "texture.png"), "shared texture");

    foreach (var skipped in new[] { "_OK", "_Null", "_EXT" })
    {
        Directory.CreateDirectory(Path.Combine(root, skipped));
        File.WriteAllText(Path.Combine(root, skipped, "ignored.fbx"), "dummy");
    }
    File.WriteAllText(Path.Combine(root, "root-only.fbx"), "must not move root");

    var scan = new VLoteQueue(root);
    Check(scan.Total == 2 && scan.Remaining == 2, "recursive discovery skips root and excluded folders");
    var path = scan.ExportDestination(scan.Items[0]);
    Check(path.StartsWith(Path.Combine(root, "_EXT", "Alpha"), StringComparison.OrdinalIgnoreCase),
        "exports are directed under _EXT / group / model");
    scan.Complete(scan.Items[0], BatchDecision.OK);
    Check(Directory.Exists(alpha) && scan.Remaining == 1,
        "group remains until every FBX has a decision");

    var resumed = new VLoteQueue(root);
    Check(resumed.Total == 2 && resumed.Remaining == 1 && resumed.Finished == 1,
        "progress survives re-scanning with one model pending");
    resumed.Complete(resumed.Current!, BatchDecision.OK);
    Check(!Directory.Exists(alpha) && Directory.Exists(Path.Combine(root, "_OK", "Alpha")),
        "completed homogeneous folder moves to _OK");
    Check(File.Exists(Path.Combine(root, "_OK", "Alpha", "texture.png")),
        "texture and sidecar resources are moved with the folder");

    var beta = Path.Combine(root, "Beta");
    Directory.CreateDirectory(beta);
    File.WriteAllText(Path.Combine(beta, "ok.fbx"), "dummy");
    File.WriteAllText(Path.Combine(beta, "bad.fbx"), "dummy");
    File.WriteAllText(Path.Combine(beta, "atlas.png"), "shared");
    var mixed = new VLoteQueue(root);
    var ok = mixed.Items.Single(e => e.Name == "ok.fbx");
    var bad = mixed.Items.Single(e => e.Name == "bad.fbx");
    mixed.Complete(ok, BatchDecision.OK);
    mixed.Complete(bad, BatchDecision.Null);
    Check(!Directory.Exists(beta), "source group retired after a mixed result");
    Check(File.Exists(Path.Combine(root, "_OK", "Beta", "ok.fbx")) &&
          !File.Exists(Path.Combine(root, "_OK", "Beta", "bad.fbx")),
          "OK branch only contains OK FBX");
    Check(File.Exists(Path.Combine(root, "_Null", "Beta", "bad.fbx")) &&
          !File.Exists(Path.Combine(root, "_Null", "Beta", "ok.fbx")),
          "NULL branch only contains NULL FBX");
    Check(File.Exists(Path.Combine(root, "_OK", "Beta", "atlas.png")) &&
          File.Exists(Path.Combine(root, "_Null", "Beta", "atlas.png")),
          "shared textures are preserved in both mixed destinations");

    var gamma = Path.Combine(root, "Gamma");
    Directory.CreateDirectory(gamma);
    File.WriteAllText(Path.Combine(gamma, "one.fbx"), "dummy");
    Directory.CreateDirectory(Path.Combine(root, "_OK", "Gamma"));
    var collision = new VLoteQueue(root);
    collision.Complete(collision.Current!, BatchDecision.OK);
    Check(Directory.Exists(Path.Combine(root, "_OK", "Gamma (1)")),
        "existing destination folders are never overwritten");
    // A Windows program can keep the source folder open without FILE_SHARE_DELETE.
    // A failed Directory.Move must publish a safe copy and preserve the original.
    var locked = Path.Combine(root, "Locked");
    Directory.CreateDirectory(locked);
    File.WriteAllText(Path.Combine(locked, "locked.fbx"), "keep source");
    File.WriteAllText(Path.Combine(locked, "atlas.png"), "keep texture");
    using (DirectoryTestLock.Hold(locked))
    {
        var lockedQueue = new VLoteQueue(root);
        var lockedItem = lockedQueue.Items.Single(e => e.Name == "locked.fbx");
        var status = lockedQueue.Complete(lockedItem, BatchDecision.OK);
        // Windows may permit a directory rename with an open folder handle;
        // both atomic MOVE and safe COPY are valid outcomes.
        var usedCopy = status.Contains("COPIADO:", StringComparison.Ordinal);
        Check(usedCopy ? File.Exists(Path.Combine(locked, "locked.fbx")) :
                         !Directory.Exists(locked),
            "source is retained if copied, removed only after atomic move");
        Check(File.Exists(Path.Combine(root, "_OK", "Locked", "locked.fbx")) &&
              File.Exists(Path.Combine(root, "_OK", "Locked", "atlas.png")),
            "locked source archived completely with textures");
        Check(new VLoteQueue(root).Remaining == 0,
            "copied locked source remains completed after a queue restart");
    }

    // Mixed results must also survive a locked source: never roll back both
    // committed copies just because deleting the source was denied.
    var mixedLocked = Path.Combine(root, "MixedLocked");
    Directory.CreateDirectory(mixedLocked);
    File.WriteAllText(Path.Combine(mixedLocked, "accepted.fbx"), "ok");
    File.WriteAllText(Path.Combine(mixedLocked, "rejected.fbx"), "null");
    File.WriteAllText(Path.Combine(mixedLocked, "shared.png"), "shared");
    using (DirectoryTestLock.Hold(mixedLocked))
    {
        var mixedQueue = new VLoteQueue(root);
        mixedQueue.Complete(mixedQueue.Items.Single(e => e.Name == "accepted.fbx"), BatchDecision.OK);
        var status = mixedQueue.Complete(
            mixedQueue.Items.Single(e => e.Name == "rejected.fbx"), BatchDecision.Null);
        Check(status.Contains("misto", StringComparison.Ordinal),
            "mixed locked folder completes classification");
        Check(File.Exists(Path.Combine(root, "_OK", "MixedLocked", "accepted.fbx")) &&
              File.Exists(Path.Combine(root, "_Null", "MixedLocked", "rejected.fbx")) &&
              File.Exists(Path.Combine(root, "_OK", "MixedLocked", "shared.png")) &&
              File.Exists(Path.Combine(root, "_Null", "MixedLocked", "shared.png")),
            "mixed locked folder preserves both committed archives");
        Check(new VLoteQueue(root).Remaining == 0,
            "mixed locked source is not reprocessed after restart");
    }
    Check(File.Exists(Path.Combine(root, "root-only.fbx")), "root-level source remains untouched");
    Check(new VLoteQueue(root).Remaining == 0, "completed models are skipped on next scan");
    // The main OpenGL preview content and the offscreen export must use
    // the SAME aspect ratio; users can select square, landscape or portrait.
    var host = new Size(1200, 700);
    foreach (var (width, height, format) in new[]
    {
        (1024, 1024, "1:1 square"),
        (1920, 1080, "16:9 landscape"),
        (720, 1280, "9:16 portrait"),
        (2048, 1536, "4:3 landscape"),
        (1536, 2048, "3:4 portrait")
    })
    {
        var box = OutputAspectLayout.Fit(host, width, height);
        int insideW = box.Width - 4; // 2px border left and right
        int insideH = box.Height - 4;
        Check(box.Left >= 0 && box.Top >= 0 &&
              box.Right <= host.Width && box.Bottom <= host.Height,
              "preview fits host: " + format);
        Check(insideW > 0 && insideH > 0 &&
              Math.Abs(insideW / (double)insideH - width / (double)height) < .015,
              "preview content matches export aspect: " + format);
        Check(Math.Abs((box.Left + box.Right) / 2d - host.Width / 2d) <= 1 &&
              Math.Abs((box.Top + box.Bottom) / 2d - host.Height / 2d) <= 1,
              "preview is centered: " + format);
    }

    foreach (var small in new[] { new Size(20, 20), new Size(0, 0), new Size(640, 300) })
    {
        var rect = OutputAspectLayout.Fit(small, 1024, 1024);
        Check(rect.Left >= 0 && rect.Top >= 0 &&
              rect.Right <= small.Width && rect.Bottom <= small.Height,
              "preview handles small/empty window " + small);
    }
    try
    {
        OutputAspectLayout.Fit(host, 0, 720);
        throw new Exception("Zero-sized export resolution should be invalid.");
    }
    catch (ArgumentOutOfRangeException) { }
    Console.WriteLine("VLote smoke PASS.");
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

internal static class DirectoryTestLock
{
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint OpenExisting = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string name, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    public static SafeFileHandle Hold(string path)
    {
        // Share read/write, but do not share DELETE: Windows must refuse a
        // folder rename while allowing the VLote fallback to copy its files.
        var handle = CreateFile(path, 0, 3, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException("Could not lock test folder: " + path +
                " / Win32=" + Marshal.GetLastWin32Error());
        return handle;
    }
}
