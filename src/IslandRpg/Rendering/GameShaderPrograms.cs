using OpenTK.Graphics.OpenGL4;

namespace IslandRpg.Rendering;

internal static class GameShaderPrograms
{
    public static int CreateClassicPcScreenProgram()
    {
        const string vertex = "#version 330 core\nlayout(location=0) in vec2 p; layout(location=1) in vec2 uvIn; out vec2 uv; void main(){uv=uvIn;gl_Position=vec4(p,0,1);}";
        const string fragment = """
            #version 330 core
            in vec2 uv;
            out vec4 color;
            uniform sampler2D image;

            float roundedGlassMask(vec2 value) {
                const float radius = .026;
                vec2 centered = abs(value - vec2(.5));
                vec2 corner = max(
                    centered - vec2(.5 - radius), vec2(0.0));
                float distance = length(corner) - radius;
                return 1.0 - smoothstep(-.003, .003, distance);
            }

            void main() {
                float glass = roundedGlassMask(uv);
                if (glass <= .001) discard;
                vec4 source = texture(image, uv);
                // A restrained edge falloff seats the rendered image behind the
                // monitor glass instead of leaving a visibly square texture edge.
                vec2 edge = uv * (1.0 - uv);
                float innerVignette = pow(clamp(
                    edge.x * edge.y * 19.0, 0.0, 1.0), .055);
                color = vec4(source.rgb * innerVignette, source.a * glass);
            }
            """;
        return CreateProgram(vertex, fragment);
    }

    public static int CreateCrtSignalProgram()
    {
        const string vertex = "#version 330 core\nlayout(location=0) in vec2 p; layout(location=1) in vec2 uvIn; out vec2 uv; void main(){uv=uvIn;gl_Position=vec4(p,0,1);}";
        const string fragment = """
            #version 330 core
            in vec2 uv;
            out vec4 color;
            uniform sampler2D image;
            uniform vec2 sourceSize;
            const float InputGamma = 2.4;

            vec3 sourceAt(vec2 sampleUv) {
                vec2 texel = 1.0 / max(sourceSize, vec2(1.0));
                vec3 center = pow(max(texture(image, sampleUv).rgb,
                    vec3(0.0)), vec3(InputGamma));
                vec3 nearLeft = pow(max(texture(image,
                    sampleUv - vec2(texel.x, 0.0)).rgb,
                    vec3(0.0)), vec3(InputGamma));
                vec3 nearRight = pow(max(texture(image,
                    sampleUv + vec2(texel.x, 0.0)).rgb,
                    vec3(0.0)), vec3(InputGamma));
                vec3 farLeft = pow(max(texture(image,
                    sampleUv - vec2(texel.x * 2.0, 0.0)).rgb,
                    vec3(0.0)), vec3(InputGamma));
                vec3 farRight = pow(max(texture(image,
                    sampleUv + vec2(texel.x * 2.0, 0.0)).rgb,
                    vec3(0.0)), vec3(InputGamma));
                return center * .56 + (nearLeft + nearRight) * .18 +
                    (farLeft + farRight) * .04;
            }

            float beamWeight(float distance, vec3 signal) {
                float luminance = dot(signal, vec3(.2126, .7152, .0722));
                float width = mix(.20, .44,
                    sqrt(clamp(luminance, 0.0, 1.0)));
                width += fwidth(distance) * .28;
                return exp(-2.15 * pow(distance / width, 2.0));
            }

            void main() {
                // A monitor-like 480-line signal is independent of the physical
                // LCD resolution. Two adjacent raster lines are integrated with
                // brightness-dependent Gaussian beam widths.
                float signalHeight = min(480.0, sourceSize.y);
                vec2 signalSize = vec2(
                    floor(signalHeight * sourceSize.x / sourceSize.y + .5),
                    signalHeight);
                vec2 signalPosition = uv * signalSize - vec2(.5);
                float line = floor(signalPosition.y);
                float lineDistance = fract(signalPosition.y);
                vec2 firstUv = vec2(uv.x, (line + .5) / signalSize.y);
                vec2 secondUv = vec2(uv.x, (line + 1.5) / signalSize.y);
                vec3 first = sourceAt(firstUv);
                vec3 second = sourceAt(secondUv);
                vec3 beam = first * beamWeight(lineDistance, first) +
                    second * beamWeight(1.0 - lineDistance, second);
                color = vec4(beam * 1.12, 1.0);
            }
            """;
        return CreateProgram(vertex, fragment);
    }

