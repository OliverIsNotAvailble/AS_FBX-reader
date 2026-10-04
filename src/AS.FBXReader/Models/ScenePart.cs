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
    public bool ForceVisible { get; set; }
    // World-space correction after skinning; applies to preview and export.
    public float OffsetX { get; set; }
    public float OffsetY { get; set; }
    public bool RebuildBindPose { get; set; }
    public bool PreserveShape { get; set; }
    // Per-vertex edits in the mesh's local coordinates, before skinning.
    public Dictionary<int, VertexOffset> VertexOffsets { get; } = new();
    public List<VisibilityKeyframe> VisibilityKeys { get; } = new();

    public string DisplayName
        => (string.IsNullOrWhiteSpace(Alias)
            ? Name
            : $"{Alias}  [{Name}]") +
           (ForceVisible ? "  [FORCED]" : string.Empty) +
           (VisibilityKeys.Count > 0 ? "  [TIMED]" : string.Empty);

    public bool ShouldRender(string? animation, double seconds, bool sourceVisible)
    {
        if (!Visible)
            return false;

        if (ForceVisible)
            return true;

        if (animation is not null)
        {
            var key = VisibilityKeys
                .Where(x => x.Animation == animation && x.Seconds <= seconds + 0.000001)
                .OrderByDescending(x => x.Seconds)
                .FirstOrDefault();
            if (key is not null)
                return key.Visible;
        }

        return sourceVisible;
    }

    public override string ToString() => DisplayName;
}

public sealed class VertexOffset
{
    public int VertexIndex { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
}

public sealed class VisibilityKeyframe
{
    public string PartId { get; set; } = string.Empty;
    public string Animation { get; set; } = string.Empty;
    public double Seconds { get; set; }
    public bool Visible { get; set; }
}
