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

// Guard against finite but absurd cluster bind offsets that stretch sprites.
var meshBind = OpenTK.Mathematics.Matrix4.CreateTranslation(1.5f, -0.2f, 0f);
var boneBind = OpenTK.Mathematics.Matrix4.CreateTranslation(1.8f, 0.4f, 0f);
var good = meshBind * boneBind.Inverted();
Check(!BindOffsetRecovery.NeedsRepair(good, meshBind, boneBind),
    "healthy skin offset is preserved");
var invalid = OpenTK.Mathematics.Matrix4.CreateScale(540f) *
    OpenTK.Mathematics.Matrix4.CreateTranslation(500f, -250f, 0f);
Check(BindOffsetRecovery.NeedsRepair(invalid, meshBind, boneBind),
    "finite oversized skin offset is detected");
Check(BindOffsetRecovery.TryRepair(meshBind, boneBind, out var recovered) &&
      !BindOffsetRecovery.NeedsRepair(recovered, meshBind, boneBind),
    "reconstructed bind restores original 2D mesh without stretching");
var slightlyOff = good;
slightlyOff.M41 += 0.01f;
Check(!BindOffsetRecovery.NeedsRepair(slightlyOff, meshBind, boneBind),
    "small numeric drift is tolerated");
Check(BindOffsetRecovery.NeedsRepair(new OpenTK.Mathematics.Matrix4(),
    meshBind, boneBind), "singular offset is identified");

// New per-mesh Edit mesh controls must be non-destructive and preserve
// the original XY silhouette while an animated rig is tilted out of plane.
var rawLeg = new[] {
    new OpenTK.Mathematics.Vector3(0f, 0f, 0f),
    new OpenTK.Mathematics.Vector3(4f, 0f, 0f),
    new OpenTK.Mathematics.Vector3(0f, 2f, 0f),
};
var sourceLeg = rawLeg.ToArray();
var badlyTilted = OpenTK.Mathematics.Matrix4.CreateRotationX(
    -MathF.PI / 2f) * OpenTK.Mathematics.Matrix4.CreateTranslation(5f, 3f, 0f);
var straightLeg = MeshPoseCorrection.Undeform(rawLeg, badlyTilted,
    OpenTK.Mathematics.Matrix4.Identity,
    new OpenTK.Mathematics.Vector3(2f, -1f, 0f));
Check(MathF.Abs((straightLeg[1] - straightLeg[0]).Length - 4f) < 0.0001f &&
      MathF.Abs((straightLeg[2] - straightLeg[0]).Length - 2f) < 0.0001f &&
      MathF.Abs(straightLeg[2].Y - straightLeg[0].Y - 2f) < 0.0001f,
    "Reset deform keeps source-image shape despite -90deg bone tilt");
Check(rawLeg.SequenceEqual(sourceLeg),
    "Reset deform never changes source vertices");

// Regression for the actual cg2.fbx layout: root cg2 has 530x scale
// and -180X / 180Z rotation (horizontal X mirror), while 42Head has
// a local ~90-degree X tilt. Both root scale and mirror MUST survive Reset.
// Prior implementation dropped the 530x scale and the X sign, making
// 42Head look fine alone (auto-fit) but effectively invisible with Face4.
var cg2Parent = OpenTK.Mathematics.Matrix4.CreateScale(-530f, 530f, -1f);
var cg2HeadRest = OpenTK.Mathematics.Matrix4.CreateRotationX(
    MathF.PI / 2f) *
    OpenTK.Mathematics.Matrix4.CreateTranslation(0f, 0.09f, 0f) *
    cg2Parent;
var cg2HeadCorners = new[] {
    new OpenTK.Mathematics.Vector3(0.8831979f, 0.39272803f, 0f),
    new OpenTK.Mathematics.Vector3(1.32479751f, 0.92291039f, 0f),
};
var cg2Restored = MeshPoseCorrection.Undeform(cg2HeadCorners, cg2HeadRest,
    cg2Parent, OpenTK.Mathematics.Vector3.Zero);
var realHeadWidth = (1.32479751f - 0.8831979f) * 530f;
var realHeadHeight = (0.92291039f - 0.39272803f) * 530f;
Check(MathF.Abs(MathF.Abs(cg2Restored[1].X - cg2Restored[0].X) -
                realHeadWidth) < 0.02f &&
      MathF.Abs(cg2Restored[1].Y - cg2Restored[0].Y -
                realHeadHeight) < 0.02f,
    "cg2 42Head Reset keeps original 530x shape size (not subpixel with Face4)");
