using System.Text.Json;
using System.Text.Json.Serialization;

namespace AS.FBXReader.Models;

public sealed class VisibilityProfile
{
    public string SourceFbx { get; set; } = string.Empty;
    public string? Animation { get; set; }
    public List<string> HiddenPartIds { get; set; } = new();

    // Front -> back, matching the UI list (top -> bottom).
    public List<string> PartOrderIds { get; set; } = new();

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
