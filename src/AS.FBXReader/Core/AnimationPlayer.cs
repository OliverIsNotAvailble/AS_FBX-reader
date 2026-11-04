using Assimp;
using OpenTK.Mathematics;

namespace AS.FBXReader.Core;

public sealed class AnimationPlayer
{
    private sealed class NodeState
    {
        public required Node Node { get; init; }
        public NodeState? Parent { get; init; }
        public List<NodeState> Children { get; } = new();
        public Matrix4 Local { get; set; }
        public Matrix4 BaseLocal { get; init; }
        public Matrix4 Global { get; set; }
        public Matrix4 BindGlobal { get; set; }
        public bool CollapsedMesh { get; init; }
        public bool CollapsedAncestor { get; init; }
        public bool SourceVisible { get; set; } = true;
        public Vector3 BaseScale { get; init; } = Vector3.One;
        public Quaternion BaseRotation { get; init; } = Quaternion.Identity;
        public Vector3 BaseTranslation { get; init; } = Vector3.Zero;
    }

    private readonly FbxSession _session;
    private readonly Dictionary<string, NodeState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NodeAnimationChannel> _channels = new(StringComparer.Ordinal);
    private NodeState? _root;

    public int AnimationIndex { get; private set; } = -1;
    public double TimeSeconds { get; private set; }
    public bool Loop { get; set; } = true;
    public bool Playing { get; set; }
    public int RecoveredBoneOffsets { get; private set; }
    public int RestoredCollapsedMeshes { get; private set; }
    public int RestoredCollapsedAncestors { get; private set; }
    public int RecoveredStaticSpriteCount
        => _states.Values.Count(state =>
            _session.IsUnanimatedStaticSprite(state.Node.Name, state.CollapsedMesh));

    public bool IsRecoveredStaticSprite(string nodeName)
        => _states.TryGetValue(nodeName, out var state) &&
           _session.IsUnanimatedStaticSprite(state.Node.Name, state.CollapsedMesh);

    public string? CurrentAnimationName
        => AnimationIndex >= 0 && AnimationIndex < _session.Animations.Count
            ? _session.Animations[AnimationIndex].Name
            : null;

    public double DurationSeconds
        => AnimationIndex >= 0 && AnimationIndex < _session.Animations.Count
            ? _session.Animations[AnimationIndex].DurationSeconds
            : 0.0;

    public AnimationPlayer(FbxSession session)
    {
        _session = session;
    }

    public void Rebuild()
    {
        // A newly loaded FBX has a completely different animation catalog.
        // Never reuse the previous model's selected index: it may point past
        // the new model's takes. MainForm selects the first take after load.
        AnimationIndex = -1;
        Playing = false;
        _root = null;
        _states.Clear();
        _channels.Clear();
        TimeSeconds = 0;
        RecoveredBoneOffsets = 0;
        RestoredCollapsedMeshes = 0;
        RestoredCollapsedAncestors = 0;

        if (_session.Scene?.RootNode is null)
            return;

        _root = BuildTree(_session.Scene.RootNode, null);
        // Some AssetStudio SpriteSkin FBXs contain NaNs in the skin clusters'
        // Transform/TransformLink matrices. Assimp passes those through to the
        // bone offsets; count them here so the user can see the recovery.
        RecoveredBoneOffsets = _session.Scene.Meshes
            .Sum(mesh => mesh.Bones.Count(bone => !IsFinite(MatrixUtil.ToOpenTk(bone.OffsetMatrix))));
        SelectAnimation(-1);
    }

    /// <summary>
    /// A user-selected take may become invalid after switching FBX files.
    /// -1 is the rest pose, not a valid indexed take. Check BOTH the imported
    /// Assimp animation count and the session's indexed-take catalog.
    /// </summary>
    public static int NormalizeAnimationIndex(
        int requested, int importedCount, int indexedTakeCount)
        => requested >= 0 && requested < importedCount &&
           requested < indexedTakeCount ? requested : -1;

    public void SelectAnimation(int index)
    {
        var scene = _session.Scene;
        index = NormalizeAnimationIndex(index,
            scene?.AnimationCount ?? 0, _session.Animations.Count);

        AnimationIndex = index;
        TimeSeconds = 0;
        _channels.Clear();

        if (index < 0 || scene is null)
        {
            Evaluate();
            return;
        }

        foreach (var channel in scene.Animations[index].NodeAnimationChannels)
            _channels[channel.NodeName] = channel;

        Evaluate();
    }

