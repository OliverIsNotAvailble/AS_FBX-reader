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
        public Matrix4 Global { get; set; }
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
        _states.Clear();
        _channels.Clear();
        TimeSeconds = 0;

        if (_session.Scene?.RootNode is null)
            return;

        _root = BuildTree(_session.Scene.RootNode, null);
        SelectAnimation(AnimationIndex);
        Evaluate();
    }

    public void SelectAnimation(int index)
    {
        AnimationIndex = index;
        TimeSeconds = 0;
        _channels.Clear();

        if (_session.Scene is null || index < 0 || index >= _session.Scene.AnimationCount)
        {
            Evaluate();
            return;
        }

        foreach (var channel in _session.Scene.Animations[index].NodeAnimationChannels)
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

    public Matrix4[] GetBoneMatrices(Node meshNode, Mesh mesh)
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

            // OpenTK TransformPosition uses row-vector semantics:
            // p' = p * matrix. Therefore the standard Assimp skinning chain is
            // offset * boneGlobal * inverseMeshGlobal.
            result[i] = offset * boneGlobal * inverseMesh;
        }
        return result;
    }

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

        var baseScale = Vector3.One;
        var baseRotation = Quaternion.Identity;
        var baseTranslation = Vector3.Zero;
        if (System.Numerics.Matrix4x4.Decompose(
                node.Transform,
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
            BaseScale = baseScale,
            BaseRotation = baseRotation,
            BaseTranslation = baseTranslation
        };

        // Row-vector convention: child local transform is applied first,
        // then its parent's global transform.
        state.Global = parent is null ? state.Local : state.Local * parent.Global;
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
        var local = MatrixUtil.ToOpenTk(state.Node.Transform);

        if (AnimationIndex >= 0 &&
            _session.Scene is not null &&
            _channels.TryGetValue(state.Node.Name, out var channel))
        {
            var animation = _session.Scene.Animations[AnimationIndex];
            var tps = animation.TicksPerSecond > 0.000001 ? animation.TicksPerSecond : 25.0;
            var ticks = TimeSeconds * tps;

            var scale = SampleVector(channel.ScalingKeys, ticks, state.BaseScale);
            var rotation = SampleQuaternion(channel.RotationKeys, ticks, state.BaseRotation);
            var translation = SampleVector(channel.PositionKeys, ticks, state.BaseTranslation);

            local =
                Matrix4.CreateScale(scale) *
                Matrix4.CreateFromQuaternion(rotation) *
                Matrix4.CreateTranslation(translation);
        }

        state.Local = local;
        state.Global = state.Parent is null ? local : local * state.Parent.Global;

        foreach (var child in state.Children)
            EvaluateNode(child);
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
