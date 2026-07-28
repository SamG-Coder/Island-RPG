const header = document.querySelector("[data-header]");
const menu = document.querySelector("[data-menu]");
const nav = document.querySelector("[data-nav]");

const updateHeader = () => header?.classList.toggle("scrolled", scrollY > 24);
updateHeader();
addEventListener("scroll", updateHeader, { passive: true });

menu?.addEventListener("click", () => {
  const open = !nav.classList.contains("open");
  nav.classList.toggle("open", open);
  menu.setAttribute("aria-expanded", String(open));
});

nav?.addEventListener("click", event => {
  if (event.target.closest("a")) {
    nav.classList.remove("open");
    menu?.setAttribute("aria-expanded", "false");
  }
});

const reveal = new IntersectionObserver(entries => {
  for (const entry of entries) {
    if (!entry.isIntersecting) continue;
    entry.target.classList.add("visible");
    reveal.unobserve(entry.target);
  }
}, { threshold: 0.12 });

document.querySelectorAll(".reveal").forEach(element => reveal.observe(element));

class ProceduralWorldRenderer {
  constructor(canvas) {
    this.canvas = canvas;
    this.gl = canvas.getContext("webgl", {
      alpha: false,
      antialias: false,
      depth: false,
      powerPreference: "high-performance"
    });
    if (!this.gl) return;

    this.reducedMotion = matchMedia("(prefers-reduced-motion: reduce)").matches;
    this.pointer = { x: 0, y: 0 };
    this.pointerTarget = { x: 0, y: 0 };
    this.scroll = scrollY;
    this.scrollTarget = scrollY;
    this.lastFrame = 0;
    this.frameRequest = 0;
    this.program = this.createProgram();
    if (!this.program) return;
    this.locations = {
      position: this.gl.getAttribLocation(this.program, "position"),
      resolution: this.gl.getUniformLocation(this.program, "resolution"),
      time: this.gl.getUniformLocation(this.program, "time"),
      scroll: this.gl.getUniformLocation(this.program, "scrollOffset"),
      pointer: this.gl.getUniformLocation(this.program, "pointerOffset")
    };
    this.createGeometry();

    this.resizeObserver = new ResizeObserver(() => this.resize());
    this.resizeObserver.observe(canvas);
    addEventListener("scroll", () => {
      this.scrollTarget = scrollY;
      if (this.reducedMotion) this.draw(performance.now());
    }, { passive: true });
    addEventListener("pointermove", event => {
      this.pointerTarget.x = event.clientX / innerWidth - .5;
      this.pointerTarget.y = event.clientY / innerHeight - .5;
    }, { passive: true });
    document.addEventListener("visibilitychange", () => {
      if (!document.hidden) this.animate(performance.now());
    });
    this.resize();
    this.animate(performance.now());
  }

