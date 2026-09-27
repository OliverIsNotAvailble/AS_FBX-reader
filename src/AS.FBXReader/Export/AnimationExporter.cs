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
        ExportSettings settings,
        CancellationToken cancellationToken = default)
    {
        var input = Path.Combine(framesFolder, "frame_%06d.png");
        var args = BuildFfmpegArguments(input, outputPath, settings);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException(
                "Could not start ffmpeg. Make sure ffmpeg is in PATH.");

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stderr = await stderrTask;
        _ = await stdoutTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg failed ({process.ExitCode}):\r\n{stderr}");
        }
    }

    private static string BuildFfmpegArguments(
        string inputPattern,
        string outputPath,
        ExportSettings settings)
    {
        var fps = Math.Clamp(settings.Fps, 1, 240);
        var quality = Math.Clamp(settings.Quality, 1, 100);

        return settings.Format switch
        {
            ExportFormat.WebP => BuildWebP(
                inputPattern,
                outputPath,
                fps,
                quality),

            ExportFormat.Mov => BuildMov(
                inputPattern,
                outputPath,
                fps,
                settings.TransparentBackground),

            _ => BuildMp4(
                inputPattern,
                outputPath,
                fps,
                quality)
        };
    }

    private static string BuildMp4(
        string input,
        string output,
        int fps,
        int quality)
    {
        // Quality 1..100 -> CRF 35..15.
        var crf = Math.Clamp(
            (int)Math.Round(35 - quality * 0.20),
            15,
            35);

        return
            $"-y -framerate {fps} -i \"{input}\" " +
            $"-c:v libx264 -preset medium -crf {crf} -pix_fmt yuv420p " +
            $"-movflags +faststart \"{output}\"";
    }

    private static string BuildWebP(
        string input,
        string output,
        int fps,
        int quality)
        =>
            $"-y -framerate {fps} -i \"{input}\" " +
            $"-c:v libwebp_anim -q:v {quality} -loop 0 -vsync 0 " +
            $"\"{output}\"";

    private static string BuildMov(
        string input,
        string output,
        int fps,
        bool transparent)
    {
        if (transparent)
        {
            // ProRes 4444 keeps alpha and is widely editable.
            return
                $"-y -framerate {fps} -i \"{input}\" " +
                $"-c:v prores_ks -profile:v 4 -pix_fmt yuva444p10le " +
                $"\"{output}\"";
        }

        return
            $"-y -framerate {fps} -i \"{input}\" " +
            $"-c:v prores_ks -profile:v 3 -pix_fmt yuv422p10le " +
            $"\"{output}\"";
    }
}
