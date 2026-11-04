using System.Globalization;
using System.Text;
using AS.FBXReader.Models;

namespace AS.FBXReader.Core;

/// <summary>
/// Non-mutating model diagnostics. Temporarily samples every take using the
/// same player/renderer visibility logic, and restores the user's playback
/// state even when an exception occurs. No FBX or textures are rewritten.
/// </summary>
public static class FbxVisibilityLog
{
    private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    public static string Create(FbxSession session, AnimationPlayer player)
    {
        if (session.Scene is null)
            throw new InvalidOperationException("Abra um FBX antes de gerar o LOG.");

        var sb = new StringBuilder(256_000);
        var previousIndex = player.AnimationIndex;
        var previousTime = player.TimeSeconds;
        var previousPlaying = player.Playing;
        var previousLoop = player.Loop;

        try
        {
            player.Playing = false;
            player.Loop = false;
            var scene = session.Scene;

            sb.AppendLine("AS_FBX-reader / VLote VISIBILITY DIAGNOSTIC");
            sb.AppendLine("==========================================");
            sb.AppendLine("Generated UTC: " + DateTime.UtcNow.ToString("O", CI));
            sb.AppendLine("Reader version: " +
                (typeof(FbxVisibilityLog).Assembly.GetName().Version?.ToString() ?? "unknown"));
            sb.AppendLine("FBX file: " + Path.GetFileName(session.FilePath));
            sb.AppendLine("FBX size: " + new FileInfo(session.FilePath).Length.ToString(CI) + " bytes");
            sb.AppendLine("Importer: " + typeof(Assimp.AssimpContext).Assembly.GetName().FullName);
            sb.AppendLine("Assimp scene: meshes=" + scene.MeshCount +
                ", materials=" + scene.MaterialCount +
                ", animation takes=" + scene.AnimationCount);
            sb.AppendLine("Part entries (including user copies): " + session.Parts.Count);
            sb.AppendLine("Original FBX models parsed: " + session.AuthoredModelCount +
                ", default-hidden: " + session.AuthoredHiddenNodes);
            sb.AppendLine("Raw FBX animation stacks: " + session.RawScaleAnimationCount +
                ", original Lcl Scaling tracks: " + session.RawScaleTrackCount);
            sb.AppendLine("Duplicate model names rewritten during import: " +
                session.DuplicateModelNamesFixed);
            sb.AppendLine("Restored collapsed mesh transforms: " +
                player.RestoredCollapsedMeshes);
            sb.AppendLine("Restored collapsed parent transforms: " +
                player.RestoredCollapsedAncestors);
            sb.AppendLine("Recovered invalid skin offsets: " +
                player.RecoveredBoneOffsets);
            sb.AppendLine();
            sb.AppendLine("RULES: source=real animation visibility; user=part toggle;");
            sb.AppendLine("FORCED ignores source switches; final=what renderer draws.");
            sb.AppendLine("Raw FBX curves are preferred when present; otherwise the");
            sb.AppendLine("reader falls back to static authored state / Assimp.");
            sb.AppendLine("This report does not prove that mesh coordinates, UVs");
            sb.AppendLine("or skin deformation are visually correct.");
            sb.AppendLine();

            sb.AppendLine("=== ALL IMPORTED PARTS / GEOMETRY / TEXTURES ===");
            foreach (var part in session.Parts)
            {
                sb.Append("PART: ").Append(part.Name)
                    .Append(" | id=").Append(part.Id)
                    .Append(" | node=").Append(part.NodeName)
                    .Append(" | meshIndex=").Append(part.MeshIndex)
                    .Append(" | USER_VISIBLE=").Append(part.Visible)
                    .Append(" | FORCED=").Append(part.ForceVisible)
                    .Append(" | timedKeys=").Append(part.VisibilityKeys.Count)
                    .AppendLine();

                if (part.MeshIndex < 0 || part.MeshIndex >= scene.MeshCount)
                {
                    sb.AppendLine("    ERROR: mesh index missing from Assimp scene");
                    continue;
                }

                var mesh = scene.Meshes[part.MeshIndex];
                sb.Append("    assimpName=").Append(mesh.Name)
                    .Append(" vertices=").Append(mesh.VertexCount)
                    .Append(" faces=").Append(mesh.FaceCount)
                    .Append(" bones=").Append(mesh.BoneCount)
                    .Append(" UV0=").Append(mesh.HasTextureCoords(0))
                    .Append(" material=").Append(mesh.MaterialIndex)
                    .AppendLine();

                if (!session.NodesByName.ContainsKey(part.NodeName))
                    sb.AppendLine("    ERROR: MISSING_ASSIMP_NODE");
                if (mesh.MaterialIndex >= 0 &&
                    mesh.MaterialIndex < scene.MaterialCount)
                {
                    var texture = string.IsNullOrWhiteSpace(part.TextureOverridePath)
                        ? session.ResolveTexturePath(scene.Materials[mesh.MaterialIndex])
                        : part.TextureOverridePath;
                    sb.Append("    texture=")
                        .Append(Path.GetFileName(texture ?? ""))
                        .Append(" FOUND=").Append(!string.IsNullOrWhiteSpace(texture)
                            && File.Exists(texture)).AppendLine();
                }
                if (mesh.BoneCount > 0)
                {
                    var invalid = mesh.Bones.Count(b =>
                        !IsFinite(b.OffsetMatrix));
                    sb.Append("    invalid bone offsets=").Append(invalid)
                        .AppendLine();
                }
            }

            sb.AppendLine();
            sb.AppendLine("=== SNAPSHOT OF USER-SELECTED ANIMATION AND TIME ===");
            player.SelectAnimation(previousIndex);
            player.SetTime(previousTime);
            WriteFrame(sb, session, player,
                "CURRENT SELECTION", details: true);

            sb.AppendLine();
            sb.AppendLine("=== REST POSE (NO SELECTED ANIMATION) ===");
            player.SelectAnimation(-1);
            WriteFrame(sb, session, player, "REST", details: true);

            sb.AppendLine();
            sb.AppendLine("=== ALL ANIMATION TAKES ===");
            foreach (var take in session.Animations)
            {
                player.SelectAnimation(take.Index);
                var duration = Math.Max(0, take.DurationSeconds);
                sb.AppendLine();
                sb.AppendLine("------------------------------------------");
                sb.AppendLine("TAKE " + take.Index + ": " + take.Name +
                    " | duration=" + Num(duration) + " seconds");
                sb.AppendLine("------------------------------------------");
                // One original-curve record for every part, including those
                // with NO source scale curve (critical for this FBX family).
                foreach (var part in session.Parts)
                {
                    sb.Append("RAW: ").Append(part.NodeName)
                        .Append(" | ").Append(
                            session.DescribeRawScaleTrack(take.Index,
                                part.NodeName, take.Name))
                        .AppendLine();
                }

                var stamps = duration <= 0
                    ? new[] { 0d }
                    : new[] { 0d, duration * .25d, duration * .5d, duration * .75d };
                foreach (var stamp in stamps)
                {
                    player.SetTime(stamp);
                    // Full ancestor chain on the first frame of every take,
                    // concise state on subsequent frames to limit log size.
                    WriteFrame(sb, session, player,
                        "t=" + Num(stamp) + " s", details: stamp == 0);
                }
            }
        }
        finally
        {
            // No persistent changes to scene, user part toggles or playback.
            player.Loop = previousLoop;
            player.SelectAnimation(previousIndex);
            player.SetTime(previousTime);
            player.Playing = previousPlaying;
        }

        return sb.ToString();
    }

