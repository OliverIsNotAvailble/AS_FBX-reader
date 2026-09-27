namespace AS.FBXReader.Models;

public sealed class ScenePart
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string NodeName { get; init; }
    public required int MeshIndex { get; init; }
    public int MaterialIndex { get; init; }
    public bool HasBones { get; init; }
    public bool Visible { get; set; } = true;

    public override string ToString() => Name;
}
