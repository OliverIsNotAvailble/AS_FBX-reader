namespace AS.FBXReader.Batch;

/// <summary>
/// Live2D-compatible session-only fixed output root. This computes paths
/// without creating anything: the exporter writes only after validation.
/// </summary>
public static class FixedOutputFolder
{
    public static string ModelFolderName(string fbxFilePath)
    {
        if (string.IsNullOrWhiteSpace(fbxFilePath) ||
            !Path.GetExtension(fbxFilePath).Equals(".fbx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("O modelo precisa ser um arquivo .fbx.", nameof(fbxFilePath));

        var stem = Path.GetFileNameWithoutExtension(fbxFilePath);
        foreach (var character in Path.GetInvalidFileNameChars())
            stem = stem.Replace(character, '_');

        stem = stem.Trim(' ', '.');
        if (string.IsNullOrEmpty(stem) || stem is "." or "..")
            throw new ArgumentException("O nome do FBX não pode formar uma pasta.", nameof(fbxFilePath));
        return stem;
    }

    public static string ForModel(string? fixedRoot, string fbxFilePath)
    {
        var value = (fixedRoot ?? "").Trim().Trim('"').Trim();
        if (value.Length == 0)
            throw new ArgumentException("Pasta de saída fixa não definida. Selecione uma pasta antes de exportar.");

        var folder = Path.GetFullPath(value);
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException(
                "A pasta de saída fixa não existe ou não é uma pasta:\r\n" + folder);

        return Path.Combine(folder, ModelFolderName(fbxFilePath));
    }

    /// <summary>
    /// Never overwrite a completed batch from another FBX with the same name.
    /// Each published directory is atomic, and collisions receive a suffix.
    /// </summary>
    public static string AvailableDestination(string desired)
    {
        var root = Path.GetDirectoryName(desired)
            ?? throw new ArgumentException("Destino sem pasta base.", nameof(desired));
        var name = Path.GetFileName(desired);
        var candidate = desired;
        for (var index = 1; File.Exists(candidate) || Directory.Exists(candidate); index++)
            candidate = Path.Combine(root, $"{name} ({index})");
        return candidate;
    }
}
