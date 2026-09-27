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
    private float _zoom = 1.0f;
    private readonly TextureCache _textures = new();

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
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);

        _initialized = true;
    }

    public void SetScene(FbxSession session, AnimationPlayer player)
    {
        _session = session;
        _player = player;
    }

    public void Render(int width, int height)
    {
        if (!_initialized)
            Initialize();

        GL.Viewport(0, 0, width, height);
        GL.ClearColor(0.11f, 0.11f, 0.11f, 1.0f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (_session?.Scene is null || _player is null)
            return;

        var extents = EstimateExtents(_session.Scene);
        var center = (extents.Min + extents.Max) * 0.5f;
        var spanX = Math.Max(1f, extents.Max.X - extents.Min.X);
        var spanY = Math.Max(1f, extents.Max.Y - extents.Min.Y);
        var aspect = width / (float)Math.Max(1, height);
        var halfH = Math.Max(spanY * 0.55f, spanX / aspect * 0.55f) / _zoom;
        var halfW = halfH * aspect;
        var projection = Matrix4.CreateOrthographicOffCenter(
            center.X - halfW,
            center.X + halfW,
            center.Y - halfH,
            center.Y + halfH,
            -10000f,
            10000f);

        GL.UseProgram(_program);
        GL.UniformMatrix4(GL.GetUniformLocation(_program, "uProjection"), false, ref projection);
        GL.Uniform4(GL.GetUniformLocation(_program, "uColor"), 1f, 1f, 1f, 1f);
        GL.Uniform1(GL.GetUniformLocation(_program, "uTexture"), 0);

        foreach (var part in _session.Parts)
        {
            if (!part.Visible)
                continue;

            var node = _session.NodesByName.TryGetValue(part.NodeName, out var found)
                ? found
                : null;
            if (node is null)
                continue;

            var mesh = _session.Scene.Meshes[part.MeshIndex];
            BindMaterial(mesh.MaterialIndex);
            DrawMesh(node, mesh);
        }

        GL.BindVertexArray(0);
        GL.UseProgram(0);
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

    private void DrawMesh(Node node, Mesh mesh)
    {
        if (_player is null)
            return;

        var boneMatrices = mesh.HasBones ? _player.GetBoneMatrices(node, mesh) : Array.Empty<Matrix4>();
        var positions = new Vector3[mesh.VertexCount];
        var weightsByVertex = mesh.HasBones ? BuildWeights(mesh) : null;

        for (var i = 0; i < mesh.VertexCount; i++)
        {
            var p = MatrixUtil.ToOpenTk(mesh.Vertices[i]);

            if (mesh.HasBones && weightsByVertex is not null)
            {
                var skinned = Vector3.Zero;
                var total = 0f;
                foreach (var influence in weightsByVertex[i])
                {
                    if (influence.BoneIndex < 0 || influence.BoneIndex >= boneMatrices.Length)
                        continue;
                    var transformed = Vector3.TransformPosition(p, boneMatrices[influence.BoneIndex]);
                    skinned += transformed * influence.Weight;
                    total += influence.Weight;
                }
                p = total > 0.00001f ? skinned / total : p;
            }

            p = Vector3.TransformPosition(p, _player.GetGlobalTransform(node.Name));
            positions[i] = p;
        }

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

    private static (Vector3 Min, Vector3 Max) EstimateExtents(Scene scene)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var mesh in scene.Meshes)
        {
            foreach (var v in mesh.Vertices)
            {
                var p = MatrixUtil.ToOpenTk(v);
                min = Vector3.ComponentMin(min, p);
                max = Vector3.ComponentMax(max, p);
            }
        }
        if (min.X == float.MaxValue)
            return (new Vector3(-1), new Vector3(1));
        return (min, max);
    }

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
