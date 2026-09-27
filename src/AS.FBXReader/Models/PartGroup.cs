namespace AS.FBXReader.Models;

public sealed class PartGroup
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New group";

    public override string ToString() => Name;
}
