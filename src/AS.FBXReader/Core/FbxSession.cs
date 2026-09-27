using Assimp;
using Assimp.Configs;
using Assimp.Unmanaged;
using AS.FBXReader.Models;

namespace AS.FBXReader.Core;

public sealed class FbxSession : IDisposable
{
    private readonly AssimpContext _context = new();
    private readonly Dictionary<string, Node> _nodesByName = new(StringComparer.Ordinal);

    public string FilePath { get; private set; } = string.Empty;
    public Scene? Scene { get; private set; }
    public List<ScenePart> Parts { get; } = new();
    public List<AnimationTake> Animations { get; } = new();
    public IReadOnlyDictionary<string, Node> NodesByName => _nodesByName;

    public void Open(string path)
    {
        CloseScene();

        FilePath = Path.GetFullPath(path);
        EnsureAssimpLoadedFromAnsiSafePath();

        // Keep pivots evaluated where possible. The AS exporter already gives us
        // clean animation stacks and the viewer benefits from a simpler node tree.
        _context.SetConfig(new FBXPreservePivotsConfig(false));

        // AssimpNetter 6.0.5 uses ANSI LoadLibrary on Windows and Assimp's
        // file import path is not reliable with characters outside the active
        // Windows code page either. Stage only the FBX itself to an ASCII-only
        // path; textures are resolved later from the original FilePath by .NET.
        var stagedFbx = StageFbxForNativeImport(FilePath);
        try
        {
            Scene = _context.ImportFile(
                stagedFbx,
                PostProcessSteps.Triangulate |
                PostProcessSteps.SortByPrimitiveType |
                PostProcessSteps.ValidateDataStructure);
        }
        finally
        {
            try { File.Delete(stagedFbx); } catch { }
        }

        if (Scene is null || Scene.RootNode is null)
            throw new InvalidDataException("Assimp did not return a valid FBX scene.");

        IndexNodes(Scene.RootNode);
        IndexParts(Scene.RootNode);
        IndexAnimations();
    }

