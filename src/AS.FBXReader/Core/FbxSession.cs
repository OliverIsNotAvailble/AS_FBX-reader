using Assimp;
using Assimp.Configs;
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
        ValidateNativeAssimpDependencies();

        // Keep pivots evaluated where possible. The AS exporter already gives us
        // clean animation stacks and the viewer benefits from a simpler node tree.
        _context.SetConfig(new FBXPreservePivotsConfig(false));

        Scene = _context.ImportFile(
            FilePath,
            PostProcessSteps.Triangulate |
            PostProcessSteps.SortByPrimitiveType |
            PostProcessSteps.ValidateDataStructure);

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

    private static void ValidateNativeAssimpDependencies()
    {
        var baseDir = AppContext.BaseDirectory;
        var nativeDll = Path.Combine(baseDir, "runtimes", "win-x64", "native", "assimp.dll");

        if (!File.Exists(nativeDll))
        {
            throw new FileNotFoundException(
                "assimp.dll was not found. Rebuild/download the latest AS_FBX-reader so the native library is copied to:\r\n" +
                nativeDll,
                nativeDll);
        }

        var systemDir = Environment.SystemDirectory;
        var vcRuntimeFiles = new[]
        {
            "MSVCP140.dll",
            "VCRUNTIME140.dll",
            "VCRUNTIME140_1.dll"
        };

        var missing = vcRuntimeFiles
            .Where(file => !File.Exists(Path.Combine(systemDir, file)))
            .ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "assimp.dll exists, but required Microsoft Visual C++ runtime DLLs are missing:\r\n\r\n" +
                string.Join("\r\n", missing.Select(x => "  - " + x)) +
                "\r\n\r\nInstall the latest Microsoft Visual C++ v14 Redistributable (x64), then restart AS_FBX-reader.");
        }
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
