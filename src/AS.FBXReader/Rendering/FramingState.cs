namespace AS.FBXReader.Rendering;

// Fixed crop/reference used by both preview and final export.
// OffsetX/Y are normalized to the fitted half-width/half-height:
// +X moves the artwork right, +Y moves it up.
public sealed class FramingState
{
    public bool Enabled { get; set; }
    public float ReferenceMinX { get; set; } = -1f;
    public float ReferenceMaxX { get; set; } = 1f;
    public float ReferenceMinY { get; set; } = -1f;
    public float ReferenceMaxY { get; set; } = 1f;
    public float Zoom { get; set; } = 1f;
    public float OffsetX { get; set; }
    public float OffsetY { get; set; }

    public FramingState Clone()
        => new()
        {
            Enabled = Enabled,
            ReferenceMinX = ReferenceMinX,
            ReferenceMaxX = ReferenceMaxX,
            ReferenceMinY = ReferenceMinY,
            ReferenceMaxY = ReferenceMaxY,
            Zoom = Zoom,
            OffsetX = OffsetX,
            OffsetY = OffsetY
        };
}
