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

class ProceduralIsland {
  constructor(canvas, seed = 2187) {
    this.canvas = canvas;
    this.context = canvas.getContext("2d", { alpha: true });
    this.seed = seed;
    this.size = 46;
    this.staticLayer = document.createElement("canvas");
    this.waterHighlights = [];
    this.frameRequest = 0;
    this.lastFrame = 0;
    this.visible = true;

    this.resizeObserver = new ResizeObserver(() => this.queueBuild());
    this.resizeObserver.observe(canvas);
    this.visibilityObserver = new IntersectionObserver(entries => {
      this.visible = entries[0]?.isIntersecting ?? false;
      if (this.visible) this.animate(performance.now());
    });
    this.visibilityObserver.observe(canvas);
    document.addEventListener("visibilitychange", () => {
      if (!document.hidden && this.visible) this.animate(performance.now());
    });
    this.queueBuild();
  }

  queueBuild() {
    cancelAnimationFrame(this.buildRequest);
    this.buildRequest = requestAnimationFrame(() => this.build());
  }

  build() {
    const bounds = this.canvas.getBoundingClientRect();
    if (bounds.width < 1 || bounds.height < 1) return;
    const ratio = Math.min(devicePixelRatio || 1, 1.5);
    const width = Math.round(bounds.width * ratio);
    const height = Math.round(bounds.height * ratio);
    this.canvas.width = width;
    this.canvas.height = height;
    this.staticLayer.width = width;
    this.staticLayer.height = height;

    const context = this.staticLayer.getContext("2d");
    context.clearRect(0, 0, width, height);
    context.save();
    context.scale(ratio, ratio);
    this.generate(context, bounds.width, bounds.height);
    context.restore();
    this.animate(performance.now());
  }

  generate(context, width, height) {
    const size = this.size;
    const heights = Array.from({ length: size + 1 }, () => new Uint8Array(size + 1));
    for (let y = 0; y <= size; y++) {
      for (let x = 0; x <= size; x++) {
        const nx = (x - size / 2) / (size * .43);
        const ny = (y - size / 2) / (size * .39);
        const warp = Math.sin(x * .17 + this.seed) * .055
          + Math.sin(y * .113) * .045
          + Math.sin((x + y) * .071) * .04;
        const land = 1 - Math.sqrt(nx * nx + ny * ny) + warp;
        heights[y][x] = Math.max(0, Math.min(6, Math.floor((land - .06) * 8)));
      }
    }

    const tileWidth = Math.min(width / 31, height / 25);
    const tileHeight = tileWidth * .5;
    const elevation = tileHeight * .62;
    const centerX = width * .59;
    const centerY = height * .48 - size * tileHeight * .48;
    const tiles = [];
    const trees = [];
    this.waterHighlights = [];

    for (let y = 0; y < size; y++) {
      for (let x = 0; x < size; x++) {
        const corners = [
          heights[y][x], heights[y][x + 1],
          heights[y + 1][x + 1], heights[y + 1][x]
        ];
        const average = corners.reduce((sum, value) => sum + value, 0) / 4;
        const moisture = (
          Math.sin(x * .29) + Math.cos(y * .23) + Math.sin((x - y) * .11)
        ) / 3;
        const biome = average < .62 ? "deep"
          : average < .85 ? "shallow"
          : average < 1.45 ? "beach"
          : average > 4.8 ? "rock"
          : average > 3.6 ? "highland"
          : moisture > -.05 ? "forest"
          : "grass";
        tiles.push({ x, y, corners, average, biome });

        const treeChance = biome === "forest" ? .16
          : biome === "highland" ? .045
          : biome === "beach" ? .012
          : 0;
        if (this.random(x, y, 19) < treeChance) {
          trees.push({ x: x + .5, y: y + .5, height: average, palm: biome === "beach" });
        }
        if ((biome === "deep" || biome === "shallow") && this.random(x, y, 31) < .018) {
          this.waterHighlights.push({ x, y, phase: this.random(x, y, 37) * Math.PI * 2 });
        }
      }
    }

    const project = (x, y, z = 0) => ({
      x: centerX + (x - y) * tileWidth / 2,
      y: centerY + (x + y) * tileHeight / 2 - z * elevation
    });

    for (const tile of tiles) {
      const points = [
        project(tile.x, tile.y, tile.corners[0]),
        project(tile.x + 1, tile.y, tile.corners[1]),
        project(tile.x + 1, tile.y + 1, tile.corners[2]),
        project(tile.x, tile.y + 1, tile.corners[3])
      ];
      const shade = .9 + (tile.corners[0] - tile.corners[2]) * .035;
      this.polygon(context, points, this.color(tile.biome, shade, tile.x, tile.y));
    }

    trees.sort((a, b) => (a.x + a.y) - (b.x + b.y));
    for (const tree of trees) this.drawTree(context, project(tree.x, tree.y, tree.height), tree.palm, tileWidth);

    this.project = project;
    this.tileWidth = tileWidth;
  }

