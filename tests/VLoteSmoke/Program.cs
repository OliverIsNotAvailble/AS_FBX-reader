using AS.FBXReader.Batch;
using AS.FBXReader.Rendering;
using System.Drawing;

static void Check(bool result, string description)
{
    if (!result)
        throw new Exception("VLote smoke FAILED: " + description);
    Console.WriteLine("PASS: " + description);
}
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