    private static void WriteFrame(StringBuilder sb, FbxSession session,
        AnimationPlayer player, string label, bool details)
    {
        var parts = session.Parts;
        var sourceAllowed = 0;
        var drawn = 0;
        var forced = 0;
        var userHidden = 0;
        foreach (var part in parts)
        {
            var sourceVisible = player.IsSourceVisible(part.NodeName);
            if (sourceVisible) sourceAllowed++;
            if (part.Visible && part.ForceVisible) forced++;
            if (!part.Visible) userHidden++;
            if (part.ShouldRender(player.CurrentAnimationName,
                    player.TimeSeconds, sourceVisible))
                drawn++;
        }

        sb.AppendLine();
        sb.AppendLine("FRAME " + label +
            " | take=" + (player.CurrentAnimationName ?? "REST") +
            " | seconds=" + Num(player.TimeSeconds) +
            " | sourceAllowed=" + sourceAllowed +
            " | finalDrawn=" + drawn +
            " | userHidden=" + userHidden +
            " | forced=" + forced +
            " | allParts=" + parts.Count);

        foreach (var part in parts)
        {
            var source = player.IsSourceVisible(part.NodeName);
            var final = part.ShouldRender(player.CurrentAnimationName,
                player.TimeSeconds, source);

            var reason = !part.Visible ? "USER_HIDDEN" :
                part.ForceVisible ? "FORCED" :
                !source ? "SOURCE_HIDDEN" :
                final ? "AUTO_VISIBLE" : "TIMED_OVERRIDE";
            sb.Append("    ").Append(final ? "DRAW " : "HIDE ")
                .Append(" | ").Append(part.Name)
                .Append(" | node=").Append(part.NodeName)
                .Append(" | source=").Append(source ? "ON" : "OFF")
                .Append(" | user=").Append(part.Visible ? "ON" : "OFF")
                .Append(" | forced=").Append(part.ForceVisible)
                .Append(" | reason=").Append(reason).AppendLine();

            if (details)
                sb.AppendLine("        " +
                    player.ExplainVisibility(part.NodeName)
                        .Replace("\n", "\n        "));
        }
    }

    private static bool IsFinite(System.Numerics.Matrix4x4 m)
        => float.IsFinite(m.M11) && float.IsFinite(m.M12) &&
           float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
           float.IsFinite(m.M21) && float.IsFinite(m.M22) &&
           float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
           float.IsFinite(m.M31) && float.IsFinite(m.M32) &&
           float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
           float.IsFinite(m.M41) && float.IsFinite(m.M42) &&
           float.IsFinite(m.M43) && float.IsFinite(m.M44);

    private static string Num(double value)
        => value.ToString("0.######", CI);
}