  polygon(context, points, fill) {
    context.beginPath();
    context.moveTo(points[0].x, points[0].y);
    for (let index = 1; index < points.length; index++) context.lineTo(points[index].x, points[index].y);
    context.closePath();
    context.fillStyle = fill;
    context.fill();
  }

  color(biome, shade, x, y) {
    const colors = {
      deep: [20, 52, 58],
      shallow: [39, 82, 78],
      beach: [151, 133, 82],
      grass: [76, 103, 58],
      forest: [48, 78, 43],
      highland: [88, 91, 58],
      rock: [91, 86, 69]
    };
    const base = colors[biome];
    const grain = (this.random(x, y, 53) - .5) * 9;
    return `rgb(${base.map(channel => Math.round(channel * shade + grain)).join(",")})`;
  }

  drawTree(context, point, palm, scale) {
    const height = scale * (palm ? 1.35 : 1.15);
    context.strokeStyle = palm ? "#78613b" : "#55472e";
    context.lineWidth = Math.max(1, scale * .1);
    context.beginPath();
    context.moveTo(point.x, point.y);
    context.lineTo(point.x + (palm ? scale * .13 : 0), point.y - height * .68);
    context.stroke();

    const crownX = point.x + (palm ? scale * .13 : 0);
    const crownY = point.y - height * .72;
    context.fillStyle = palm ? "#507242" : "#304d2d";
    if (palm) {
      for (let branch = 0; branch < 5; branch++) {
        const angle = branch / 5 * Math.PI * 2;
        context.beginPath();
        context.ellipse(
          crownX + Math.cos(angle) * scale * .24,
          crownY + Math.sin(angle) * scale * .11,
          scale * .3, scale * .09, angle, 0, Math.PI * 2);
        context.fill();
      }
    } else {
      context.beginPath();
      context.arc(crownX, crownY, scale * .28, 0, Math.PI * 2);
      context.arc(crownX - scale * .2, crownY + scale * .08, scale * .22, 0, Math.PI * 2);
      context.arc(crownX + scale * .2, crownY + scale * .08, scale * .22, 0, Math.PI * 2);
      context.fill();
    }
  }

  random(x, y, salt) {
    let value = Math.imul(x + salt, 374761393) + Math.imul(y - salt, 668265263);
    value = Math.imul(value ^ (value >>> 13) ^ this.seed, 1274126177);
    return ((value ^ (value >>> 16)) >>> 0) / 4294967296;
  }

  animate(time) {
    cancelAnimationFrame(this.frameRequest);
    if (!this.visible || document.hidden || !this.canvas.width) return;
    if (time - this.lastFrame < 33) {
      this.frameRequest = requestAnimationFrame(next => this.animate(next));
      return;
    }
    this.lastFrame = time;
    const context = this.context;
    context.clearRect(0, 0, this.canvas.width, this.canvas.height);
    context.drawImage(this.staticLayer, 0, 0);

    const ratio = this.canvas.width / Math.max(1, this.canvas.clientWidth);
    context.save();
    context.scale(ratio, ratio);
    context.lineWidth = 1;
    for (const highlight of this.waterHighlights) {
      const point = this.project(highlight.x + .5, highlight.y + .5, 0);
      const pulse = .12 + (Math.sin(time * .0012 + highlight.phase) + 1) * .08;
      context.strokeStyle = `rgba(151, 193, 182, ${pulse})`;
      context.beginPath();
      context.moveTo(point.x - this.tileWidth * .22, point.y);
      context.lineTo(point.x + this.tileWidth * .22, point.y);
      context.stroke();
    }
    context.restore();
    this.frameRequest = requestAnimationFrame(next => this.animate(next));
  }
}

const islandCanvas = document.querySelector("[data-island-demo]");
if (islandCanvas) new ProceduralIsland(islandCanvas);
