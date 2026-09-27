using AS.FBXReader.Core;
using AS.FBXReader.Rendering;

namespace AS.FBXReader.Export;

public sealed class AnimationExporter
{
    public async Task ExportPngSequenceAsync(
        ViewerControl viewer,
        AnimationPlayer player,
        string outputFolder,
        int width,
        int height,
        int fps,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputFolder);
        var duration = Math.Max(0.001, player.DurationSeconds);
        var frames = Math.Max(1, (int)Math.Ceiling(duration * fps));
        var wasPlaying = player.Playing;
        player.Playing = false;

        try
        {
            for (var frame = 0; frame < frames; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                player.SetTime(frame / (double)fps);
                using var bitmap = viewer.CaptureFrame(width, height);
                bitmap.Save(
                    Path.Combine(outputFolder, $"frame_{frame:000000}.png"),
                    System.Drawing.Imaging.ImageFormat.Png);
                progress?.Report((frame + 1) * 100 / frames);
                await Task.Yield();
            }
        }
        finally
        {
            player.Playing = wasPlaying;
        }
    }

    public async Task EncodeWithFfmpegAsync(
        string framesFolder,
        string outputPath,
        int fps,
        CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(outputPath).ToLowerInvariant();
        var args = extension switch
        {
            ".webp" => $"-y -framerate {fps} -i \"{Path.Combine(framesFolder, "frame_%06d.png")}\" -loop 0 -c:v libwebp_anim -q:v 90 \"{outputPath}\"",
            _ => $"-y -framerate {fps} -i \"{Path.Combine(framesFolder, "frame_%06d.png")}\" -c:v libx264 -pix_fmt yuv420p \"{outputPath}\""
        };

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start ffmpeg. Make sure ffmpeg is in PATH.");

        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException($"ffmpeg failed ({process.ExitCode}):\r\n{stderr}");
        }
    }
}