Check(cg2Restored[1].X < cg2Restored[0].X,
    "cg2 Reset keeps existing root X mirror instead of flipping orientation");
var cg2FaceCenter = OpenTK.Mathematics.Vector3.TransformPosition(
    new OpenTK.Mathematics.Vector3(1.05f, 0.64f, 0f), cg2Parent);
Check(cg2FaceCenter.X >= MathF.Min(cg2Restored[0].X, cg2Restored[1].X) &&
      cg2FaceCenter.X <= MathF.Max(cg2Restored[0].X, cg2Restored[1].X) &&
      cg2FaceCenter.Y >= MathF.Min(cg2Restored[0].Y, cg2Restored[1].Y) &&
      cg2FaceCenter.Y <= MathF.Max(cg2Restored[0].Y, cg2Restored[1].Y),
    "cg2 Face4 position overlaps Reset-corrected 42Head bounds");
var cg2Move = MeshPoseCorrection.Undeform(cg2HeadCorners, cg2HeadRest,
    cg2Parent, new OpenTK.Mathematics.Vector3(24f, -12f, 0f));
Check(MathF.Abs(cg2Move[0].X - cg2Restored[0].X - 24f) < 0.001f &&
      MathF.Abs(cg2Move[0].Y - cg2Restored[0].Y + 12f) < 0.001f,
    "cg2 corrected mesh still follows animated bone translation");
// Reset deform motion must follow the mesh's point on the rig, not the
// bone's global ORIGIN. Rotating a bone in-place leaves M41/M42 unchanged,
// which made the old implementation animate the head in the wrong direction
// (or not at all) with cg2's pivot and mirrored hierarchy.
var pivotRest = OpenTK.Mathematics.Matrix4.CreateTranslation(10f, 20f, 0f);
var pivotAnimated = OpenTK.Mathematics.Matrix4.CreateRotationZ(
    MathF.PI / 2f) * pivotRest;
var attachmentRest = new OpenTK.Mathematics.Vector3(12f, 20f, 0f);
var attachmentMovement = MeshPoseCorrection.MotionAtAnchor(
    pivotRest, pivotAnimated, attachmentRest);
Check(MathF.Abs(pivotAnimated.M41 - pivotRest.M41) < 0.0001f &&
      MathF.Abs(pivotAnimated.M42 - pivotRest.M42) < 0.0001f &&
      MathF.Abs(attachmentMovement.X + 2f) < 0.0001f &&
      MathF.Abs(attachmentMovement.Y - 2f) < 0.0001f,
    "Reset follows actual anchor after bone rotates even when bone origin stays still");

// The same calculation must behave correctly with cg2's negative X scale,
// and leave the restored sprite silhouette unchanged.
var mirroredRest = OpenTK.Mathematics.Matrix4.CreateScale(-530f, 530f, 1f);
var mirroredAnimated = OpenTK.Mathematics.Matrix4.CreateRotationZ(
    MathF.PI / 12f) * mirroredRest;
var mirroredAnchor = new OpenTK.Mathematics.Vector3(-600f, 350f, 0f);
var mirrorDelta = MeshPoseCorrection.MotionAtAnchor(
    mirroredRest, mirroredAnimated, mirroredAnchor);
var localAnchor = OpenTK.Mathematics.Vector3.TransformPosition(
    mirroredAnchor, mirroredRest.Inverted());
var expectedWorld = OpenTK.Mathematics.Vector3.TransformPosition(
    localAnchor, mirroredAnimated);
Check((mirroredAnchor + mirrorDelta - expectedWorld).Length < 0.0001f,
    "Reset anchor follows rig correctly under negative-X parent scale");
var onlyTranslation = MeshPoseCorrection.MotionAtAnchor(
    pivotRest, OpenTK.Mathematics.Matrix4.CreateTranslation(13f, 16f, 0f),
    attachmentRest);
Check(MathF.Abs(onlyTranslation.X - 3f) < 0.0001f &&
      MathF.Abs(onlyTranslation.Y + 4f) < 0.0001f,
    "Reset follows an ordinary translation unchanged");