    public string ResolveTexturePath(Material material)
    {
        if (!material.GetMaterialTexture(TextureType.Diffuse, 0, out var slot))
            return string.Empty;

        var raw = slot.FilePath?.Replace('/', Path.DirectorySeparatorChar) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        if (Path.IsPathRooted(raw))
            return raw;

        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(FilePath)!, raw));
    }

    private static void EnsureAssimpLoadedFromAnsiSafePath()
    {
        if (AssimpLibrary.Instance.IsLibraryLoaded)
            return;

        var baseDir = AppContext.BaseDirectory;
        var sourceNativeDir = Path.Combine(baseDir, "runtimes", "win-x64", "native");
        var sourceAssimp = Path.Combine(sourceNativeDir, "assimp.dll");

        if (!File.Exists(sourceAssimp))
        {
            throw new FileNotFoundException(
                "assimp.dll was not found. Rebuild/download the latest AS_FBX-reader so the native library is copied to:\r\n" +
                sourceAssimp,
                sourceAssimp);
        }

        // AssimpNetter 6.0.5 calls kernel32!LoadLibraryA (ANSI), not LoadLibraryW.
        // A perfectly valid app path such as "...\\■clean pra trampo\\..." is
        // therefore mangled before Windows sees it and produces ERROR_MOD_NOT_FOUND.
        // Keep the native runtime in a deliberately ASCII-only, user-writable path.
        var publicDocs = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        var safeDir = Path.Combine(publicDocs, "AS_FBX-reader", "native-win-x64");
        Directory.CreateDirectory(safeDir);

        CopyNativeDlls(sourceNativeDir, safeDir);

        // Also copy app-local VC runtime DLLs when present. This covers machines
        // where the VC redistributable registration exists but one runtime DLL was
        // not published to System32.
        foreach (var file in Directory.EnumerateFiles(baseDir, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("VCRUNTIME", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("MSVCP", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("CONCRT", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(file, Path.Combine(safeDir, name), true);
            }
        }

        var safeAssimp = Path.Combine(safeDir, "assimp.dll");
        try
        {
            AssimpLibrary.Instance.LoadLibrary(safeAssimp);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Assimp native library could not be loaded even from the ASCII-safe runtime directory.\r\n\r\n" +
                $"Source app path: {baseDir}\r\n" +
                $"Safe runtime path: {safeAssimp}\r\n\r\n" +
                "If this still reports 0x8007007E, place the matching VC runtime DLLs " +
                "(MSVCP140.dll, VCRUNTIME140.dll, VCRUNTIME140_1.dll) next to AS_FBX-reader.exe and try again.",
                ex);
        }
    }

    private static void CopyNativeDlls(string sourceDir, string destinationDir)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*.dll", SearchOption.TopDirectoryOnly))
        {
            File.Copy(
                file,
                Path.Combine(destinationDir, Path.GetFileName(file)),
                overwrite: true);
        }
    }

    private static string StageFbxForNativeImport(string originalPath)
    {
        var publicDocs = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        var stageDir = Path.Combine(publicDocs, "AS_FBX-reader", "import-cache");
        Directory.CreateDirectory(stageDir);

        var staged = Path.Combine(stageDir, $"scene_{Guid.NewGuid():N}.fbx");
        File.Copy(originalPath, staged, overwrite: true);
        return staged;
    }

    private void IndexNodes(Node node)
    {
        if (!_nodesByName.ContainsKey(node.Name))
            _nodesByName.Add(node.Name, node);

        foreach (var child in node.Children)
            IndexNodes(child);
    }

    private void IndexParts(Node node)
    {
        if (Scene is null)
            return;

        foreach (var meshIndex in node.MeshIndices)
        {
            var mesh = Scene.Meshes[meshIndex];
            var name = string.IsNullOrWhiteSpace(mesh.Name) ? node.Name : mesh.Name;
            var id = $"mesh:{meshIndex}:{node.Name}";

            Parts.Add(new ScenePart
            {
                Id = id,
                Name = CleanAssetStudioName(name),
                NodeName = node.Name,
                MeshIndex = meshIndex,
                MaterialIndex = mesh.MaterialIndex,
                HasBones = mesh.HasBones,
                Visible = true
            });
        }

        foreach (var child in node.Children)
            IndexParts(child);
    }

    private void IndexAnimations()
    {
        if (Scene is null)
            return;

        for (var i = 0; i < Scene.AnimationCount; i++)
        {
            var animation = Scene.Animations[i];
            var tps = animation.TicksPerSecond > 0.000001
                ? animation.TicksPerSecond
                : 25.0;

            Animations.Add(new AnimationTake
            {
                Index = i,
                Name = CleanAnimationName(animation.Name, i),
                DurationSeconds = animation.DurationInTicks / tps,
                TicksPerSecond = tps
            });
        }
    }

    private static string CleanAnimationName(string? value, int index)
    {
        if (string.IsNullOrWhiteSpace(value))
            return $"Animation {index + 1}";

        return value
            .Replace("AnimStack", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string CleanAssetStudioName(string value)
    {
        var name = value;
        if (name.EndsWith("Geometry", StringComparison.Ordinal))
            name = name[..^"Geometry".Length];
        if (name.StartsWith("__spriteskin_", StringComparison.Ordinal) ||
            name.StartsWith("__sprite_", StringComparison.Ordinal))
        {
            var pieces = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length >= 4)
                name = string.Join('_', pieces.Skip(3));
        }
        return name;
    }

    private void CloseScene()
    {
        Scene = null;
        FilePath = string.Empty;
        Parts.Clear();
        Animations.Clear();
        _nodesByName.Clear();
    }

    public void Dispose()
    {
        CloseScene();
        _context.Dispose();
    }
}
