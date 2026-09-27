namespace AS.FBXReader.Export;

public enum ExportFormat
{
    Mp4,
    WebP,
    Mov
}

public sealed class ExportSettings
{
    public ExportFormat Format { get; set; } = ExportFormat.WebP;
    public int Width { get; set; } = 2048;
    public int Height { get; set; } = 2048;
    public int Fps { get; set; } = 30;
    public int Quality { get; set; } = 90;
    public bool TransparentBackground { get; set; } = true;
    public Color BackgroundColor { get; set; } = Color.Black;
    public bool ExportAllAnimations { get; set; }
    public string OutputPath { get; set; } = string.Empty;

    public string Extension => Format switch
    {
        ExportFormat.Mp4 => ".mp4",
        ExportFormat.WebP => ".webp",
        ExportFormat.Mov => ".mov",
        _ => ".mp4"
    };

    public bool SupportsTransparency
        => Format is ExportFormat.WebP or ExportFormat.Mov;
}