var afterResetMovement = cg2Restored.Select(p => p + mirrorDelta).ToArray();
Check((afterResetMovement[1] - afterResetMovement[0] -
       (cg2Restored[1] - cg2Restored[0])).Length < 0.0001f,
    "Reset bone motion translates the 530x head without deformation");

var rotatedLeg = straightLeg.ToArray();
MeshPoseCorrection.RotateAroundCenter(rotatedLeg, 90f);
Check(MathF.Abs((rotatedLeg[1] - rotatedLeg[0]).Length -
                (straightLeg[1] - straightLeg[0]).Length) < 0.0001f &&
      MathF.Abs(rotatedLeg[1].X - rotatedLeg[0].X) < 0.0001f,
    "90deg per-mesh rotation preserves silhouette edge lengths");
MeshPoseCorrection.RotateAroundCenter(rotatedLeg, -90f);
Check((rotatedLeg[1] - straightLeg[1]).Length < 0.0001f &&
      (rotatedLeg[2] - straightLeg[2]).Length < 0.0001f,
    "rotating back restores the exact previous placement");
var otherPart = new ScenePart {
    Id = "other", Name = "other", NodeName = "node", MeshIndex = 0
};
Check(!otherPart.ResetDeform && otherPart.MeshRotationDegrees == 0f,
    "per-part corrections default to off and do not affect siblings");
var adjusted = new MeshAdjustmentProfile {
    ResetDeform = true, MeshRotationDegrees = -15f
};
var encoded = System.Text.Json.JsonSerializer.Serialize(adjusted);
var decoded = System.Text.Json.JsonSerializer.Deserialize<MeshAdjustmentProfile>(encoded)!;
Check(decoded.ResetDeform && decoded.MeshRotationDegrees == -15f,
    "mesh reset and rotation persist as independent profile settings");

// Flip X is applied AFTER evaluating the skinned animation, not instead of
// the skinning. Preserve the pose's height/deformation and all texture UVs.
var animatedMesh = new[] {
    new OpenTK.Mathematics.Vector3(1f, 5f, 0.1f),
    new OpenTK.Mathematics.Vector3(5f, 2f, 0.3f),
    new OpenTK.Mathematics.Vector3(10f, 9f, 0.4f)
};
var originalPose = animatedMesh.ToArray();
var originalCenter = animatedMesh.Average(p => p.X);
MeshPoseCorrection.FlipHorizontal(animatedMesh);
for (var i = 0; i < animatedMesh.Length; i++)
{
    Check(MathF.Abs(animatedMesh[i].X -
                     (2f * originalCenter - originalPose[i].X)) < 0.0001f &&
          animatedMesh[i].Y == originalPose[i].Y &&
          animatedMesh[i].Z == originalPose[i].Z,
        $"Flip horizontal changes only the animated mesh's X, vertex {i}");
}
MeshPoseCorrection.FlipHorizontal(animatedMesh);
Check(animatedMesh.Zip(originalPose).All(pair =>
        (pair.First - pair.Second).Length < 0.0001f),
    "flipping twice restores the exact animated mesh pose");
var rotatedThenFlipped = originalPose.ToArray();
MeshPoseCorrection.RotateAroundCenter(rotatedThenFlipped, 30f);
var rotatedPose = rotatedThenFlipped.ToArray();
MeshPoseCorrection.FlipHorizontal(rotatedThenFlipped);
Check(rotatedThenFlipped.Zip(rotatedPose).All(pair =>
        pair.First.Y == pair.Second.Y && pair.First.Z == pair.Second.Z),
    "flip horizontal also works after manual mesh rotation");
var unrelatedMesh = new ScenePart {
    Id = "other2", Name = "other2", NodeName = "node2", MeshIndex = 0
};
Check(!unrelatedMesh.MeshFlipHorizontal,
    "other meshes are not flipped by default");
var flippedProfile = new MeshAdjustmentProfile {
    MeshFlipHorizontal = true, MeshRotationDegrees = 15f
};
var flippedJson = System.Text.Json.JsonSerializer.Serialize(flippedProfile);
var readFlip = System.Text.Json.JsonSerializer.Deserialize<MeshAdjustmentProfile>(flippedJson)!;
Check(readFlip.MeshFlipHorizontal && readFlip.MeshRotationDegrees == 15f,
    "horizontal mesh flip persists independently of rotation");


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