    public static int CreateCrtTubeProgram()
    {
        const string vertex = "#version 330 core\nlayout(location=0) in vec2 p; layout(location=1) in vec2 uvIn; out vec2 uv; void main(){uv=uvIn;gl_Position=vec4(p,0,1);}";
        const string fragment = """
            #version 330 core
            in vec2 uv;
            out vec4 color;
            uniform sampler2D image;
            uniform vec2 sourceSize;
            uniform vec2 outputSize;
            uniform float time;

            vec2 curve(vec2 value) {
                vec2 centered = value * 2.0 - 1.0;
                float radiusSquared = dot(centered, centered);
                centered += centered * vec2(.032, .044) * radiusSquared;
                centered *= vec2(.972, .965);
                return centered * .5 + .5;
            }

            vec3 signalAt(vec2 value) {
                return texture(image, clamp(value, vec2(0.0), vec2(1.0))).rgb;
            }

            vec3 phosphorMask(vec2 pixel) {
                // Three RGB slots form one shadow-mask triad. The second row is
                // offset like a slot-mask tube, but remains independent from the
                // raster scanline phase reconstructed in the previous pass.
                float pitch = max(1.0, floor(outputSize.y / 720.0));
                float maskRow = floor(pixel.y / (pitch * 2.0));
                float shiftedX = pixel.x + mod(maskRow, 2.0) * pitch * 1.5;
                float channel = floor(mod(shiftedX, pitch * 3.0) / pitch);
                vec3 mask = vec3(.56);
                if (channel < 1.0) mask.r = 1.18;
                else if (channel < 2.0) mask.g = 1.18;
                else mask.b = 1.18;
                float rowPosition = fract(pixel.y / (pitch * 2.0));
                float slot = mix(.76, 1.0,
                    smoothstep(.08, .25, rowPosition) *
                    (1.0 - smoothstep(.75, .92, rowPosition)));
                return mask * slot;
            }

            void main() {
                vec2 warped = curve(uv);
                if (warped.x <= 0.0 || warped.x >= 1.0 ||
                    warped.y <= 0.0 || warped.y >= 1.0) {
                    color = vec4(.0025, .003, .0025, 1.0);
                    return;
                }

                vec2 texel = 1.0 / max(sourceSize, vec2(1.0));
                // Slight beam convergence error is channel-specific, not a
                // generic RGB tint over every output pixel.
                vec3 signal;
                signal.r = signalAt(warped + vec2(texel.x * .32, 0.0)).r;
                signal.g = signalAt(warped).g;
                signal.b = signalAt(warped - vec2(texel.x * .28, 0.0)).b;

                vec3 nearGlow =
                    signalAt(warped + vec2(texel.x * 2.0, 0.0)) +
                    signalAt(warped - vec2(texel.x * 2.0, 0.0)) +
                    signalAt(warped + vec2(0.0, texel.y * 2.0)) +
                    signalAt(warped - vec2(0.0, texel.y * 2.0));
                vec3 farGlow =
                    signalAt(warped + vec2(texel.x * 6.0, texel.y * 3.0)) +
                    signalAt(warped + vec2(-texel.x * 6.0, texel.y * 3.0)) +
                    signalAt(warped + vec2(texel.x * 6.0, -texel.y * 3.0)) +
                    signalAt(warped - vec2(texel.x * 6.0, texel.y * 3.0));
                vec3 bloom = nearGlow * .035 + farGlow * .013;
                vec3 halation = bloom * vec3(1.06, .91, .78);

                vec2 physicalPixel = uv * outputSize;
                vec3 tube = signal * phosphorMask(physicalPixel) * 1.28;
                tube += bloom * .18 + halation * .10;

                vec2 edge = warped * (1.0 - warped);
                float vignette = pow(clamp(
                    edge.x * edge.y * 17.0, 0.0, 1.0), .20);
                float glass = 1.0 - .045 * dot(
                    warped - vec2(.5), warped - vec2(.5));
                tube *= vignette * glass;
                tube += max(tube - .78, vec3(0.0)) * .08;
                color = vec4(pow(max(tube, vec3(0.0)),
                    vec3(1.0 / 2.2)), 1.0);
            }
            """;
        return CreateProgram(vertex, fragment);
    }

    public static int CreateSoftShadowProgram()
    {
        const string vertex = "#version 330 core\nlayout(location=0) in vec2 p; layout(location=1) in vec2 uvIn; out vec2 uv; void main(){uv=uvIn;gl_Position=vec4(p,0,1);}";
        const string fragment = """
            #version 330 core
            in vec2 uv;
            out vec4 color;
            uniform float opacity;
            void main() {
                vec2 centered = (uv - vec2(.5)) * 2.0;
                float distanceSquared = dot(centered, centered);
                if (distanceSquared >= 1.0) discard;
                float alpha = pow(1.0 - distanceSquared, 1.7) * opacity;
                color = vec4(.055, .060, .052, alpha);
            }
            """;
        return CreateProgram(vertex, fragment);
    }

    public static int CreateSlimeImpactProgram()
    {
        const string vertex = "#version 330 core\nlayout(location=0) in vec2 p; layout(location=1) in vec2 uvIn; out vec2 uv; void main(){uv=uvIn;gl_Position=vec4(p,0,1);}";
        const string fragment = """
            #version 330 core
            in vec2 uv;
            out vec4 color;
            uniform vec3 effectColor;
            uniform float progress;
            uniform float opacity;
            uniform float distortion;
            float hash(vec2 p) {
                return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
            }
            void main() {
                vec2 p = (uv - .5) * 2.0;
                p.y *= 1.75;
                float angle = atan(p.y, p.x);
                float noise = (hash(vec2(floor(angle * 13.0), 2.0)) - .5) * .09;
                float radius = length(p) + noise * distortion;
                float ringAt = mix(.10, .88, progress);
                float ring = 1.0 - smoothstep(.025, .105,
                    abs(radius - ringAt));
                float core = (1.0 - smoothstep(0.0, .52, radius)) *
                    (1.0 - progress) * .42;
                float alpha = (ring + core) * opacity *
                    (1.0 - smoothstep(.76, 1.0, radius));
                if (alpha < .008) discard;
                vec3 glow = effectColor * (1.25 + ring * .55);
                color = vec4(glow, alpha);
            }
            """;
        return CreateProgram(vertex, fragment);
    }