    public void SetTime(double seconds)
    {
        TimeSeconds = Math.Max(0, seconds);
        NormalizeTime();
        Evaluate();
    }

    public void Update(double deltaSeconds)
    {
        if (!Playing || AnimationIndex < 0)
            return;

        TimeSeconds += deltaSeconds;
        NormalizeTime();
        Evaluate();
    }

    public Matrix4 GetGlobalTransform(string nodeName)
        => _states.TryGetValue(nodeName, out var state)
            ? state.Global
            : Matrix4.Identity;

    // Keeps the mesh in its original rest position while making it follow a
    // different rig node. Row-vector order: mesh bind -> target bind inverse
    // -> animated target -> animated mesh inverse. The renderer applies the
    // mesh global transform afterwards, canceling that final inverse.
    public Matrix4? GetFollowBoneMatrix(Node meshNode, string boneName)
    {
        if (!_states.TryGetValue(meshNode.Name, out var meshState) ||
            !_states.TryGetValue(boneName, out var boneState))
            return null;

        var follow = meshState.BindGlobal * boneState.BindGlobal.Inverted() *
            boneState.Global * meshState.Global.Inverted();
        return IsFinite(follow) ? follow : null;
    }

    // A SpriteRenderer lives on the mesh, but a FBX Null/Canvas parent can
    // also animate visibility using (0,0,0) scale. Respect the complete chain.
    // ForceVisible still overrides this at the ScenePart level for debugging.
    public bool IsSourceVisible(string nodeName)
    {
        if (!_states.TryGetValue(nodeName, out var state))
            return true;

        for (NodeState? current = state; current is not null; current = current.Parent)
        {
            if (!current.SourceVisible)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Human-readable explanation of the actual runtime visibility decision.
    /// Reports every ancestor, including which one blocks a sprite. This uses
    /// the exact evaluated states used by the renderer, not a guessed rule.
    /// </summary>
    public string ExplainVisibility(string nodeName)
    {
        if (!_states.TryGetValue(nodeName, out var state))
            return "MISSING_NODE: Assimp did not provide an animation state for " + nodeName;

        var sb = new System.Text.StringBuilder();
        sb.Append("effective=").Append(IsSourceVisible(nodeName) ? "ON" : "OFF");
        var current = state;
        while (current is not null)
        {
            var hasAuthored = _session.TryGetAuthoredVisibility(
                current.Node.Name, out var authored);
            var raw = current.BaseScale;
            var hasRaw = AnimationIndex >= 0 &&
                _session.TrySampleRawScale(AnimationIndex, current.Node.Name,
                    TimeSeconds, current.BaseScale, out raw,
                    CurrentAnimationName);
            var hasAssimp = _channels.TryGetValue(current.Node.Name, out var channel);
            sb.AppendLine();
            sb.Append("    NODE=").Append(current.Node.Name);
            sb.Append(" evaluated=").Append(current.SourceVisible ? "ON" : "OFF");
            sb.Append(" authored=").Append(hasAuthored ?
                (authored ? "ON" : "OFF") : "NOT_FOUND");
            sb.Append(" rawScale=").Append(hasRaw
                ? FormatVector(raw) : "NO_CURVE");
            sb.Append(" assimpScaleKeys=").Append(hasAssimp ?
                channel!.ScalingKeys.Count.ToString() : "NO_CHANNEL");
            sb.Append(" importedBindScale=").Append(FormatVector(current.BaseScale));
            sb.Append(" repairedMesh=").Append(current.CollapsedMesh);
            sb.Append(" staticSpriteRecovery=").Append(
                _session.IsUnanimatedStaticSprite(current.Node.Name, current.CollapsedMesh));
            sb.Append(" repairedParent=").Append(current.CollapsedAncestor);
            if (!current.SourceVisible)
                sb.Append("  <<< BLOCKS VISIBILITY");
            current = current.Parent;
        }
        return sb.ToString();
    }

    private static string FormatVector(Vector3 value)
        => string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "({0:0.####},{1:0.####},{2:0.####})", value.X, value.Y, value.Z);

    public Matrix4[] GetBoneMatrices(Node meshNode, Mesh mesh, bool rebuildBindPose = false)
    {
        var result = new Matrix4[mesh.BoneCount];
        if (mesh.BoneCount == 0)
            return result;

        var inverseMesh = GetGlobalTransform(meshNode.Name).Inverted();
        for (var i = 0; i < mesh.BoneCount; i++)
        {
            var bone = mesh.Bones[i];
            var boneGlobal = GetGlobalTransform(bone.Name);
            var offset = MatrixUtil.ToOpenTk(bone.OffsetMatrix);

            if (_states.TryGetValue(meshNode.Name, out var meshState) &&
                _states.TryGetValue(bone.Name, out var boneState))
            {
                if (rebuildBindPose || meshState.CollapsedMesh || !IsFinite(offset))
                {
                    // Row-vector bind pose: meshBind * inverse(boneBind).
                    // Assimp can normalize a broken FBX bind into a finite but
                    // unusable matrix. A collapsed mesh always needs recovery;
                    // healthy meshes keep their original offsets.
                    var inverseBoneBind = boneState.BindGlobal.Inverted();
                    var recovered = meshState.BindGlobal * inverseBoneBind;
                    if (IsFinite(recovered))
                        offset = recovered;
                }
            }

            // OpenTK TransformPosition uses row-vector semantics:
            // p' = p * matrix. Therefore the standard Assimp skinning chain is
            // offset * boneGlobal * inverseMeshGlobal.
            result[i] = offset * boneGlobal * inverseMesh;
        }
        return result;
    }

    private static bool IsFinite(Matrix4 m)
        => float.IsFinite(m.M11) && float.IsFinite(m.M12) &&
           float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
           float.IsFinite(m.M21) && float.IsFinite(m.M22) &&
           float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
           float.IsFinite(m.M31) && float.IsFinite(m.M32) &&
           float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
           float.IsFinite(m.M41) && float.IsFinite(m.M42) &&
           float.IsFinite(m.M43) && float.IsFinite(m.M44);

    private void NormalizeTime()
    {
        var duration = DurationSeconds;
        if (duration <= 0)
        {
            TimeSeconds = 0;
            return;
        }

        if (Loop)
            TimeSeconds %= duration;
        else if (TimeSeconds > duration)
        {
            TimeSeconds = duration;
            Playing = false;
        }
    }

    private NodeState BuildTree(Node node, NodeState? parent)
    {
        var local = MatrixUtil.ToOpenTk(node.Transform);

        // AssetStudio can write (0,0,0) scale not only on SpriteSkin meshes,
        // but also on an ancestor such as "TopmostCanvas". Even after fixing
        // every mesh, that ancestor would flatten the entire character.
        // Restore only zero-scale nodes containing renderable descendants;
        // leave unrelated scene transforms and bone hierarchies untouched.
        var zeroScale = IsCollapsedMesh(local);
        var collapsedMesh = node.MeshIndices.Count > 0 && zeroScale;
        var collapsedAncestor = node.MeshIndices.Count == 0 &&
            zeroScale && HasMeshDescendant(node);
        if (collapsedMesh || collapsedAncestor)
        {
            local = Matrix4.CreateTranslation(local.M41, local.M42, local.M43);
            if (collapsedMesh)
                RestoredCollapsedMeshes++;
            else
                RestoredCollapsedAncestors++;
        }

        var baseScale = Vector3.One;
        var baseRotation = Quaternion.Identity;
        var baseTranslation = Vector3.Zero;
        var decomposable = new System.Numerics.Matrix4x4(
            local.M11, local.M12, local.M13, local.M14,
            local.M21, local.M22, local.M23, local.M24,
            local.M31, local.M32, local.M33, local.M34,
            local.M41, local.M42, local.M43, local.M44);
        if (System.Numerics.Matrix4x4.Decompose(
                decomposable,
                out var scaleN,
                out var rotationN,
                out var translationN))
        {
            baseScale = MatrixUtil.ToOpenTk(scaleN);
            baseRotation = MatrixUtil.ToOpenTk(rotationN);
            baseTranslation = MatrixUtil.ToOpenTk(translationN);
        }

        var state = new NodeState
        {
            Node = node,
            Parent = parent,
            Local = local,
            BaseLocal = local,
            CollapsedMesh = collapsedMesh,
            CollapsedAncestor = collapsedAncestor,
            BaseScale = baseScale,
            BaseRotation = baseRotation,
            BaseTranslation = baseTranslation
        };

        // Row-vector convention: child local transform is applied first,
        // then its parent's global transform.
        state.Global = parent is null ? state.Local : state.Local * parent.Global;
        state.BindGlobal = state.Global;
        _states[node.Name] = state;

        foreach (var child in node.Children)
            state.Children.Add(BuildTree(child, state));

        return state;
    }

    private void Evaluate()
    {
        if (_root is null)
            return;

        EvaluateNode(_root);
    }

    private void EvaluateNode(NodeState state)
    {
        var local = state.BaseLocal;
        // Only the FBX-authored default determines initial visibility.
        // A collapsed Assimp bind pose must not hide a mesh in every take.
        // A collapsed SpriteSkin with NO scale keys in ANY take is not an
        // animated alternative. It is a static body/head/hair/limb component
        // whose exporter wrote an invalid zero-scale rest pose. Recover it
        // without forcing animated eyes/mouths or mutually exclusive layers.
        var recoveredStaticSprite =
            _session.IsUnanimatedStaticSprite(state.Node.Name, state.CollapsedMesh);
        state.SourceVisible = recoveredStaticSprite || state.CollapsedAncestor || (
            _session.TryGetAuthoredVisibility(state.Node.Name, out var authored)
                ? authored
                : !state.CollapsedMesh);

        // The index is normalized on every selection/rebuild. Recheck the
        // catalog here too so future callers cannot index stale take data.
        var selectedTake = CurrentAnimationName;
        var rawAvailable = selectedTake is not null && _session.HasRawScaleAnimations;
        var authoredScale = state.BaseScale;
        var hasAuthoredKeys = rawAvailable &&
            _session.TrySampleRawScale(AnimationIndex, state.Node.Name,
                TimeSeconds, state.BaseScale, out authoredScale,
                selectedTake);

        if (hasAuthoredKeys && !state.CollapsedAncestor)
        {
            // The original FBX's per-take 0/1 sprite switches always win
            // over Assimp's animation channel, which may contain synthetic
            // zero keys for sprites that never had any scale curve at all.
            state.SourceVisible = IsVisibleScale(authoredScale);
        }

        if (selectedTake is not null &&
            _session.Scene is not null &&
            AnimationIndex < _session.Scene.AnimationCount &&
            _channels.TryGetValue(state.Node.Name, out var channel))
        {
            var animation = _session.Scene.Animations[AnimationIndex];
            var tps = animation.TicksPerSecond > 0.000001 ? animation.TicksPerSecond : 25.0;
            var ticks = TimeSeconds * tps;

            var scale = state.BaseScale;
            if (rawAvailable)
            {
                // Raw animation data is authoritative even when this model
                // has no Lcl Scaling curve: do not invent visibility from a
                // synthetic Assimp scaling channel.
                if (hasAuthoredKeys && state.SourceVisible)
                    scale = authoredScale;
            }
            else if (channel.ScalingKeys.Count > 0)
            {
                scale = SampleVector(channel.ScalingKeys, ticks, state.BaseScale);
                if (IsVisibilityCurve(channel.ScalingKeys))
                    scale = SampleSteppedVector(channel.ScalingKeys, ticks, state.BaseScale);

                var ignoredCollapsedCanvas =
                    state.CollapsedAncestor &&
                    IsZeroScale(scale) &&
                    channel.ScalingKeys.All(key =>
                        IsZeroScale(MatrixUtil.ToOpenTk(key.Value)));
                if (ignoredCollapsedCanvas)
                    scale = state.BaseScale;
                else
                    state.SourceVisible = IsVisibleScale(scale);
            }

            // Even if logically hidden, keep a nonsingular local transform
            // for skeleton bind recovery and the explicit debug Force mode.
            if (!state.SourceVisible || state.CollapsedAncestor && IsZeroScale(scale))
                scale = state.BaseScale;

            var rotation = SampleQuaternion(channel.RotationKeys, ticks, state.BaseRotation);
            var translation = SampleVector(channel.PositionKeys, ticks, state.BaseTranslation);
            local =
                Matrix4.CreateScale(scale) *
                Matrix4.CreateFromQuaternion(rotation) *
                Matrix4.CreateTranslation(translation);
        }
        else if (hasAuthoredKeys && state.SourceVisible && !state.CollapsedAncestor)
        {
            // The source has scale keys, but Assimp completely discarded
            // the channel. Preserve its scale animation without losing
            // the rest pose's rotation/translation.
            local =
                Matrix4.CreateScale(authoredScale) *
                Matrix4.CreateFromQuaternion(state.BaseRotation) *
                Matrix4.CreateTranslation(state.BaseTranslation);
        }

        state.Local = local;
        state.Global = state.Parent is null ? local : local * state.Parent.Global;

        foreach (var child in state.Children)
            EvaluateNode(child);
    }

    private static bool IsVisibleScale(Vector3 scale)
    {
        const float epsilon = 0.00001f;
        return float.IsFinite(scale.X) && float.IsFinite(scale.Y) &&
               MathF.Abs(scale.X) > epsilon && MathF.Abs(scale.Y) > epsilon;
    }

    private static bool IsVisibilityCurve(IList<VectorKey> keys)
        => keys.Count > 0 && keys.All(key =>
        {
            var value = MatrixUtil.ToOpenTk(key.Value);
            const float epsilon = 0.0001f;
            // Ignore Z for binary SpriteRenderer switches: AssetStudio can
            // store drawing-depth changes alongside an XY visibility switch.
            var hidden = MathF.Abs(value.X) < epsilon &&
                         MathF.Abs(value.Y) < epsilon;
            var shown = MathF.Abs(value.X - 1f) < epsilon &&
                        MathF.Abs(value.Y - 1f) < epsilon;
            return hidden || shown;
        });

    private static Vector3 SampleSteppedVector(IList<VectorKey> keys, double time, Vector3 fallback)
    {
        if (keys.Count == 0)
            return fallback;

        // Assimp keeps the FBX key times; on a repeated timestamp the last
        // authored key wins. Never interpolate a visible and hidden sprite.
        for (var i = keys.Count - 1; i >= 0; i--)
        {
            if (time + 0.000001 >= keys[i].Time)
                return MatrixUtil.ToOpenTk(keys[i].Value);
        }

        return MatrixUtil.ToOpenTk(keys[0].Value);
    }

    private static bool HasMeshDescendant(Node node)
        => node.Children.Any(child =>
            child.MeshIndices.Count > 0 || HasMeshDescendant(child));

    private static bool IsZeroScale(Vector3 scale)
    {
        const float epsilon = 0.000001f;
        return MathF.Abs(scale.X) < epsilon &&
               MathF.Abs(scale.Y) < epsilon &&
               MathF.Abs(scale.Z) < epsilon;
    }

    private static bool IsCollapsedMesh(Matrix4 m)
    {
        const float epsilon = 0.000001f;
        return MathF.Abs(m.M11) < epsilon && MathF.Abs(m.M12) < epsilon &&
               MathF.Abs(m.M13) < epsilon && MathF.Abs(m.M21) < epsilon &&
               MathF.Abs(m.M22) < epsilon && MathF.Abs(m.M23) < epsilon &&
               MathF.Abs(m.M31) < epsilon && MathF.Abs(m.M32) < epsilon &&
               MathF.Abs(m.M33) < epsilon &&
               float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43);
    }

