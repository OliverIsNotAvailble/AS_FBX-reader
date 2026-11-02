using AS.FBXReader.Batch;

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
    Console.WriteLine("VLote smoke PASS.");
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}
