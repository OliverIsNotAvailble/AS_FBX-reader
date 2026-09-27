namespace AS.FBXReader.Models;

public sealed class ScenePart
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Alias { get; set; }
    public required string NodeName { get; init; }
    public required int MeshIndex { get; init; }
    public int MaterialIndex { get; init; }
    public bool HasBones { get; init; }
    public bool Visible { get; set; } = true;

    public string DisplayName
        => string.IsNullOrWhiteSpace(Alias)
            ? Name
            : $"{Alias}  [{Name}]";

    public override string ToString() => DisplayName;
}