    private static Vector3 SampleVector(IList<VectorKey> keys, double time, Vector3 fallback)
    {
        if (keys.Count == 0)
            return fallback;
        if (keys.Count == 1 || time <= keys[0].Time)
            return MatrixUtil.ToOpenTk(keys[0].Value);

        for (var i = 0; i < keys.Count - 1; i++)
        {
            if (time <= keys[i + 1].Time)
            {
                var a = keys[i];
                var b = keys[i + 1];
                var span = Math.Max(0.0000001, b.Time - a.Time);
                var t = (float)((time - a.Time) / span);
                return Vector3.Lerp(MatrixUtil.ToOpenTk(a.Value), MatrixUtil.ToOpenTk(b.Value), t);
            }
        }

        return MatrixUtil.ToOpenTk(keys[^1].Value);
    }

    private static Quaternion SampleQuaternion(IList<QuaternionKey> keys, double time, Quaternion fallback)
    {
        if (keys.Count == 0)
            return fallback;
        if (keys.Count == 1 || time <= keys[0].Time)
            return MatrixUtil.ToOpenTk(keys[0].Value);

        for (var i = 0; i < keys.Count - 1; i++)
        {
            if (time <= keys[i + 1].Time)
            {
                var a = keys[i];
                var b = keys[i + 1];
                var span = Math.Max(0.0000001, b.Time - a.Time);
                var t = (float)((time - a.Time) / span);
                return Quaternion.Slerp(MatrixUtil.ToOpenTk(a.Value), MatrixUtil.ToOpenTk(b.Value), t);
            }
        }

        return MatrixUtil.ToOpenTk(keys[^1].Value);
    }
}
