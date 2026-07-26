using OpenTK.Graphics.OpenGL4;

namespace IslandRpg.Rendering;

internal sealed class ShaderUniformCache
{
    private readonly Dictionary<int, Dictionary<string, int>> _programs = [];

    public int Get(int program, string name)
    {
        if (!_programs.TryGetValue(program, out var uniforms))
        {
            uniforms = new(StringComparer.Ordinal);
            _programs.Add(program, uniforms);
        }
        if (uniforms.TryGetValue(name, out var location))
            return location;
        location = GL.GetUniformLocation(program, name);
        uniforms.Add(name, location);
        return location;
    }

    public void Remove(int program) => _programs.Remove(program);
}
