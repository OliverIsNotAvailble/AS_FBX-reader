using Assimp;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using AS.FBXReader.Core;
using AS.FBXReader.Models;

namespace AS.FBXReader.Rendering;

// First renderer pass. It intentionally keeps the GPU side simple:
// CPU skinning + one dynamic vertex buffer per draw. That is more than fast
// enough for the small 2D SpriteSkin FBXs we are targeting and makes debugging
// visibility/animation much easier than hiding state inside a GPU skinning path.
public sealed class SceneRenderer : IDisposable
{
    private FbxSession? _session;
    private AnimationPlayer? _player;
    private int _program;
    private int _vao;
    private int _vbo;
    private int _ebo;
    private bool _initialized;
    private readonly TextureCache _textures = new();
    private bool _premultiplyAlpha = true;
    private bool _transparentBackground;
    private Color _backgroundColor = Color.FromArgb(28, 28, 28);
    private FramingState _framing = new();

    public bool PremultiplyAlpha => _premultiplyAlpha;
    public bool TransparentBackground => _transparentBackground;
    public Color BackgroundColor => _backgroundColor;

    public void SetPremultiplyAlpha(bool enabled)
    {
        if (_premultiplyAlpha == enabled)
            return;

        _premultiplyAlpha = enabled;
        _textures.SetPremultiplyAlpha(enabled);
    }

    public void Initialize()
    {
        if (_initialized)
            return;

        _program = BuildProgram(VertexShader, FragmentShader);
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        _ebo = GL.GenBuffer();

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);

