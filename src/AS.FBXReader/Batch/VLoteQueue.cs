using System.Text.Json;

namespace AS.FBXReader.Batch;

public enum BatchDecision { Pending, OK, Null }

public sealed class BatchEntry
{
    public required string FilePath { get; init; }
    public required string GroupPath { get; init; }
    public BatchDecision Decision { get; set; }
    public string Name => Path.GetFileName(FilePath);
}

/// <summary>
/// VLote: one FBX at a time. A top-level child folder of the selected root
/// is moved only after ALL FBXs inside it are classified.
/// Separate export output is always under root/_EXT.
/// </summary>
public sealed class VLoteQueue
{
    private static readonly StringComparer Compare = StringComparer.OrdinalIgnoreCase;
    private readonly List<BatchEntry> _items = new();
    private readonly Dictionary<string, BatchDecision> _progress = new(Compare);
    public string Root { get; }
    public IReadOnlyList<BatchEntry> Items => _items;
    public int Total => _items.Count;
    public int Finished => _items.Count(x => x.Decision != BatchDecision.Pending);
    public int Remaining => Total - Finished;
    public BatchEntry? Current => _items.FirstOrDefault(x => x.Decision == BatchDecision.Pending);
    public string Counter => $"{Finished} / {Total} finalizados  •  {Remaining} restantes";

    public VLoteQueue(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Selecione uma pasta raiz antes de iniciar.", nameof(root));
        Root = Path.GetFullPath(root.Trim().Trim('"'))
                   .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(Root))
            throw new DirectoryNotFoundException(Root);

