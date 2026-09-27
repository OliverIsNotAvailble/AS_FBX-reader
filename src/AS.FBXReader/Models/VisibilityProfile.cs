using System.Text.Json;
using System.Text.Json.Serialization;

namespace AS.FBXReader.Models;

public sealed class VisibilityProfile
{
    public string SourceFbx { get; set; } = string.Empty;
    public string? Animation { get; set; }
    public List<string> HiddenPartIds { get; set; } = new();

    // Front -> back. Kept for backwards compatibility with 0.1.4/0.1.5 profiles.
    public List<string> PartOrderIds { get; set; } = new();

    public Dictionary<string, string> PartAliases { get; set; } = new();

    // V0.1.6+: folders/subfolders + exact visual ordering.
    public List<OrganizationNodeProfile> Organization { get; set; } = new();

    public static VisibilityProfile Load(string path)
        => JsonSerializer.Deserialize<VisibilityProfile>(
               File.ReadAllText(path),
               JsonOptions())
           ?? new VisibilityProfile();

    public void Save(string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions()));

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed class OrganizationNodeProfile
{
    // "part" or "group"
    public string Type { get; set; } = "part";

    // ScenePart.Id for parts; stable generated id for groups.
    public string Id { get; set; } = string.Empty;

    // Only groups need a stored display name. Part names/aliases live elsewhere.
    public string? Name { get; set; }

    public List<OrganizationNodeProfile> Children { get; set; } = new();
}