        GL.Enable(EnableCap.Blend);
        ApplyBlendMode();
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);

        _initialized = true;
    }

    public void SetScene(FbxSession session, AnimationPlayer player)
    {
        _session = session;
        _player = player;
        _framing = new FramingState();
    }

    public void SetBackground(bool transparent, Color color)
    {
        _transparentBackground = transparent;
        _backgroundColor = color;
    }

    public FramingState CaptureFramingReference()
    {
        var prepared = PrepareVisibleParts();
        var extents = EstimateExtents(prepared);

        _framing = new FramingState
        {
            Enabled = true,
            ReferenceMinX = extents.Min.X,
            ReferenceMaxX = extents.Max.X,
            ReferenceMinY = extents.Min.Y,
            ReferenceMaxY = extents.Max.Y,
            Zoom = 1f,
            OffsetX = 0f,
            OffsetY = 0f
        };

        return _framing.Clone();
    }

    public FramingState GetFraming() => _framing.Clone();

    public (float X, float Y) GetWorldUnitsPerPixel(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var view = BuildView(PrepareVisibleParts(), width, height);
        return ((view.Right - view.Left) / width, (view.Top - view.Bottom) / height);
    }

    public void SetFraming(FramingState framing)
    {
        _framing = framing.Clone();
        _framing.Zoom = Math.Clamp(_framing.Zoom, 0.05f, 20f);
    }

    public void SetFramingTransform(float zoom, float offsetX, float offsetY)
    {
        if (!_framing.Enabled)
            CaptureFramingReference();

        _framing.Zoom = Math.Clamp(zoom, 0.05f, 20f);
        _framing.OffsetX = Math.Clamp(offsetX, -10f, 10f);
        _framing.OffsetY = Math.Clamp(offsetY, -10f, 10f);
    }

    public ScenePart? PickPart(int screenX, int screenY, int width, int height)
    {
        if (_session?.Scene is null || _player is null || width <= 0 || height <= 0)
            return null;

        var prepared = PrepareVisibleParts();
        if (prepared.Count == 0)
            return null;

        var view = BuildView(prepared, width, height);
        var worldX = view.Left + (screenX / (float)width) * (view.Right - view.Left);
        var worldY = view.Top - (screenY / (float)height) * (view.Top - view.Bottom);
        var point = new Vector2(worldX, worldY);

        // UI order is front -> back, so hit-test in that same order and return
        // the first triangle under the cursor.
        foreach (var item in prepared)
        {
            var indices = item.Mesh.GetUnsignedIndices().ToArray();
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                var ia = (int)indices[i];
                var ib = (int)indices[i + 1];
                var ic = (int)indices[i + 2];

                if (ia < 0 || ib < 0 || ic < 0 ||
                    ia >= item.Positions.Length ||
                    ib >= item.Positions.Length ||
                    ic >= item.Positions.Length)
                {
                    continue;
                }

                var a = new Vector2(item.Positions[ia].X, item.Positions[ia].Y);
                var b = new Vector2(item.Positions[ib].X, item.Positions[ib].Y);
                var c = new Vector2(item.Positions[ic].X, item.Positions[ic].Y);

                if (PointInTriangle(point, a, b, c))
                    return item.Part;
            }
        }

        return null;
    }

    public void Render(int width, int height)
    {
        if (!_initialized)
            Initialize();

        GL.Viewport(0, 0, width, height);

        var alpha = _transparentBackground ? 0f : 1f;
        GL.ClearColor(
            _backgroundColor.R / 255f,
            _backgroundColor.G / 255f,
            _backgroundColor.B / 255f,
            alpha);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (_session?.Scene is null || _player is null)
            return;

        var prepared = PrepareVisibleParts();
        var view = BuildView(prepared, width, height);
        var projection = Matrix4.CreateOrthographicOffCenter(
            view.Left,
            view.Right,
            view.Bottom,
            view.Top,
            -10000f,
            10000f);

        ApplyBlendMode();

        GL.UseProgram(_program);
        GL.UniformMatrix4(GL.GetUniformLocation(_program, "uProjection"), false, ref projection);
        GL.Uniform4(GL.GetUniformLocation(_program, "uColor"), 1f, 1f, 1f, 1f);
        GL.Uniform1(GL.GetUniformLocation(_program, "uTexture"), 0);

        // Manual layer order is authoritative.
        // The parts list shown in the UI is FRONT -> BACK (top -> bottom), so
        // painter's-order rendering must draw it in reverse: bottom/back first,
        // top/front last. This lets the user repair ambiguous Unity sorting
        // directly by dragging items instead of depending on imperfect FBX Z data.
        for (var i = prepared.Count - 1; i >= 0; i--)
        {
            var item = prepared[i];
            BindMaterial(item.Mesh.MaterialIndex);
            DrawMesh(item.Mesh, item.Positions);
        }

        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }


    private List<PreparedPart> PrepareVisibleParts()
    {
        var prepared = new List<PreparedPart>(_session?.Parts.Count ?? 0);

        if (_session?.Scene is null || _player is null)
            return prepared;

        foreach (var part in _session.Parts)
        {
            if (!part.ShouldRender(
                    _player.CurrentAnimationName,
                    _player.TimeSeconds,
                    _player.IsSourceVisible(part.NodeName)))
                continue;

            if (!_session.NodesByName.TryGetValue(part.NodeName, out var node) || node is null)
                continue;

            var mesh = _session.Scene.Meshes[part.MeshIndex];
            prepared.Add(new PreparedPart(
                part,
                node,
                mesh,
                GetWorldPositions(node, mesh, part)));
        }

        return prepared;
    }

    private ViewBounds BuildView(IReadOnlyList<PreparedPart> prepared, int width, int height)
    {
        Vector3 min;
        Vector3 max;

        if (_framing.Enabled)
        {
            min = new Vector3(_framing.ReferenceMinX, _framing.ReferenceMinY, 0f);
            max = new Vector3(_framing.ReferenceMaxX, _framing.ReferenceMaxY, 0f);
        }
        else
        {
            var extents = EstimateExtents(prepared);
            min = extents.Min;
            max = extents.Max;
        }

        var center = (min + max) * 0.5f;
        var spanX = Math.Max(0.001f, max.X - min.X);
        var spanY = Math.Max(0.001f, max.Y - min.Y);
        var aspect = width / (float)Math.Max(1, height);

        var baseHalfH = Math.Max(spanY * 0.58f, spanX / aspect * 0.58f);
        var baseHalfW = baseHalfH * aspect;
        var zoom = _framing.Enabled ? Math.Max(0.05f, _framing.Zoom) : 1f;

        // X/Y are artwork offsets, so moving the artwork right/up means moving
        // the camera center in the opposite direction.
        if (_framing.Enabled)
        {
            center.X -= _framing.OffsetX * baseHalfW;
            center.Y -= _framing.OffsetY * baseHalfH;
        }

        var halfH = baseHalfH / zoom;
        var halfW = halfH * aspect;

        return new ViewBounds(
            center.X - halfW,
            center.X + halfW,
            center.Y - halfH,
            center.Y + halfH);
    }

    private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        static float Sign(Vector2 p1, Vector2 p2, Vector2 p3)
            => (p1.X - p3.X) * (p2.Y - p3.Y) -
               (p2.X - p3.X) * (p1.Y - p3.Y);

        var d1 = Sign(p, a, b);
        var d2 = Sign(p, b, c);
        var d3 = Sign(p, c, a);

        const float epsilon = 0.00001f;
        var hasNeg = d1 < -epsilon || d2 < -epsilon || d3 < -epsilon;
        var hasPos = d1 > epsilon || d2 > epsilon || d3 > epsilon;
        return !(hasNeg && hasPos);
    }

    private void ApplyBlendMode()
    {
        // Keep alpha accumulation correct for transparent offscreen exports.
        // RGB changes between straight and premultiplied source data; alpha is
        // always accumulated as srcA + dstA * (1-srcA).
        if (_premultiplyAlpha)
        {
            GL.BlendFuncSeparate(
                BlendingFactorSrc.One,
                BlendingFactorDest.OneMinusSrcAlpha,
                BlendingFactorSrc.One,
                BlendingFactorDest.OneMinusSrcAlpha);
        }
        else
        {
            GL.BlendFuncSeparate(
                BlendingFactorSrc.SrcAlpha,
                BlendingFactorDest.OneMinusSrcAlpha,
                BlendingFactorSrc.One,
                BlendingFactorDest.OneMinusSrcAlpha);
        }
    }

    private void BindMaterial(int materialIndex)
    {
        if (_session?.Scene is null || materialIndex < 0 || materialIndex >= _session.Scene.MaterialCount)
        {
            GL.Uniform1(GL.GetUniformLocation(_program, "uUseTexture"), 0);
            return;
        }

        var material = _session.Scene.Materials[materialIndex];
        var path = _session.ResolveTexturePath(material);
        var texture = _textures.GetOrLoad(path);
        if (texture == 0)
        {
            GL.Uniform1(GL.GetUniformLocation(_program, "uUseTexture"), 0);
            return;
        }

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.Uniform1(GL.GetUniformLocation(_program, "uUseTexture"), 1);
    }

    private Vector3[] GetWorldPositions(Node node, Mesh mesh, ScenePart part)
    {
        if (_player is null)
            return Array.Empty<Vector3>();

        var boneMatrices = mesh.HasBones
            ? _player.GetBoneMatrices(node, mesh, part.RebuildBindPose || part.PreserveShape)
            : Array.Empty<Matrix4>();
        // SpriteSkin files can have a clean mesh/UV layout but bone weights
        // that pull interior vertices away from their surrounding triangles.
        // Keep the source shape intact and follow its strongest bone as a
        // single rigid piece, instead of making the part static.
        var rigidBone = -1;
        if (part.PreserveShape && mesh.HasBones && mesh.BoneCount > 0)
        {
            var maxWeight = 0f;
            for (var b = 0; b < mesh.BoneCount; b++)
            {
                var weight = mesh.Bones[b].VertexWeights.Sum(vw => vw.Weight);
                if (weight > maxWeight)
                {
                    maxWeight = weight;
                    rigidBone = b;
                }
            }
        }
        var positions = new Vector3[mesh.VertexCount];
        var weightsByVertex = mesh.HasBones && !part.PreserveShape ? BuildWeights(mesh) : null;
        var meshGlobal = _player.GetGlobalTransform(node.Name);

        for (var i = 0; i < mesh.VertexCount; i++)
        {
            var p = MatrixUtil.ToOpenTk(mesh.Vertices[i]);
            if (part.VertexOffsets.TryGetValue(i, out var edit))
                p += new Vector3(edit.X, edit.Y, 0f);

            if (rigidBone >= 0)
            {
                p = Vector3.TransformPosition(p, boneMatrices[rigidBone]);
            }
            else if (mesh.HasBones && weightsByVertex is not null)
            {
                var skinned = Vector3.Zero;
                var total = 0f;
                foreach (var influence in weightsByVertex[i])
                {
                    if (influence.BoneIndex < 0 || influence.BoneIndex >= boneMatrices.Length)
                        continue;

                    skinned += Vector3.TransformPosition(
                        p,
                        boneMatrices[influence.BoneIndex]) * influence.Weight;
                    total += influence.Weight;
                }

                if (total > 0.00001f)
                    p = skinned / total;
            }

            positions[i] = Vector3.TransformPosition(p, meshGlobal) +
                new Vector3(part.OffsetX, part.OffsetY, 0f);
        }

        return positions;
    }

    private void DrawMesh(Mesh mesh, Vector3[] positions)
    {
        if (positions.Length != mesh.VertexCount)
            return;

        var vertexData = new float[mesh.VertexCount * 5];
        var hasUv = mesh.HasTextureCoords(0);
        for (var i = 0; i < mesh.VertexCount; i++)
        {
            var p = positions[i];
            vertexData[i * 5 + 0] = p.X;
            vertexData[i * 5 + 1] = p.Y;
            vertexData[i * 5 + 2] = p.Z;
            if (hasUv)
            {
                var uv = mesh.TextureCoordinateChannels[0][i];
                vertexData[i * 5 + 3] = uv.X;
                vertexData[i * 5 + 4] = 1f - uv.Y;
            }
        }

        var indices = mesh.GetUnsignedIndices().Select(x => (uint)x).ToArray();
        if (indices.Length == 0)
            return;

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertexData.Length * sizeof(float), vertexData, BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.DynamicDraw);
        GL.DrawElements(OpenTK.Graphics.OpenGL4.PrimitiveType.Triangles, indices.Length, DrawElementsType.UnsignedInt, 0);
    }

    private static List<Influence>[] BuildWeights(Mesh mesh)
    {
        var result = Enumerable.Range(0, mesh.VertexCount)
            .Select(_ => new List<Influence>(4))
            .ToArray();

        for (var boneIndex = 0; boneIndex < mesh.BoneCount; boneIndex++)
        {
            foreach (var vw in mesh.Bones[boneIndex].VertexWeights)
            {
                if (vw.VertexID >= 0 && vw.VertexID < result.Length)
                    result[vw.VertexID].Add(new Influence(boneIndex, vw.Weight));
            }
        }
        return result;
    }

    private static (Vector3 Min, Vector3 Max) EstimateExtents(IEnumerable<PreparedPart> parts)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var item in parts)
        {
            foreach (var p in item.Positions)
            {
                if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
                    continue;

                min = Vector3.ComponentMin(min, p);
                max = Vector3.ComponentMax(max, p);
            }
        }

        if (min.X == float.MaxValue)
            return (new Vector3(-1), new Vector3(1));

        return (min, max);
    }

    private readonly record struct ViewBounds(
        float Left,
        float Right,
        float Bottom,
        float Top);

    private sealed record PreparedPart(
        ScenePart Part,
        Node Node,
        Mesh Mesh,
        Vector3[] Positions);
    private readonly record struct Influence(int BoneIndex, float Weight);

    private static int BuildProgram(string vertex, string fragment)
    {
        int Compile(ShaderType type, string source)
        {
            var shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out var status);
            if (status == 0)
                throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
            return shader;
        }

        var vs = Compile(ShaderType.VertexShader, vertex);
        var fs = Compile(ShaderType.FragmentShader, fragment);
        var program = GL.CreateProgram();
        GL.AttachShader(program, vs);
        GL.AttachShader(program, fs);
        GL.LinkProgram(program);
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var status);
        if (status == 0)
            throw new InvalidOperationException(GL.GetProgramInfoLog(program));
        return program;
    }

    private const string VertexShader = """
        #version 330 core
        layout(location=0) in vec3 aPosition;
        layout(location=1) in vec2 aUv;
        uniform mat4 uProjection;
        out vec2 vUv;
        void main() {
            vUv = aUv;
            gl_Position = uProjection * vec4(aPosition, 1.0);
        }
        """;

    private const string FragmentShader = """
        #version 330 core
        in vec2 vUv;
        uniform vec4 uColor;
        uniform bool uUseTexture;
        uniform sampler2D uTexture;
        out vec4 fragColor;
        void main() {
            vec4 texel = uUseTexture ? texture(uTexture, vUv) : vec4(1.0);
            if (texel.a < 0.001) discard;
            fragColor = texel * uColor;
        }
        """;

    public void Dispose()
    {
        if (!_initialized)
            return;
        GL.DeleteBuffer(_vbo);
        GL.DeleteBuffer(_ebo);
        GL.DeleteVertexArray(_vao);
        _textures.Dispose();
        GL.DeleteProgram(_program);
        _initialized = false;
    }
}