        ReadProgress();
        foreach (var child in Directory.EnumerateDirectories(Root)
                     .OrderBy(x => x, Compare))
        {
            if (Excluded(Path.GetFileName(child)) || IsLink(child))
                continue;
            Scan(child, child);
        }
        _items.Sort((a, b) => Compare.Compare(
            Path.GetRelativePath(Root, a.FilePath),
            Path.GetRelativePath(Root, b.FilePath)));
    }

    public static bool Excluded(string name) =>
        name.Equals("_OK", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("_Null", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("_EXT", StringComparison.OrdinalIgnoreCase);

    private static bool IsLink(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private void Scan(string folder, string group)
    {
        foreach (var file in Directory.EnumerateFiles(folder)
                     .OrderBy(x => x, Compare))
        {
            if (IsLink(file) || !file.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                continue;
            _items.Add(new BatchEntry
            {
                FilePath = file,
                GroupPath = group,
                Decision = _progress.GetValueOrDefault(
                    Path.GetRelativePath(Root, file), BatchDecision.Pending)
            });
        }
        foreach (var child in Directory.EnumerateDirectories(folder)
                     .OrderBy(x => x, Compare))
        {
            if (!Excluded(Path.GetFileName(child)) && !IsLink(child))
                Scan(child, group);
        }
    }

    /// <summary>Unique _EXT/group/model subfolder; avoids export collisions.</summary>
    public string ExportDestination(BatchEntry entry)
    {
        if (!_items.Contains(entry))
            throw new ArgumentException("FBX not part of current batch.", nameof(entry));
        var relative = Path.GetRelativePath(entry.GroupPath, entry.FilePath);
        var model = Path.ChangeExtension(relative, null)!
            .Replace(Path.DirectorySeparatorChar, '_')
            .Replace(Path.AltDirectorySeparatorChar, '_');
        foreach (var character in Path.GetInvalidFileNameChars())
            model = model.Replace(character, '_');
        var root = Path.Combine(Root, "_EXT", Path.GetFileName(entry.GroupPath));
        return UniquePath(root, model);
    }

    public static string UniquePath(string parent, string name)
    {
        var candidate = Path.Combine(parent, name);
        for (var n = 1; Directory.Exists(candidate) || File.Exists(candidate); n++)
            candidate = Path.Combine(parent, $"{name} ({n})");
        return candidate;
    }

    /// <summary>
    /// Mark a model; do not move a group until its final pending model is
    /// complete. If a move fails, restore the last decision to Pending.
    /// </summary>
    public string Complete(BatchEntry entry, BatchDecision decision)
    {
        if (decision == BatchDecision.Pending || !_items.Contains(entry) ||
            entry.Decision != BatchDecision.Pending)
            throw new InvalidOperationException("Invalid or already completed FBX.");

        entry.Decision = decision;
        SaveProgress();
        var group = _items.Where(x => Compare.Equals(x.GroupPath, entry.GroupPath))
                          .ToArray();
        var remaining = group.Count(x => x.Decision == BatchDecision.Pending);
        if (remaining > 0)
            return $"{entry.Name}: {decision}; aguardando {remaining} FBX da mesma pasta.";

        try
        {
            var output = MoveGroup(entry.GroupPath, group);
            foreach (var member in group)
                _progress.Remove(Path.GetRelativePath(Root, member.FilePath));
            // A successful folder move is authoritative; an unexpected
            // progress-write failure must NOT roll back a completed move.
            try { SaveProgress(); }
            catch (IOException ex)
            {
                return $"Pasta finalizada → {output} (aviso no progresso: {ex.Message})";
            }
            return $"Pasta finalizada → {output}";
        }
        catch
        {
            if (Directory.Exists(entry.GroupPath))
            {
                entry.Decision = BatchDecision.Pending;
                SaveProgress();
            }
            throw;
        }
    }

    private string MoveGroup(string source, BatchEntry[] group)
    {
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException(source);

        var allOk = group.All(x => x.Decision == BatchDecision.OK);
        var allNull = group.All(x => x.Decision == BatchDecision.Null);
        if (allOk || allNull)
        {
            var parent = Path.Combine(Root, allOk ? "_OK" : "_Null");
            var destination = UniquePath(parent, Path.GetFileName(source));
            Directory.CreateDirectory(parent);
            try
            {
                Directory.Move(source, destination);
                return destination;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Windows can refuse to rename a folder while another program
                // has a file handle open (Explorer, antivirus, FBX importer).
                // Copy to a staging directory and publish atomically instead.
                // NEVER delete the source in this fallback: it may still be
                // locked, and deleting recursively can destroy only part of it.
                if (!Directory.Exists(source))
                    throw;

                var stage = destination + ".vlote-stage-" + Guid.NewGuid().ToString("N");
                try
                {
                    CopyTree(source, stage);
                    Directory.Move(stage, destination);
                    return $"{destination} (COPIADO: pasta original mantida porque a movimentação foi bloqueada; " +
                           "apague a original manualmente quando não estiver mais em uso)";
                }
                finally
                {
                    if (Directory.Exists(stage))
                    {
                        try { Directory.Delete(stage, true); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }
        }

        // Mixed results: retain shared textures/sidecar files in BOTH copies.
        // Only the other category's .fbx is excluded from each copy.
        // Source is untouched until BOTH destinations are fully committed.
        var okParent = Path.Combine(Root, "_OK");
        var nullParent = Path.Combine(Root, "_Null");
        Directory.CreateDirectory(okParent);
        Directory.CreateDirectory(nullParent);
        var ok = UniquePath(okParent, Path.GetFileName(source));
        var bad = UniquePath(nullParent, Path.GetFileName(source));
        var stageOk = ok + ".vlote-stage-" + Guid.NewGuid().ToString("N");
        var stageBad = bad + ".vlote-stage-" + Guid.NewGuid().ToString("N");
        var movedOk = false;
        var movedBad = false;
        try
        {
            CopyTree(source, stageOk);
            CopyTree(source, stageBad);
            foreach (var item in group)
            {
                var excludeFrom = item.Decision == BatchDecision.OK ? stageBad : stageOk;
                var foreign = Path.Combine(excludeFrom,
                    Path.GetRelativePath(source, item.FilePath));
                if (File.Exists(foreign))
                    File.Delete(foreign);
            }
            Directory.Move(stageOk, ok);
            movedOk = true;
            Directory.Move(stageBad, bad);
            movedBad = true;
        }
        catch
        {
            // Roll back only while publishing copies. Once BOTH copies have
            // been committed, cleanup of the source must never erase them.
            if (Directory.Exists(stageOk)) Directory.Delete(stageOk, true);
            if (Directory.Exists(stageBad)) Directory.Delete(stageBad, true);
            if (movedOk && Directory.Exists(ok)) Directory.Delete(ok, true);
            if (movedBad && Directory.Exists(bad)) Directory.Delete(bad, true);
            throw;
        }

        try
        {
            Directory.Delete(source, recursive: true);
            return $"{ok} + {bad} (misto)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Both archived copies are already complete. Keep those results
            // even if the source cannot be deleted due to a Windows lock.
            return $"{ok} + {bad} (misto; pasta original parcialmente ou totalmente mantida, " +
                   "limpe a origem manualmente quando possível)";
        }
    }

    private static void CopyTree(string source, string destination)
    {
        if (IsLink(source))
            throw new IOException($"Junction/link not permitted: {source}");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if (IsLink(file))
                throw new IOException($"File link not permitted: {file}");
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
        }
        foreach (var child in Directory.EnumerateDirectories(source))
            CopyTree(child, Path.Combine(destination, Path.GetFileName(child)));
    }

    private string ProgressFile => Path.Combine(Root, ".fbx-vlote-progress.json");

    private void ReadProgress()
    {
        if (!File.Exists(ProgressFile))
            return;
        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(ProgressFile));
        if (data is null)
            return;
        foreach (var pair in data)
        {
            if (Enum.TryParse<BatchDecision>(pair.Value, true, out var value))
                _progress[pair.Key] = value;
        }
    }

    private void SaveProgress()
    {
        var state = _items.Where(x => x.Decision != BatchDecision.Pending &&
                                      File.Exists(x.FilePath))
            .ToDictionary(x => Path.GetRelativePath(Root, x.FilePath),
                x => x.Decision.ToString(), Compare);
        var pending = ProgressFile + ".part";
        File.WriteAllText(pending, JsonSerializer.Serialize(state,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(pending, ProgressFile, overwrite: true);
    }
}