  createProgram() {
    const vertex = `
      attribute vec2 position;
      void main() {
        gl_Position = vec4(position, 0.0, 1.0);
      }
    `;
    const fragment = `
      precision highp float;
      uniform vec2 resolution;
      uniform float time;
      uniform float scrollOffset;
      uniform vec2 pointerOffset;

      float hash21(vec2 p) {
        p = fract(p * vec2(123.34, 456.21));
        p += dot(p, p + 45.32);
        return fract(p.x * p.y);
      }

      vec2 hash22(vec2 p) {
        float n = hash21(p);
        return vec2(n, hash21(p + n + 19.19));
      }

      float valueNoise(vec2 p) {
        vec2 cell = floor(p);
        vec2 f = fract(p);
        f = f * f * (3.0 - 2.0 * f);
        return mix(
          mix(hash21(cell), hash21(cell + vec2(1.0, 0.0)), f.x),
          mix(hash21(cell + vec2(0.0, 1.0)), hash21(cell + vec2(1.0)), f.x),
          f.y
        );
      }

      float fbm(vec2 p) {
        float value = 0.0;
        float amplitude = .52;
        mat2 turn = mat2(.80, -.60, .60, .80);
        for (int octave = 0; octave < 5; octave++) {
          value += valueNoise(p) * amplitude;
          p = turn * p * 2.03 + 13.7;
          amplitude *= .49;
        }
        return value;
      }

      float terrainField(vec2 p) {
        float broad = fbm(p * .31);
        float coastWarp = sin(p.x * .37 + sin(p.y * .24)) * .055;
        return p.y * .28 - p.x * .105 + broad * .82 + coastWarp - .46;
      }

      float circle(vec2 p, float radius) {
        return 1.0 - smoothstep(radius * .76, radius, length(p));
      }

      vec3 terrainColor(vec2 p, float field, float fine, float shade) {
        float land = smoothstep(-.035, .055, field);
        float beach = smoothstep(-.07, .015, field) *
                      (1.0 - smoothstep(.02, .13, field));
        float wetGrass = fbm(p * .55 + 21.0);
        float forest = smoothstep(.46, .72, wetGrass) *
                       smoothstep(.08, .25, field);
        float dryPatch = smoothstep(.55, .80,
          fbm(p * .82 + vec2(-17.0, 8.0)));

        vec3 deepWater = vec3(.025, .245, .390);
        vec3 shelfWater = vec3(.035, .375, .535);
        float shelf = smoothstep(-.32, -.02, field);
        vec3 water = mix(deepWater, shelfWater, shelf);

        vec3 sand = mix(
          vec3(.62, .51, .31),
          vec3(.80, .70, .47),
          fine * .58);
        vec3 grass = mix(
          vec3(.28, .43, .19),
          vec3(.45, .56, .25),
          fine * .46);
        grass = mix(grass, vec3(.47, .42, .22), dryPatch * .42);
        vec3 forestFloor = mix(
          vec3(.19, .29, .13),
          vec3(.33, .35, .16),
          fine);
        vec3 ground = mix(grass, forestFloor, forest * .72);
        ground = mix(ground, sand, beach);
        float micro = hash21(floor(p * 310.0)) - .5;
        float fibres = sin((p.x + p.y) * 155.0) *
                       sin((p.x - p.y) * 117.0);
        ground += vec3(micro * .055 + fibres * .012);
        ground *= shade;
        return mix(water, ground, land);
      }

      vec3 addVegetation(vec3 color, vec2 p, float field) {
        float forestSuitability = smoothstep(.12, .34, field) *
          smoothstep(.42, .67, fbm(p * .55 + 21.0));
        vec2 scaled = p * 4.7;
        vec2 baseCell = floor(scaled);
        vec2 local = fract(scaled) - .5;

        for (int y = -1; y <= 1; y++) {
          for (int x = -1; x <= 1; x++) {
            vec2 offset = vec2(float(x), float(y));
            vec2 cell = baseCell + offset;
            vec2 random = hash22(cell + 2187.0);
            vec2 center = offset + random * .72 - .36;
            vec2 delta = local - center;
            float exists = step(.34, random.x) * forestSuitability;
            float size = mix(.15, .27, random.y);

            vec2 shadowDelta = delta + vec2(.13, -.085);
            float shadow = circle(
              vec2(shadowDelta.x, shadowDelta.y * 1.8),
              size * 1.25) * exists;
            color *= 1.0 - shadow * .30;

            float trunk = step(abs(delta.x), size * .12) *
              step(-size * .68, delta.y) * step(delta.y, size * .28) * exists;
            color = mix(color, vec3(.22, .145, .07), trunk * .82);

            float crownA = circle(
              vec2(delta.x, (delta.y + size * .22) * 1.12), size) * exists;
            float crownB = circle(
              vec2(delta.x + size * .58, (delta.y - size * .02) * 1.2),
              size * .72) * exists;
            float crownC = circle(
              vec2(delta.x - size * .58, (delta.y - size * .02) * 1.2),
              size * .72) * exists;
            float crown = max(crownA, max(crownB, crownC));
            vec3 darkLeaf = vec3(.045, .27, .045);
            vec3 lightLeaf = vec3(.18, .49, .075);
            vec3 autumnLeaf = vec3(.48, .31, .045);
            vec3 leaves = mix(darkLeaf, lightLeaf,
              smoothstep(-size, size, -delta.y));
            leaves = mix(leaves, autumnLeaf,
              step(.80, hash21(cell + 77.0)) * .55);
            color = mix(color, leaves, crown * .94);
          }
        }
        return color;
      }

      void main() {
        vec2 uv = gl_FragCoord.xy / resolution;
        vec2 centered = uv - .5;
        centered.x *= resolution.x / resolution.y;

        float camera = scrollOffset * .00115;
        vec2 p = centered * 4.45;
        p += vec2(camera * .25, camera * .62);
        p += pointerOffset * vec2(.16, -.10);

        float field = terrainField(p);
        float fine = fbm(p * 3.8 + 8.0);

        float epsilon = .012;
        float heightCenter = fbm(p * .72);
        float heightX = fbm((p + vec2(epsilon, 0.0)) * .72);
        float heightY = fbm((p + vec2(0.0, epsilon)) * .72);
        vec3 normal = normalize(vec3(
          (heightCenter - heightX) * 5.2,
          (heightCenter - heightY) * 5.2,
          1.0));
        vec3 sunlight = normalize(vec3(-.42, .52, .74));
        float shade = clamp(.82 + dot(normal, sunlight) * .25, .70, 1.13);

        vec3 color = terrainColor(p, field, fine, shade);
        float waterCoverage = 1.0 - smoothstep(-.035, .055, field);
        if (waterCoverage > .001) {
          vec2 flowA = vec2(time * .035, time * .017);
          vec2 flowB = vec2(-time * .019, time * .027);
          float waveA = fbm(p * 4.3 + flowA);
          float waveB = fbm(p * 8.2 + flowB);
          float waves = waveA * .64 + waveB * .36;
          float rippleLines =
            sin((p.x * .82 + p.y) * 48.0 + time * 1.7) *
            sin((p.x - p.y * .63) * 31.0 - time * 1.1);
          float sparkle = pow(max(0.0, waves - .54) * 2.05, 5.0);
          color += vec3(.20, .48, .59) * (waves - .42) * .20 * waterCoverage;
          color += vec3(.62, .83, .86) * sparkle * .22 * waterCoverage;
          color += vec3(.12, .31, .38) * rippleLines * .035 * waterCoverage;

          float shore = (1.0 - smoothstep(.0, .095, abs(field))) *
            step(field, 0.0);
          float foamBreakup = smoothstep(.48, .70,
            valueNoise(p * 9.0 + vec2(time * .08, 0.0)));
          color = mix(color, vec3(.66, .78, .72),
            shore * foamBreakup * .19);
        }

        color = addVegetation(color, p, field);

        float grassSpeck = step(.82, hash21(floor(p * 95.0))) *
          smoothstep(.08, .18, field);
        color += vec3(.08, .16, .025) * grassSpeck * .24;

        float vignette = 1.0 - smoothstep(.55, 1.18, length(centered));
        color *= mix(.72, 1.0, vignette);
        float grain = hash21(gl_FragCoord.xy + floor(time * 8.0)) - .5;
        color += grain * .018;
        color = pow(max(color, 0.0), vec3(.92));
        gl_FragColor = vec4(color, 1.0);
      }
    `;
    const vertexShader = this.compile(this.gl.VERTEX_SHADER, vertex);
    const fragmentShader = this.compile(this.gl.FRAGMENT_SHADER, fragment);
    if (!vertexShader || !fragmentShader) return null;
    const program = this.gl.createProgram();
    this.gl.attachShader(program, vertexShader);
    this.gl.attachShader(program, fragmentShader);
    this.gl.linkProgram(program);
    if (!this.gl.getProgramParameter(program, this.gl.LINK_STATUS)) {
      console.warn("Procedural world shader failed:", this.gl.getProgramInfoLog(program));
      return null;
    }
    this.gl.deleteShader(vertexShader);
    this.gl.deleteShader(fragmentShader);
    return program;
  }