    public static int CreateCinematicLightningProgram()
    {
        const string vertex = "#version 330 core\nlayout(location=0) in vec2 p; layout(location=1) in vec2 uvIn; out vec2 uv; void main(){uv=uvIn;gl_Position=vec4(p,0,1);}";
        const string fragment = """
            #version 330 core
            in vec2 uv;
            out vec4 color;
            uniform vec2 target;
            uniform float time;
            uniform float intensity;
            uniform float aspect;
            float hash(float value) {
                return fract(sin(value * 91.731 + 17.17) * 43758.5453);
            }
            void main() {
                if (uv.y > target.y) discard;
                float segment = floor(uv.y * 18.0);
                float taper = clamp(uv.y / max(target.y, 0.001), 0.0, 1.0);
                float jagged = (hash(segment + floor(time * 7.0)) - 0.5) *
                    mix(0.13, 0.025, taper);
                float boltX = mix(0.44, target.x, taper) + jagged;
                float distanceToBolt = abs(uv.x - boltX) * aspect;
                float core = 1.0 - smoothstep(0.0015, 0.0055, distanceToBolt);
                float glow = 1.0 - smoothstep(0.003, 0.022, distanceToBolt);
                float branchGate = step(0.63, hash(segment + 4.3));
                float branchX = boltX + (uv.y - segment / 18.0) * .38;
                float branch = (1.0 - smoothstep(0.002, 0.006,
                    abs(uv.x - branchX) * aspect)) * branchGate * .55;
                float alpha = (core + glow * .45 + branch) * intensity;
                if (alpha < .01) discard;
                color = vec4(vec3(.72, .86, 1.0) * (1.2 + core), alpha);
            }
            """;
        return CreateProgram(vertex, fragment);
    }

    public static int CreateCinematicOceanProgram()
    {
        const string vertex = "#version 330 core\nlayout(location=0) in vec2 p; layout(location=1) in vec2 uvIn; out vec2 uv; void main(){uv=uvIn;gl_Position=vec4(p,0,1);}";
        const string fragment = """
            #version 330 core
            in vec2 uv;
            out vec4 color;
            uniform sampler2DArray terrain;
            uniform sampler2DArray waterNormals;
            uniform float time;
            uniform float lightning;
            uniform float cameraZoom;
            uniform vec2 cameraOffset;
            void main() {
                vec2 cameraUv = (uv - vec2(.5)) /
                    max(cameraZoom, .001) + vec2(.5);
                vec2 flowA = vec2(time * 0.016, time * 0.005);
                vec2 flowB = vec2(-time * 0.009, time * 0.012);
                vec2 n1 = texture(waterNormals,
                    vec3(cameraUv * vec2(8.0, 5.0) + flowA, 0.0)).xy * 2.0 - 1.0;
                vec2 n2 = texture(waterNormals,
                    vec3(cameraUv * vec2(5.0, 8.0) + flowB, 1.0)).xy * 2.0 - 1.0;
                vec2 waves = n1 * 0.82 + n2 * 0.58;
                float swell = sin((uv.x * 19.0 + uv.y * 7.0) - time * 1.35) * 0.5 + 0.5;
                float crossSwell = sin((uv.x * -11.0 + uv.y * 17.0) - time * 1.08) * 0.5 + 0.5;
                vec2 sampleUv = (cameraUv + cameraOffset) * vec2(7.0, 4.5) + waves * 0.075;
                vec3 ageWater = texture(terrain, vec3(sampleUv, 0.0)).rgb;
                float wavePeak = length(waves) * 0.62 + swell * 0.38 + crossSwell * 0.22;
                float crest = smoothstep(0.68, 0.94, wavePeak);
                float crestBreakup = smoothstep(0.67, 0.88,
                    fract(uv.x * 37.0 + uv.y * 23.0 - time * .7));
                float whitecap = smoothstep(0.88, 1.08, wavePeak) * crestBreakup;
                vec3 night = ageWater * vec3(0.09, 0.12, 0.175);
                night += vec3(0.03, 0.065, 0.09) * crest;
                night = mix(night, vec3(0.31, 0.39, 0.43), whitecap * .22);
                night += vec3(0.55, 0.66, 0.78) * lightning *
                    (0.35 + crest * 0.65);
                float rainCell = fract((uv.x * 1.7 + uv.y) * 86.0 - time * 1.9);
                float rainBand = step(0.925, rainCell) *
                    step(fract(uv.x * 173.0 + time * 0.37), 0.58);
                night += vec3(0.10, 0.14, 0.19) * rainBand * 0.18;
                color = vec4(night, 1.0);
            }
            """;
        return CreateProgram(vertex, fragment);
    }

