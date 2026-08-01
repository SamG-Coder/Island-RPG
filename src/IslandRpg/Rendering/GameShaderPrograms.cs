using OpenTK.Graphics.OpenGL4;

namespace IslandRpg.Rendering;

internal static class GameShaderPrograms
{
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
            void main() {
                vec2 flowA = vec2(time * 0.016, time * 0.005);
                vec2 flowB = vec2(-time * 0.009, time * 0.012);
                vec2 n1 = texture(waterNormals,
                    vec3(uv * vec2(8.0, 5.0) + flowA, 0.0)).xy * 2.0 - 1.0;
                vec2 n2 = texture(waterNormals,
                    vec3(uv * vec2(5.0, 8.0) + flowB, 1.0)).xy * 2.0 - 1.0;
                vec2 waves = n1 * 0.65 + n2 * 0.35;
                float swell = sin((uv.x * 19.0 + uv.y * 7.0) - time * 1.35) * 0.5 + 0.5;
                vec2 sampleUv = uv * vec2(7.0, 4.5) + waves * 0.045;
                vec3 ageWater = texture(terrain, vec3(sampleUv, 0.0)).rgb;
                float crest = smoothstep(0.64, 0.95,
                    length(waves) * 0.7 + swell * 0.38);
                vec3 night = ageWater * vec3(0.22, 0.29, 0.42);
                night += vec3(0.08, 0.16, 0.22) * crest;
                night += vec3(0.55, 0.66, 0.78) * lightning *
                    (0.35 + crest * 0.65);
                float rainCell = fract((uv.x * 1.7 + uv.y) * 86.0 - time * 1.9);
                float rainBand = step(0.965, rainCell) *
                    step(fract(uv.x * 173.0 + time * 0.37), 0.42);
                night += vec3(0.18, 0.24, 0.31) * rainBand * 0.24;
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
            "uniform int sceneLighting;uniform float sceneDarkness;" +
            "uniform float sceneFogAmount;uniform vec2 sceneFogCenter;" +
            "uniform vec2 sceneFogRadius;" +
            "uniform int sceneUnderground;uniform int localLightCount;" +
            "uniform vec2 localLightUv[16];uniform vec2 localLightRadius[16];" +
            "uniform vec3 localLightColor[16];uniform float localLightIntensity[16];" +
            "uniform float waterlineUv;uniform vec2 texelSize;" +
            "void main(){vec4 source=texture(image,uv);" +
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