  compile(type, source) {
    const shader = this.gl.createShader(type);
    this.gl.shaderSource(shader, source);
    this.gl.compileShader(shader);
    if (!this.gl.getShaderParameter(shader, this.gl.COMPILE_STATUS)) {
      console.warn("Procedural world shader failed:", this.gl.getShaderInfoLog(shader));
      this.gl.deleteShader(shader);
      return null;
    }
    return shader;
  }

  createGeometry() {
    const buffer = this.gl.createBuffer();
    this.gl.bindBuffer(this.gl.ARRAY_BUFFER, buffer);
    this.gl.bufferData(
      this.gl.ARRAY_BUFFER,
      new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]),
      this.gl.STATIC_DRAW);
    this.gl.useProgram(this.program);
    this.gl.enableVertexAttribArray(this.locations.position);
    this.gl.vertexAttribPointer(
      this.locations.position, 2, this.gl.FLOAT, false, 0, 0);
  }

  resize() {
    const ratio = Math.min(devicePixelRatio || 1, 1.25);
    const width = Math.max(1, Math.round(innerWidth * ratio));
    const height = Math.max(1, Math.round(innerHeight * ratio));
    if (this.canvas.width !== width || this.canvas.height !== height) {
      this.canvas.width = width;
      this.canvas.height = height;
      this.gl.viewport(0, 0, width, height);
    }
    this.draw(performance.now());
  }

  animate(now) {
    cancelAnimationFrame(this.frameRequest);
    if (document.hidden || !this.program) return;
    if (!this.reducedMotion && now - this.lastFrame >= 33) {
      this.scroll += (this.scrollTarget - this.scroll) * .075;
      this.pointer.x += (this.pointerTarget.x - this.pointer.x) * .045;
      this.pointer.y += (this.pointerTarget.y - this.pointer.y) * .045;
      this.draw(now);
      this.lastFrame = now;
    }
    this.frameRequest = requestAnimationFrame(time => this.animate(time));
  }

  draw(now) {
    if (!this.program) return;
    this.gl.useProgram(this.program);
    this.gl.uniform2f(
      this.locations.resolution, this.canvas.width, this.canvas.height);
    this.gl.uniform1f(this.locations.time, now * .001);
    this.gl.uniform1f(this.locations.scroll, this.reducedMotion ? scrollY : this.scroll);
    this.gl.uniform2f(
      this.locations.pointer,
      this.reducedMotion ? 0 : this.pointer.x,
      this.reducedMotion ? 0 : this.pointer.y);
    this.gl.drawArrays(this.gl.TRIANGLE_STRIP, 0, 4);
  }
}

const worldCanvas = document.querySelector("[data-world-background]");
if (worldCanvas) new ProceduralWorldRenderer(worldCanvas);