    private static int CreateProgram(string vertex, string fragment)
    {
        static int Compile(ShaderType type, string source)
        {
            var shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out var ok);
            if (ok == 0)
                throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
            return shader;
        }
        var vs = Compile(ShaderType.VertexShader, vertex);
        var fs = Compile(ShaderType.FragmentShader, fragment);
        var program = GL.CreateProgram();
        GL.AttachShader(program, vs);
        GL.AttachShader(program, fs);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linked);
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);
        if (linked == 0)
            throw new InvalidOperationException(GL.GetProgramInfoLog(program));
        return program;
    }

    public static int CreateDemoProgram()
    {
        const string vertex = "#version 330 core\nlayout(location=0) in vec2 p; layout(location=1) in vec2 uv; out vec2 tex; void main(){tex=uv;gl_Position=vec4(p,0,1);}";
        const string fragment = "#version 330 core\nin vec2 tex; out vec4 color; uniform sampler2D image; uniform vec4 tint; uniform int useTexture; void main(){color=useTexture==1?texture(image,tex):tint;}";
        int Compile(ShaderType type, string source)
        {
            var shader = GL.CreateShader(type); GL.ShaderSource(shader, source); GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out var ok);
            if (ok == 0) throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
            return shader;
        }
        var vs = Compile(ShaderType.VertexShader, vertex);
        var fs = Compile(ShaderType.FragmentShader, fragment);
        var program = GL.CreateProgram(); GL.AttachShader(program, vs); GL.AttachShader(program, fs); GL.LinkProgram(program);
        GL.DeleteShader(vs); GL.DeleteShader(fs);
        return program;
    }

    public static int CreateCliffProgram()
    {
        int Compile(ShaderType type, string source)
        {
            var shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out var ok);
            if (ok == 0) throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
            return shader;
        }

        const string vertex = """
            #version 330 core
            layout(location=0) in vec2 world;
            layout(location=1) in vec2 textureUv;
            layout(location=2) in float opacity;
            uniform vec2 viewport;
            uniform vec2 camera;
            uniform float zoom;
            out vec2 uv;
            out float alpha;
            void main() {
                vec2 pixel = world * zoom + camera;
                gl_Position = vec4(pixel.x * 2.0 / viewport.x,
                                  -pixel.y * 2.0 / viewport.y, 0.0, 1.0);
                uv = textureUv;
                alpha = opacity;
            }
            """;
        const string fragment = """
            #version 330 core
            in vec2 uv;
            in float alpha;
            out vec4 color;
            void main() {
                color = vec4(0.20, 0.15, 0.10, alpha);
            }
            """;
        var vs = Compile(ShaderType.VertexShader, vertex);
        var fs = Compile(ShaderType.FragmentShader, fragment);
        var program = GL.CreateProgram();
        GL.AttachShader(program, vs);
        GL.AttachShader(program, fs);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linked);
        if (linked == 0) throw new InvalidOperationException(GL.GetProgramInfoLog(program));
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);
        return program;
    }

    public static int CreateTerrainProgram()
    {
        int Compile(ShaderType type, string source)
        {
            var shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out var ok);
            if (ok == 0) throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
            return shader;
        }

        const string vertex = """
            #version 330 core
            layout(location=0) in vec2 world;
            layout(location=1) in vec2 textureUv;
            layout(location=2) in vec2 tileUv;
            layout(location=3) in vec3 layerPNE;
            layout(location=4) in vec2 layerSW;
            layout(location=5) in float slopeShade;
            uniform vec2 viewport;
            uniform vec2 camera;
            uniform float zoom;
            out vec2 uv;
            out vec2 mapUv;
            out float terrainShade;
            void main() {
                vec2 pixel = world * zoom + camera;
                gl_Position = vec4(pixel.x * 2.0 / viewport.x,
                                  -pixel.y * 2.0 / viewport.y, 0.0, 1.0);
                uv = textureUv;
                mapUv = clamp(tileUv, 0.0, 1.0);
                terrainShade = slopeShade;
            }
            """;
        const string fragment = """
            #version 330 core
            in vec2 uv;
            in vec2 mapUv;
            in float terrainShade;
            uniform sampler2DArray terrain;
            uniform sampler2D biomeWeightsA;
            uniform sampler2D biomeWeightsB;
            uniform sampler2D biomeWeightsC;
            uniform sampler2D biomeWeightsD;
            uniform sampler2D shoreDistance;
            uniform sampler2DArray waterNormals;
            uniform float time;
            uniform float opacity;
            uniform int rippleCount;
            uniform vec2 ripplePositions[8];
            uniform float rippleAges[8];
            out vec4 color;
            vec2 hash22(vec2 p) {
                vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973));
                p3 += dot(p3, p3.yzx + 33.33);
                return fract((p3.xx + p3.yz) * p3.zy);
            }
            float valueNoise(vec2 p) {
                vec2 cell = floor(p);
                vec2 f = fract(p);
                f = f*f*(3.0-2.0*f);
                float a = hash22(cell).x;
                float b = hash22(cell+vec2(1,0)).x;
                float c = hash22(cell+vec2(0,1)).x;
                float d = hash22(cell+vec2(1,1)).x;
                return mix(mix(a,b,f.x), mix(c,d,f.x), f.y);
            }
            vec4 sampleLayerAt(float layer, vec2 coordinates) {
                // Minecraft-style deterministic block variation, softened into
                // stochastic tiling so repeated sheets do not form a grid.
                vec2 cell = floor(coordinates);
                vec2 f = fract(coordinates);
                vec2 blend = f*f*(3.0-2.0*f);
                vec2 a = hash22(cell);
                vec2 b = hash22(cell+vec2(1,0));
                vec2 c = hash22(cell+vec2(0,1));
                vec2 d = hash22(cell+vec2(1,1));
                vec4 ca = texture(terrain, vec3(f+a, layer));
                vec4 cb = texture(terrain, vec3(f+b, layer));
                vec4 cc = texture(terrain, vec3(f+c, layer));
                vec4 cd = texture(terrain, vec3(f+d, layer));
                vec4 result = mix(mix(ca,cb,blend.x), mix(cc,cd,blend.x), blend.y);
                float macro = valueNoise(coordinates * 0.20) - 0.5;
                float strength = layer < 1.5 ? 0.025 : 0.07;
                result.rgb *= 1.0 + macro * strength;
                return result;
            }
            vec4 sampleLayer(float layer) { return sampleLayerAt(layer, uv); }
            vec4 sampleWaterLayer(float layer, vec2 coordinates) {
                // Retain some of the soft, cloudy variation from the stochastic
                // terrain blend, but keep the original water sheet dominant.
                vec4 primary = texture(terrain, vec3(coordinates, layer));
                vec4 organic = sampleLayerAt(layer, coordinates * 0.72);
                return mix(primary, organic, 0.32);
            }
            vec2 normalXY(float layer, vec2 coordinates) {
                return texture(waterNormals, vec3(coordinates, layer)).xy * 2.0 - 1.0;
            }
            void main() {
                vec4 a = texture(biomeWeightsA, mapUv);
                vec4 b = texture(biomeWeightsB, mapUv);
                vec4 c = texture(biomeWeightsC, mapUv);
                vec4 d = texture(biomeWeightsD, mapUv);
                float shorelineDistance = (texture(shoreDistance, mapUv).r * 2.0 - 1.0) * 8.0;
                float shorelineProximity = 1.0 - smoothstep(0.0, 3.0, abs(shorelineDistance));
                float total = max(dot(a, vec4(1.0)) + dot(b, vec4(1.0)) +
                                  dot(c, vec4(1.0)) + dot(d, vec4(1.0)), 0.001);
                color = vec4(0.0);
                float waterWeight = dot(a, vec4(1.0));
                float waterCoverage = clamp(waterWeight / total, 0.0, 1.0);
                float surfaceEffect = smoothstep(0.38, 0.72, waterCoverage);
                vec3 waterNormal = vec3(0.0, 0.0, 1.0);
                vec2 waterDistortion = vec2(0.0);
                vec2 primaryFlow = vec2(1.0, 0.0);
                vec2 secondaryFlow = vec2(0.0, 1.0);
                float waveSlope = 0.0;
                if (waterWeight > 0.002) {
                    // IslandMap-style regional currents, evaluated in global
                    // world UVs so neighbouring streamed chunks remain seamless.
                    float flowAngle = (valueNoise(uv * 0.11) - 0.5) * 5.2;
                    primaryFlow = vec2(cos(flowAngle), sin(flowAngle));
                    float secondAngle = flowAngle + 1.75 +
                        (valueNoise(uv * 0.17 + 19.7) - 0.5) * 0.8;
                    secondaryFlow = vec2(cos(secondAngle), sin(secondAngle));
                    vec2 deepA = normalXY(
                        0.0, uv * 1.35 + primaryFlow * time * 0.034);
                    vec2 deepB = normalXY(
                        1.0, uv * 0.73 + secondaryFlow * time * 0.025);
                    vec2 shoreA = normalXY(
                        2.0, uv * 1.65 + primaryFlow * time * 0.021);
                    vec2 shoreB = normalXY(
                        3.0, uv * 0.92 - secondaryFlow * time * 0.019);
                    float shallow = (a.g + a.b + a.a) / max(waterWeight, 0.001);
                    vec2 waves = mix(deepA * 0.62 + deepB * 0.38,
                                     shoreA * 0.62 + shoreB * 0.38, shallow);
                    waveSlope = length(waves);
                    // Shallow water keeps its own wave pattern, but uses the
                    // same normal strength so its moving shine does not disappear.
                    waterNormal = normalize(vec3(waves * 1.12, 1.0));
                    waterDistortion =
                        waterNormal.xy * mix(0.027, 0.014, shallow);
                }
                vec4 deepWaterSample = vec4(0.0);
                if (a.r > 0.002 || a.g > 0.002) {
                    deepWaterSample = sampleWaterLayer(0.0, uv + waterDistortion);
                }
                if (a.r > 0.002) color += deepWaterSample * a.r;
                if (a.g > 0.002) {
                    vec4 lightWaterSample =
                        sampleWaterLayer(1.0, uv + waterDistortion);
                    // Three-stage ocean falloff: dark open sea, a related
                    // mid-blue shelf, then the lighter blue immediately offshore.
                    float coastalStage = 1.0 - smoothstep(0.35, 7.0,
                        max(shorelineDistance, 0.0));
                    // Keep the middle shelf related to the deep ocean, while
                    // allowing the actual coastal strip to reach the light sheet.
                    float shelfLight = mix(0.72, 1.0, coastalStage);
                    vec4 stagedShelf = mix(deepWaterSample, lightWaterSample, shelfLight);
                    color += stagedShelf * a.g;
                }
                if (a.b > 0.002) color += sampleWaterLayer(2.0, uv + waterDistortion) * a.b;
                if (a.a > 0.002) color += sampleWaterLayer(3.0, uv + waterDistortion) * a.a;
                if (b.r > 0.002) color += sampleLayer(4.0) * b.r;
                if (b.g > 0.002) color += sampleLayer(5.0) * b.g;
                if (b.b > 0.002) color += sampleLayer(6.0) * b.b;
                if (b.a > 0.002) color += sampleLayer(7.0) * b.a;
                if (c.r > 0.002) color += sampleLayer(8.0) * c.r;
                if (c.g > 0.002) color += sampleLayer(9.0) * c.g;
                if (c.b > 0.002) color += sampleLayer(10.0) * c.b;
                if (c.a > 0.002) color += sampleLayer(11.0) * c.a;
                if (d.r > 0.002) color += sampleLayer(12.0) * d.r;
                if (d.g > 0.002) color += sampleLayer(13.0) * d.g;
                if (d.b > 0.002) color += sampleLayer(14.0) * d.b;
                if (d.a > 0.002) color += sampleLayer(15.0) * d.a;
                color /= total;
                // Directional relief lighting affects land only. The upper-right
                // light direction matches the classic isometric hill treatment.
                float snowCoverage = clamp(d.g / total, 0.0, 1.0);
                float softenedShade = mix(terrainShade, 1.0, snowCoverage * 0.62);
                color.rgb *= mix(softenedShade, 1.0, waterCoverage);
                if (snowCoverage > 0.001 && softenedShade < 1.0) {
                    // Snow shadows retain cool skylight instead of turning grey.
                    color.rgb += vec3(0.025, 0.040, 0.065) *
                                 (1.0 - softenedShade) * snowCoverage;
                }
                if (waterWeight > 0.002) {
                    vec3 lightDirection = normalize(vec3(-0.38, -0.48, 0.79));
                    float sparkle = pow(max(dot(waterNormal, lightDirection), 0.0), 30.0);
                    float broadHighlight = pow(max(dot(waterNormal, lightDirection), 0.0), 7.0);
                    float crest = smoothstep(0.22, 0.62, length(waterNormal.xy));
                    vec3 reflection = vec3(0.38, 0.70, 0.84) * broadHighlight * 0.11 +
                                      vec3(0.86, 0.97, 1.0) * sparkle * 0.38 +
                                      vec3(0.24, 0.58, 0.67) * crest * 0.075;
                    color.rgb = mix(color.rgb,
                        color.rgb * (0.96 + broadHighlight * 0.11), surfaceEffect);
                    color.rgb += reflection * surfaceEffect;

                    // Whitecaps only form where an animated wave is steep and
                    // the moving breakup field selects a short-lived crest.
                    float breakupA = valueNoise(uv * 2.2 + primaryFlow * time * 0.18);
                    float breakupB = valueNoise(uv * 5.4 - secondaryFlow * time * 0.11 + 31.4);
                    float breakup = breakupA * 0.62 + breakupB * 0.38;
                    float steepCrest = smoothstep(0.48, 0.88, waveSlope);
                    float sparsePatch = smoothstep(0.61, 0.79, breakup);
                    float shallowBoost = smoothstep(
                        0.10, 0.62, (a.g + a.b + a.a) / max(waterWeight, 0.001));
                    float shoreBoost = max(shallowBoost,
                        shorelineProximity * step(0.0, shorelineDistance));
                    float foam = steepCrest * sparsePatch * mix(0.42, 0.82, shoreBoost);
                    vec3 foamColor = vec3(0.84, 0.94, 0.95);
                    color.rgb = mix(color.rgb, foamColor, foam * surfaceEffect * 0.48);

                    // Each planted foot emits a circular world-space impulse.
                    // Isometric projection turns the circle into the correct
                    // screen-space ellipse; overlapping impulses superpose.
                    float rippleWave = 0.0;
                    for (int rippleIndex = 0; rippleIndex < 8; rippleIndex++) {
                        if (rippleIndex >= rippleCount) break;
                        float age = rippleAges[rippleIndex];
                        vec2 rippleDelta =
                            (uv - ripplePositions[rippleIndex]) * 8.0;
                        float distanceFromFoot = length(rippleDelta);
                        float radius = 0.035 + age * 0.18;
                        float fade = (1.0 - smoothstep(0.72, 1.35, age)) *
                                     smoothstep(0.0, 0.08, age);
                        float crest = 1.0 - smoothstep(
                            0.012, 0.030, abs(distanceFromFoot - radius));
                        float troughRadius = max(0.0, radius - 0.040);
                        float trough = 1.0 - smoothstep(
                            0.010, 0.025,
                            abs(distanceFromFoot - troughRadius));
                        rippleWave += (crest - trough * 0.62) * fade;
                    }
                    // Quantized crest/trough bands preserve the reference
                    // resolution's pixel-art character.
                    float rippleLight =
                        smoothstep(0.24, 0.48, rippleWave) * surfaceEffect;
                    float rippleShadow =
                        smoothstep(0.18, 0.38, -rippleWave) * surfaceEffect;
                    color.rgb *= 1.0 - rippleShadow * 0.035;
                    color.rgb += vec3(0.42, 0.70, 0.77) * rippleLight * 0.115;
                }
                color.a *= opacity;

            }
            """;
        var vs = Compile(ShaderType.VertexShader, vertex);
        var fs = Compile(ShaderType.FragmentShader, fragment);
        var program = GL.CreateProgram();
        GL.AttachShader(program, vs);
        GL.AttachShader(program, fs);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linked);
        if (linked == 0) throw new InvalidOperationException(GL.GetProgramInfoLog(program));
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);
        return program;
    }

    public static int CreateSpriteProgram()
    {
        int Compile(ShaderType type, string source)
        {
            var shader = GL.CreateShader(type); GL.ShaderSource(shader, source); GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out var ok);
            if (ok == 0) throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
            return shader;
        }
        var vs = Compile(ShaderType.VertexShader,
            "#version 330 core\nlayout(location=0) in vec2 p;layout(location=1) in vec2 u;" +
            "layout(location=2) in float vertexOpacity;out vec2 uv;out float alpha;" +
            "void main(){uv=u;alpha=vertexOpacity;gl_Position=vec4(p,0,1);}");
        var fs = Compile(ShaderType.FragmentShader,
            "#version 330 core\nin vec2 uv;in float alpha;out vec4 c;uniform sampler2D image;" +
            "uniform int recolorPlayer;uniform vec3 playerColor;" +
            "uniform float opacity;uniform float brightness;uniform float tintAmount;" +
            "uniform float grayscaleAmount;" +
            "uniform vec3 colorTint;uniform int outlineOnly;uniform int wading;" +
            "uniform vec3 outlineColor;" +
            "uniform int preserveDarkTint;" +
            "uniform int spriteOutline;uniform vec3 spriteOutlineColor;" +
            "uniform int pixelArtFilter;uniform vec2 pixelArtGrid;" +
            "uniform int sceneLighting;uniform float sceneDarkness;" +
            "uniform float sceneFogAmount;uniform vec2 sceneFogCenter;" +
            "uniform vec2 sceneFogRadius;" +
            "uniform int sceneUnderground;uniform int localLightCount;" +
            "uniform vec2 localLightUv[16];uniform vec2 localLightRadius[16];" +
            "uniform vec3 localLightColor[16];uniform float localLightIntensity[16];" +
            "uniform float waterlineUv;uniform vec2 texelSize;" +
            "vec4 spriteSample(vec2 at){" +
            "if(pixelArtFilter==0)return texture(image,at);" +
            "vec2 cell=(floor(at*pixelArtGrid)+vec2(0.5))/pixelArtGrid;" +
            "vec2 tap=vec2(0.24)/pixelArtGrid;" +
            "vec4 a=texture(image,cell+vec2(-tap.x,-tap.y));" +
            "vec4 b=texture(image,cell+vec2(tap.x,-tap.y));" +
            "vec4 d=texture(image,cell+vec2(-tap.x,tap.y));" +
            "vec4 e=texture(image,cell+tap);float coverage=a.a+b.a+d.a+e.a;" +
            "vec3 rgb=(a.rgb*a.a+b.rgb*b.a+d.rgb*d.a+e.rgb*e.a)/max(coverage,0.001);" +
            "return vec4(rgb,smoothstep(0.18,0.72,coverage*0.25));}" +
            "void main(){vec4 source=spriteSample(uv);" +
            "if(outlineOnly==1){float around=0.0;" +
            "around=max(around,texture(image,uv+vec2(texelSize.x,0)).a);" +
            "around=max(around,texture(image,uv-vec2(texelSize.x,0)).a);" +
            "around=max(around,texture(image,uv+vec2(0,texelSize.y)).a);" +
            "around=max(around,texture(image,uv-vec2(0,texelSize.y)).a);" +
            "float ring=around*(1.0-source.a);if(ring<0.05)discard;" +
            "c=vec4(outlineColor,ring*opacity*alpha);}" +
            "else{c=source;" +
            "if(spriteOutline==1&&source.a<0.05){float around=0.0;" +
            "around=max(around,texture(image,uv+vec2(texelSize.x,0)).a);" +
            "around=max(around,texture(image,uv-vec2(texelSize.x,0)).a);" +
            "around=max(around,texture(image,uv+vec2(0,texelSize.y)).a);" +
            "around=max(around,texture(image,uv-vec2(0,texelSize.y)).a);" +
            "around=max(around,texture(image,uv+texelSize).a);" +
            "around=max(around,texture(image,uv-texelSize).a);" +
            "around=max(around,texture(image,uv+vec2(texelSize.x,-texelSize.y)).a);" +
            "around=max(around,texture(image,uv+vec2(-texelSize.x,texelSize.y)).a);" +
            "if(around>=0.05){c=vec4(spriteOutlineColor,around*opacity*alpha);return;}}" +
            "if(recolorPlayer==1&&source.a>0.01){" +
            "float strongestOther=max(source.r,source.g);" +
            "bool authoredBlue=source.b>0.16&&source.b>strongestOther*1.22&&" +
            "(source.b-strongestOther)>0.055;" +
            "if(authoredBlue){float shade=dot(source.rgb,vec3(0.2126,0.7152,0.0722));" +
            "float targetLuma=max(dot(playerColor,vec3(0.2126,0.7152,0.0722)),0.001);" +
            "c.rgb=clamp(playerColor*(shade/targetLuma),0.0,1.0);}}" +
            "if(wading==1&&uv.y>=waterlineUv&&source.a>0.01){" +
            "float surface=1.0-smoothstep(waterlineUv,waterlineUv+0.035,uv.y);" +
            "c.rgb=mix(c.rgb,vec3(0.08,0.34,0.53),0.43);" +
            "c.rgb+=vec3(0.16,0.42,0.55)*surface*0.22;c.a*=0.68;}" +
            "c.rgb*=1.0+brightness;" +
            "float gray=dot(c.rgb,vec3(0.2126,0.7152,0.0722));" +
            "c.rgb=mix(c.rgb,vec3(gray),grayscaleAmount);" +
            "if(preserveDarkTint==1){" +
            "float shade=max(c.r,max(c.g,c.b));" +
            "float targetPeak=max(colorTint.r,max(colorTint.g,colorTint.b));" +
            "vec3 colorized=clamp(colorTint*(shade/targetPeak),0.0,1.0);" +
            "c.rgb=mix(vec3(shade),colorized,tintAmount);" +
            "}else{c.rgb=mix(c.rgb,colorTint,tintAmount);}" +
            "if(pixelArtFilter==1&&c.a>0.01){" +
            "float peak=max(c.r,max(c.g,c.b));" +
            "float band=floor(peak*6.0+0.5)/6.0;" +
            "c.rgb*=band/max(peak,0.001);}" +
            "if(sceneLighting==1){" +
            "float night=sceneDarkness*sceneDarkness;" +
            "vec3 ambient=sceneUnderground==1?vec3(0.16):" +
            "mix(vec3(1.0),vec3(0.40,0.46,0.66),night);" +
            "vec3 illumination=vec3(0.0);" +
            "for(int i=0;i<16;i++){if(i>=localLightCount)break;" +
            "vec2 delta=(uv-localLightUv[i])/max(localLightRadius[i],vec2(0.0001));" +
            "float light=clamp(1.0-length(delta),0.0,1.0);" +
            "light=light*light*(3.0-2.0*light);" +
            "illumination+=localLightColor[i]*light*localLightIntensity[i];}" +
            "c.rgb*=clamp(ambient+illumination,vec3(0.0),vec3(1.12));" +
            "}" +
            "if(sceneFogAmount>0.001){" +
            "vec2 fogDelta=(uv-sceneFogCenter)/max(sceneFogRadius,vec2(0.001));" +
            "float fogCoverage=smoothstep(0.68,1.0,length(fogDelta));" +
            "vec3 fogColor=vec3(0.035,0.038,0.035);" +
            "c.rgb=mix(c.rgb,fogColor,fogCoverage*sceneFogAmount);" +
            "}" +
            "c.a*=opacity*alpha;}}");
        var program = GL.CreateProgram(); GL.AttachShader(program, vs); GL.AttachShader(program, fs); GL.LinkProgram(program);
        GL.DeleteShader(vs); GL.DeleteShader(fs);
        return program;
    }

    public static int CreateModalBlurProgram()
    {
        int Compile(ShaderType type, string source)
        {
            var shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out var ok);
            if (ok == 0)
                throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
            return shader;
        }

        var vertex = Compile(
            ShaderType.VertexShader,
            "#version 330 core\n" +
            "layout(location=0) in vec2 p;layout(location=1) in vec2 u;" +
            "out vec2 uv;void main(){uv=u;gl_Position=vec4(p,0,1);}");
        var fragment = Compile(
            ShaderType.FragmentShader,
            "#version 330 core\n" +
            "in vec2 uv;out vec4 color;uniform sampler2D image;" +
            "uniform vec2 direction;" +
            "void main(){" +
            "vec3 sum=texture(image,uv).rgb*0.1633;" +
            "sum+=texture(image,uv+direction*1.5).rgb*0.1531;" +
            "sum+=texture(image,uv-direction*1.5).rgb*0.1531;" +
            "sum+=texture(image,uv+direction*3.5).rgb*0.12245;" +
            "sum+=texture(image,uv-direction*3.5).rgb*0.12245;" +
            "sum+=texture(image,uv+direction*5.5).rgb*0.0918;" +
            "sum+=texture(image,uv-direction*5.5).rgb*0.0918;" +
            "sum+=texture(image,uv+direction*7.5).rgb*0.05102;" +
            "sum+=texture(image,uv-direction*7.5).rgb*0.05102;" +
            "color=vec4(sum,1.0);}");
        var program = GL.CreateProgram();
        GL.AttachShader(program, vertex);
        GL.AttachShader(program, fragment);
        GL.LinkProgram(program);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        return program;
    }
}
