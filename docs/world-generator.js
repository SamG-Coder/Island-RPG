/*
 * Browser port of InfiniteWorldGenerator + the developer atlas presentation.
 * Keep constants and arithmetic aligned with the C# source. Both are covered
 * by this repository's MIT licence.
 */
(() => {
  "use strict";

  const f = Math.fround;
  const U64 = value => BigInt.asUintN(64, value);
  const I64 = value => BigInt.asIntN(64, value);
  const clamp = (value, minimum, maximum) =>
    Math.min(maximum, Math.max(minimum, value));
  const mix = (a, b, amount) => f(a + f(f(b - a) * amount));
  const smoothStep = (a, b, value) => {
    const t = f(clamp(f(f(value - a) / f(b - a)), 0, 1));
    return f(f(t * t) * f(3 - f(2 * t)));
  };
  const floorDiv = (value, divisor) => Math.floor(value / divisor);
  const nextTurn = () => new Promise(resolve =>
    requestAnimationFrame(() => resolve()));

  const Biome = Object.freeze({
    DeepWater: 0, ShallowWater: 1, RiverWater: 2, MangroveShallows: 3,
    Beach: 4, Grassland: 5, DryGrass: 6, Mud: 7, Forest: 8,
    JungleFloor: 9, Highland: 10, Rock: 11, Tundra: 12, Snow: 13,
    DesertSand: 14, CrackedEarth: 15
  });
  const Region = Object.freeze({
    Ocean: 0, Coast: 1, River: 2, Wetland: 3, TemperateGrassland: 4,
    TemperateForest: 5, Rainforest: 6, Savanna: 7, Desert: 8,
    Taiga: 9, Tundra: 10, Alpine: 11
  });

  class IslandWorldSampler {
    static ISLAND_CELL_SIZE = 192;
    static HYDROLOGY_CELL_SIZE = 8;
    static HYDROLOGY_REGION_CELLS = 64;
    static HYDROLOGY_REGION_SPAN = 512;
    static HYDROLOGY_HALO_CELLS = 24;
    static HYDROLOGY_GRID_SIZE = 112;
    static HYDROLOGY_BLEND_TILES = 96;

    constructor(seed) {
      this.seed = I64(seed);
      this.hydrology = new Map();
      this.baseElevationCache = null;
      this.heightCache = null;
    }

    unitHash(seed, x, y, salt) {
      let value = U64(seed) ^
        U64(BigInt(x) * 0x9e3779b185ebca87n) ^
        U64(BigInt(y) * 0xc2b2ae3d27d4eb4fn) ^
        BigInt(salt >>> 0);
      value = U64(value ^ (value >> 30n));
      value = U64(value * 0xbf58476d1ce4e5b9n);
      value = U64(value ^ (value >> 27n));
      value = U64(value * 0x94d049bb133111ebn);
      value = U64(value ^ (value >> 31n));
      return f(Number(value >> 40n) / 16777216);
    }

    valueNoise(seed, x, y) {
      const x0 = Math.floor(x);
      const y0 = Math.floor(y);
      let fx = f(x - x0);
      let fy = f(y - y0);
      fx = f(f(fx * fx) * f(3 - f(2 * fx)));
      fy = f(f(fy * fy) * f(3 - f(2 * fy)));
      const a = this.unitHash(seed, x0, y0, 0);
      const b = this.unitHash(seed, x0 + 1, y0, 0);
      const c = this.unitHash(seed, x0, y0 + 1, 0);
      const d = this.unitHash(seed, x0 + 1, y0 + 1, 0);
      return mix(mix(a, b, fx), mix(c, d, fx), fy);
    }

    fractalNoise(seed, x, y, octaves) {
      let value = f(0);
      let amplitude = f(1);
      let total = f(0);
      x = f(x);
      y = f(y);
      for (let octave = 0; octave < octaves; octave++) {
        value = f(value + f(this.valueNoise(
          I64(seed + BigInt(octave * 1013)), x, y) * amplitude));
        total = f(total + amplitude);
        amplitude = f(amplitude * .5);
        x = f(x * 2.03);
        y = f(y * 2.03);
      }
      return f(f(value / total) * 2 - 1);
    }

    mountainProfile(x, y) {
      const seed = this.seed;
      const warpedX = f(x + f(this.fractalNoise(
        I64(seed ^ 0x3c6ef372fe94f82bn), f(x / 310), f(y / 310), 3) * 42));
      const warpedY = f(y + f(this.fractalNoise(
        I64(seed ^ 0x428a2f98d728ae22n), f(x / 310), f(y / 310), 3) * 42));
      const cellX = floorDiv(x, 768);
      const cellY = floorDiv(y, 768);
      let ramp = f(0);
      let core = f(0);
      for (let cy = cellY - 1; cy <= cellY + 1; cy++) {
        for (let cx = cellX - 1; cx <= cellX + 1; cx++) {
          const centerX = f(f(cx + .5 +
            f(f(this.unitHash(seed, cx, cy, 401) - .5) * .34)) * 768);
          const centerY = f(f(cy + .5 +
            f(f(this.unitHash(seed, cx, cy, 409) - .5) * .34)) * 768);
          const angle = f(this.unitHash(seed, cx, cy, 419) * Math.PI);
          const halfLength = f(300 + this.unitHash(seed, cx, cy, 421) * 250);
          const halfWidth = f(125 + this.unitHash(seed, cx, cy, 431) * 105);
          const axisX = f(Math.cos(angle));
          const axisY = f(Math.sin(angle));
          const relativeX = f(warpedX - centerX);
          const relativeY = f(warpedY - centerY);
          const along = f(clamp(
            f(f(relativeX * axisX) + f(relativeY * axisY)),
            -halfLength, halfLength));
          const nearestX = f(centerX + f(axisX * along));
          const nearestY = f(centerY + f(axisY * along));
          const dx = f(warpedX - nearestX);
          const dy = f(warpedY - nearestY);
          const normalized = f(f(Math.sqrt(f(f(dx * dx) + f(dy * dy)))) /
            halfWidth);
          ramp = Math.max(ramp, f(1 - smoothStep(.15, 1, normalized)));
          core = Math.max(core, f(1 - smoothStep(.05, .34, normalized)));
        }
      }
      return [f(ramp), f(core)];
    }

    baseElevation(x, y) {
      const cacheKey = `${x},${y}`;
      const cached = this.baseElevationCache?.get(cacheKey);
      if (cached !== undefined) return cached;
      const seed = this.seed;
      const continental = this.fractalNoise(
        I64(seed ^ 0x6a09e667f3bcc909n), f(x / 720), f(y / 720), 4);
      const continentalDetail = this.fractalNoise(
        I64(seed ^ 0xbb67ae8584caa73bn), f(x / 280), f(y / 280), 3);
      const continentHeight = f(f(continental +
        f(continentalDetail * .22) + .12) * 5.4);
      const cellX = floorDiv(x, IslandWorldSampler.ISLAND_CELL_SIZE);
      const cellY = floorDiv(y, IslandWorldSampler.ISLAND_CELL_SIZE);
      let island = f(-1);
      for (let cy = cellY - 1; cy <= cellY + 1; cy++) {
        for (let cx = cellX - 1; cx <= cellX + 1; cx++) {
          const centerX = f(f(cx + .18 +
            f(this.unitHash(seed, cx, cy, 11) * .64)) * 192);
          const centerY = f(f(cy + .18 +
            f(this.unitHash(seed, cx, cy, 17) * .64)) * 192);
          const radiusX = f(192 * f(.25 +
            f(this.unitHash(seed, cx, cy, 23) * .20)));
          const radiusY = f(192 * f(.23 +
            f(this.unitHash(seed, cx, cy, 29) * .19)));
          const dx = f(f(x - centerX) / radiusX);
          const dy = f(f(y - centerY) / radiusY);
          const distance = f(Math.sqrt(f(f(dx * dx) + f(dy * dy))));
          const warp = f(this.fractalNoise(
            I64(seed ^ 0x243f6a8885a308d3n),
            f(x / 48), f(y / 48), 3) * .28);
          island = Math.max(island, f(1 - distance + warp));
        }
      }
      const islandHeight = f(f(island - .08) * 7.2);
      const [rangeRamp, mountainCore] = this.mountainProfile(x, y);
      const mountainGate = f(clamp(f(f(continental + .15) * 1.7), 0, 1));
      const passNoise = this.fractalNoise(
        I64(seed ^ 0x428a2f98d728ae22n), f(x / 115), f(y / 115), 2);
      const passCut = f(clamp(f(f(passNoise - .42) * 2.3), 0, .72));
      const mountains = f(f(f(f(mountainCore * mountainGate) * 12.5) *
        f(1 - passCut)));
      const foothills = f(f(f(f(rangeRamp * mountainGate) * 6) *
        f(1 - f(passCut * .55))));
      const hillNoise = Math.max(0, this.fractalNoise(
        I64(seed ^ 0x7137449123ef65cdn), f(x / 92), f(y / 92), 3));
      const hills = f(f(f(f(hillNoise * hillNoise) *
        clamp(f(f(continental + .3) * 1.25), 0, 1)) * 2.6));
      const detail = f(this.fractalNoise(
        I64(seed ^ 0x13198a2e03707344n), f(x / 22), f(y / 22), 3) * .8);
      const result = f(Math.max(continentHeight, islandHeight) +
        mountains + foothills + hills + detail);
      this.baseElevationCache?.set(cacheKey, result);
      return result;
    }

    rainfall(x, y) {
      const seed = this.seed;
      const broad = this.fractalNoise(
        I64(seed ^ 0x5deece66dn), f(x / 430), f(y / 430), 4);
      const detail = this.fractalNoise(
        I64(seed ^ 0xa54ff53a5f1d36f1n), f(x / 105), f(y / 105), 2);
      const windAngle = f(this.unitHash(seed, 0, 0, 557) * Math.PI * 2);
      const windX = f(Math.cos(windAngle));
      const windY = f(Math.sin(windAngle));
      const localElevation = this.baseElevation(x, y);
      const upwindNear = this.baseElevation(
        Math.trunc(x - windX * 72), Math.trunc(y - windY * 72));
      const upwindFar = this.baseElevation(
        Math.trunc(x - windX * 152), Math.trunc(y - windY * 152));
      const barrier = f(Math.max(upwindNear, upwindFar) - localElevation);
      const rainShadow = f(clamp(f(barrier * .045), 0, .48));
      const oceanMoisture = upwindFar < .5 ? .16 : 0;
      return f(clamp(f(.65 + f(broad * .28) + f(detail * .12) +
        oceanMoisture - rainShadow), .10, 1.2));
    }

    async prepareHydrology(minX, minY, maxX, maxY, onProgress, cancelled) {
      const firstX = floorDiv(Math.floor(minX - 96), 512);
      const lastX = floorDiv(Math.floor(maxX + 96), 512);
      const firstY = floorDiv(Math.floor(minY - 96), 512);
      const lastY = floorDiv(Math.floor(maxY + 96), 512);
      const missing = [];
      for (let y = firstY; y <= lastY; y++) {
        for (let x = firstX; x <= lastX; x++) {
          if (!this.hydrology.has(`${x},${y}`)) missing.push([x, y]);
        }
      }
      for (let index = 0; index < missing.length; index++) {
        if (cancelled()) throw new DOMException("Generation cancelled", "AbortError");
        const [x, y] = missing[index];
        this.hydrology.set(
          `${x},${y}`,
          await this.generateHydrologyRegion(x, y, cancelled));
        onProgress?.(index + 1, missing.length);
      }
      this.baseElevationCache = new Map();
      this.heightCache = new Map();
    }

    async generateHydrologyRegion(regionX, regionY, cancelled) {
      const grid = 112;
      const count = grid * grid;
      const originX = regionX * 512 - 24 * 8;
      const originY = regionY * 512 - 24 * 8;
      const original = new Float32Array(count);
      const filled = new Float32Array(count);
      const lake = new Float32Array(count);
      const receiver = new Int32Array(count);
      const accumulation = new Float32Array(count);
      receiver.fill(-1);
      for (let y = 0; y < grid; y++) {
        for (let x = 0; x < grid; x++) {
          const worldX = originX + x * 8;
          const worldY = originY + y * 8;
          const index = y * grid + x;
          original[index] = this.baseElevation(worldX, worldY);
          filled[index] = original[index];
          accumulation[index] = this.rainfall(worldX, worldY);
        }
        if ((y & 7) === 7) {
          if (cancelled()) throw new DOMException(
            "Generation cancelled", "AbortError");
          await nextTurn();
        }
      }
      for (let pass = 0; pass < 20; pass++) {
        for (let y = 1; y < grid - 1; y++) {
          for (let x = 1; x < grid - 1; x++) {
            const index = y * grid + x;
            let lowest = Number.MAX_VALUE;
            for (let oy = -1; oy <= 1; oy++) {
              for (let ox = -1; ox <= 1; ox++) {
                if (ox || oy) lowest = Math.min(
                  lowest, filled[(y + oy) * grid + x + ox]);
              }
            }
            if (filled[index] < lowest) {
              filled[index] = Math.min(f(lowest + .002), f(original[index] + 3));
            }
          }
        }
        if ((pass & 1) === 1) {
          if (cancelled()) throw new DOMException(
            "Generation cancelled", "AbortError");
          await nextTurn();
        }
      }
      for (let y = 1; y < grid - 1; y++) {
        for (let x = 1; x < grid - 1; x++) {
          const index = y * grid + x;
          let best = filled[index];
          let bestIndex = -1;
          for (let oy = -1; oy <= 1; oy++) {
            for (let ox = -1; ox <= 1; ox++) {
              if (!ox && !oy) continue;
              const candidate = (y + oy) * grid + x + ox;
              const penalty = ox && oy ? .0002 : 0;
              if (filled[candidate] + penalty >= best) continue;
              best = filled[candidate] + penalty;
              bestIndex = candidate;
            }
          }
          receiver[index] = bestIndex;
          lake[index] = clamp(f(f(filled[index] - original[index]) / 1.2), 0, 1);
        }
      }
      const order = Array.from({ length: count }, (_, index) => index);
      order.sort((a, b) => filled[b] - filled[a] || a - b);
      for (const index of order) {
        const target = receiver[index];
        if (target >= 0) accumulation[target] = f(
          accumulation[target] + accumulation[index]);
      }
      const river = new Float32Array(count);
      for (let index = 0; index < count; index++) {
        if (original[index] < .55) continue;
        const flow = f(Math.log2(f(1 + accumulation[index])));
        river[index] = smoothStep(2.7, 6.4, flow);
        lake[index] = f(lake[index] * smoothStep(.8, 2.2, accumulation[index]));
      }
      await nextTurn();
      return { originX, originY, river, lake, flow: accumulation };
    }

    regionSample(region, worldX, worldY) {
      const grid = 112;
      const x = clamp((worldX - region.originX) / 8, 0, grid - 1.001);
      const y = clamp((worldY - region.originY) / 8, 0, grid - 1.001);
      const x0 = Math.floor(x), y0 = Math.floor(y);
      const x1 = Math.min(x0 + 1, grid - 1);
      const y1 = Math.min(y0 + 1, grid - 1);
      const tx = f(x - x0), ty = f(y - y0);
      const sample = values => mix(
        mix(values[y0 * grid + x0], values[y0 * grid + x1], tx),
        mix(values[y1 * grid + x0], values[y1 * grid + x1], tx), ty);
      return [sample(region.river), sample(region.lake), sample(region.flow)];
    }

    hydrologyAt(worldX, worldY) {
      const regionX = floorDiv(Math.floor(worldX), 512);
      const regionY = floorDiv(Math.floor(worldY), 512);
      const localX = worldX - regionX * 512;
      const localY = worldY - regionY * 512;
      const xNeighbor = localX < 96 ? -1 : localX > 416 ? 1 : 0;
      const yNeighbor = localY < 96 ? -1 : localY > 416 ? 1 : 0;
      const xBlend = xNeighbor < 0 ? 1 - localX / 96 :
        xNeighbor > 0 ? (localX - 416) / 96 : 0;
      const yBlend = yNeighbor < 0 ? 1 - localY / 96 :
        yNeighbor > 0 ? (localY - 416) / 96 : 0;
      const get = (x, y) => this.regionSample(
        this.hydrology.get(`${x},${y}`), worldX, worldY);
      const lerpSample = (a, b, amount) => [
        mix(a[0], b[0], amount), mix(a[1], b[1], amount),
        mix(a[2], b[2], amount)
      ];
      const center = get(regionX, regionY);
      if (!xNeighbor && !yNeighbor) return center;
      const horizontal = !xNeighbor ? center : lerpSample(
        center, get(regionX + xNeighbor, regionY), xBlend);
      if (!yNeighbor) return horizontal;
      const vertical = lerpSample(
        center, get(regionX, regionY + yNeighbor), yBlend);
      if (!xNeighbor) return vertical;
      return lerpSample(horizontal, lerpSample(
        vertical, get(regionX + xNeighbor, regionY + yNeighbor), xBlend), yBlend);
    }

    heightAt(x, y) {
      const cacheKey = `${x},${y}`;
      const cached = this.heightCache?.get(cacheKey);
      if (cached !== undefined) return cached;
      let elevation = this.baseElevation(x, y);
      const [river, lake] = this.hydrologyAt(x, y);
      if (elevation > .35) {
        const channelCarve = f(river * Math.min(6.5, elevation - .25));
        const lakeCarve = f(lake * Math.min(3.2, elevation - .2));
        elevation = f(elevation - Math.max(channelCarve, lakeCarve));
      }
      const result = clamp(Math.floor(elevation), 0, 22);
      this.heightCache?.set(cacheKey, result);
      return result;
    }

    classify(x, y, elevation) {
      const seed = this.seed;
      const baseElevation = this.baseElevation(x, y);
      if (baseElevation < -.35) return [Biome.DeepWater, Region.Ocean];
      if (baseElevation < .9) return [Biome.ShallowWater, Region.Ocean];
      const [river, lake] = this.hydrologyAt(x, y);
      const continental = this.fractalNoise(
        I64(seed ^ 0x6a09e667f3bcc909n), f(x / 720), f(y / 720), 4);
      if (lake > .48 && elevation < 5.5) {
        const warm = Math.sin((y + Number(seed % 10000n)) / 1450) > -.05;
        const mangrove = baseElevation < 1.7 && warm &&
          this.rainfall(x, y) > .72;
        return [mangrove ? Biome.MangroveShallows : Biome.RiverWater,
          Region.Wetland];
      }
      if (river > .48 && continental > -.18) {
        return [Biome.RiverWater, Region.River];
      }
      if (elevation < 1.45) return [Biome.Beach, Region.Coast];
      const moisture = clamp(.5 +
        this.fractalNoise(I64(seed ^ 0x5deece66dn),
          f(x / 430), f(y / 430), 4) * .34 +
        this.fractalNoise(I64(seed ^ 0xa54ff53a5f1d36f1n),
          f(x / 105), f(y / 105), 2) * .16 + river * .24, 0, 1);
      const climateBand = Math.sin((y + Number(seed % 10000n)) / 1450);
      const temperature = clamp(.55 + climateBand * .24 +
        this.fractalNoise(I64(seed ^ 0x510e527fade682d1n),
          f(x / 610), f(y / 610), 3) * .22 -
        Math.max(0, elevation - 3) * .032, 0, 1);
      if (elevation > 13) return temperature < .43 && moisture > .34
        ? [Biome.Snow, Region.Alpine] : [Biome.Rock, Region.Alpine];
      if (elevation > 9) return temperature < .30 && moisture > .42
        ? [Biome.Snow, Region.Alpine] : [Biome.Rock, Region.Alpine];
      if (elevation > 6) return temperature < .24 && moisture > .48
        ? [Biome.Snow, Region.Alpine]
        : [Biome.Highland, Region.TemperateGrassland];
      if (temperature < .20) return [Biome.Tundra, Region.Tundra];
      if (temperature < .36) return moisture > .43
        ? [Biome.Forest, Region.Taiga] : [Biome.Tundra, Region.Tundra];
      if (moisture < .18 && temperature > .58) {
        return [Biome.CrackedEarth, Region.Desert];
      }
      if (moisture < .30 && temperature > .5) {
        return [Biome.DesertSand, Region.Desert];
      }
      if (moisture < .43 && temperature > .55) {
        return [Biome.DryGrass, Region.Savanna];
      }
      if (river > .24 && moisture > .62) return [Biome.Mud, Region.Wetland];
      if (moisture > .72 && temperature > .58) {
        return [Biome.JungleFloor, Region.Rainforest];
      }
      if (moisture > .53) return [Biome.Forest, Region.TemperateForest];
      return [Biome.Grassland, Region.TemperateGrassland];
    }

    sampleTile(x, y) {
      const raw = [
        this.heightAt(x, y), this.heightAt(x + 1, y),
        this.heightAt(x + 1, y + 1), this.heightAt(x, y + 1)
      ];
      const average = (raw[0] + raw[1] + raw[2] + raw[3]) / 4;
      const [biome, region] = this.classify(x, y, average);
      return {
        x, y, biome, region,
        heights: raw.map(height => height <= 2 ? 0 : height)
      };
    }
  }

  const PALETTE = {
    [Region.Ocean]: [34, 92, 138], [Region.Coast]: [218, 197, 137],
    [Region.River]: [46, 126, 174], [Region.Wetland]: [66, 112, 83],
    [Region.TemperateGrassland]: [111, 151, 77],
    [Region.TemperateForest]: [53, 109, 61],
    [Region.Rainforest]: [31, 91, 53], [Region.Savanna]: [166, 156, 76],
    [Region.Desert]: [205, 178, 104], [Region.Taiga]: [66, 104, 83],
    [Region.Tundra]: [145, 153, 139], [Region.Alpine]: [112, 108, 104]
  };
  const biomeColor = tile => {
    if (tile.biome === Biome.Snow) return [224, 232, 235];
    if (tile.biome === Biome.DeepWater) return [24, 72, 116];
    if (tile.biome === Biome.ShallowWater && tile.region === Region.Ocean) {
      return [43, 112, 151];
    }
    if (tile.biome === Biome.RiverWater) return [45, 125, 171];
    if (tile.biome === Biome.MangroveShallows) return [62, 119, 113];
    return PALETTE[tile.region] || [120, 120, 120];
  };
  const spawnChance = (region, elevation) => {
    const chance = {
      [Region.Rainforest]: .31, [Region.TemperateForest]: .23,
      [Region.Taiga]: .19, [Region.Wetland]: .13, [Region.Savanna]: .065,
      [Region.Alpine]: .045, [Region.Coast]: .012,
      [Region.Tundra]: .025, [Region.Desert]: .009
    }[region] || 0;
    return region === Region.Alpine
      ? chance * clamp((12 - elevation) / 4, 0, 1) : chance;
  };

  class WorldGenerationDemo {
    constructor(root) {
      this.root = root;
      this.canvas = root.querySelector("[data-world-map]");
      this.context = this.canvas.getContext("2d", { alpha: false });
      this.seedInput = root.querySelector("[data-world-seed]");
      this.status = root.querySelector("[data-world-status]");
      this.layer = "terrain";
      this.center = { x: 0, y: 0 };
      this.span = 384;
      this.generation = 0;
      this.sampler = null;
      this.samplerSeed = null;
      this.drag = null;
      this.bind();
      this.drawIdleState();
    }

    bind() {
      this.root.querySelector("[data-world-generate]").addEventListener(
        "click", () => this.generate());
      this.root.querySelector("[data-world-random]").addEventListener(
        "click", () => {
          const values = new BigInt64Array(1);
          crypto.getRandomValues(values);
          this.seedInput.value = values[0].toString();
          this.generate();
        });
      this.root.querySelector("[data-world-layer]").addEventListener(
        "click", event => {
          this.layer = this.layer === "terrain" ? "trees" : "terrain";
          event.currentTarget.textContent = this.layer === "terrain"
            ? "Tree density" : "Terrain";
          this.generate();
        });
      this.seedInput.addEventListener("keydown", event => {
        if (event.key === "Enter") this.generate();
      });
      this.canvas.addEventListener("pointerdown", event => {
        this.drag = { x: event.clientX, y: event.clientY,
          centerX: this.center.x, centerY: this.center.y };
        this.canvas.setPointerCapture(event.pointerId);
      });
      this.canvas.addEventListener("pointermove", event => {
        if (!this.drag) return;
        const scale = this.span / this.canvas.clientWidth;
        const isoX = (event.clientX - this.drag.x) * scale;
        const isoY = (event.clientY - this.drag.y) * scale;
        this.center.x = this.drag.centerX - isoX - isoY;
        this.center.y = this.drag.centerY + isoX - isoY;
      });
      this.canvas.addEventListener("pointerup", () => {
        if (!this.drag) return;
        this.drag = null;
        this.generate();
      });
      this.canvas.addEventListener("wheel", event => {
        event.preventDefault();
        this.span = clamp(this.span * (event.deltaY > 0 ? 1.25 : .8), 192, 3072);
        this.generate();
      }, { passive: false });
    }

    drawIdleState() {
      const gradient = this.context.createLinearGradient(
        0, 0, this.canvas.width, this.canvas.height);
      gradient.addColorStop(0, "#172819");
      gradient.addColorStop(.52, "#26391f");
      gradient.addColorStop(.68, "#86784d");
      gradient.addColorStop(.76, "#2b718d");
      gradient.addColorStop(1, "#12344f");
      this.context.fillStyle = gradient;
      this.context.fillRect(0, 0, this.canvas.width, this.canvas.height);
      this.status.textContent = "Enter a seed and generate";
    }

    parseSeed() {
      const text = this.seedInput.value.trim();
      if (/^[+-]?\d+$/.test(text)) {
        try {
          const numeric = BigInt(text);
          if (numeric >= -0x8000000000000000n &&
              numeric <= 0x7fffffffffffffffn) return numeric;
        } catch {
          // Match long.TryParse: invalid/overflowing numbers become text seeds.
        }
      }
      let hash = 1469598103934665603n;
      for (let index = 0; index < text.length; index++) {
        hash = I64((U64(hash) ^ BigInt(text.charCodeAt(index))) *
          1099511628211n);
      }
      return hash;
    }

    async generate() {
      const id = ++this.generation;
      const seed = this.parseSeed();
      if (!this.sampler || this.samplerSeed !== seed) {
        this.sampler = new IslandWorldSampler(seed);
        this.samplerSeed = seed;
      }
      const sampler = this.sampler;
      const size = this.canvas.width;
      const half = this.span * .72;
      const minX = this.center.x - half;
      const maxX = this.center.x + half;
      const minY = this.center.y - half;
      const maxY = this.center.y + half;
      this.root.classList.add("generating");
      this.status.textContent = "Preparing drainage 0%";
      const started = performance.now();
      try {
        await sampler.prepareHydrology(
          minX, minY, maxX, maxY,
          (done, total) => {
            this.status.textContent =
              `Preparing drainage ${Math.round(done / total * 100)}%`;
          },
          () => id !== this.generation);
        if (id !== this.generation) return;
        const image = this.context.createImageData(size, size);
        const tileCache = new Map();
        const riverPixels = new Uint8Array(size * size);
        const bridgeablePixels = new Uint8Array(size * size);
        for (let imageY = 0; imageY < size; imageY++) {
          for (let imageX = 0; imageX < size; imageX++) {
            const apparentIsoX = (imageX + .5) / size * this.span -
              this.span / 2;
            const apparentIsoY = (imageY + .5) / size * this.span -
              this.span / 2;
            let terrainIsoY = apparentIsoY;
            let tile;
            for (let iteration = 0; iteration < 3; iteration++) {
              const worldX = Math.floor(this.center.x +
                apparentIsoX + terrainIsoY);
              const worldY = Math.floor(this.center.y +
                terrainIsoY - apparentIsoX);
              const key = `${worldX},${worldY}`;
              tile = tileCache.get(key);
              if (!tile) {
                tile = sampler.sampleTile(worldX, worldY);
                tileCache.set(key, tile);
              }
              const elevation = tile.heights.reduce((a, b) => a + b, 0) / 4;
              terrainIsoY = apparentIsoY + elevation * 1.35;
            }
            const elevation = tile.heights.reduce((a, b) => a + b, 0) / 4;
            let color;
            if (this.layer === "trees") {
              const density = clamp(spawnChance(tile.region, elevation) / .31, 0, 1);
              if (density <= 0) {
                color = tile.region === Region.Ocean ? [10, 18, 25] : [24, 25, 21];
              } else if (density < .5) {
                const amount = density * 2;
                color = [24, 25, 21].map((value, channel) =>
                  Math.round(value + ([43, 142, 65][channel] - value) * amount));
              } else {
                const amount = (density - .5) * 2;
                color = [43, 142, 65].map((value, channel) =>
                  Math.round(value + ([238, 205, 70][channel] - value) * amount));
              }
            } else {
              color = biomeColor(tile);
            }
            const [north, east, south, west] = tile.heights;
            const slopeX = (east + south - north - west) * .5;
            const slopeY = (west + south - north - east) * .5;
            const relief = clamp((-slopeX + slopeY) * .065, -.24, .22);
            const elevationShade = (north + east + south + west) / 88;
            const shade = this.layer === "trees" ? 1 :
              tile.region === Region.Ocean ? .94 :
                .88 + elevationShade * .15 + relief;
            const index = (imageY * size + imageX) * 4;
            const pixel = imageY * size + imageX;
            riverPixels[pixel] = tile.biome === Biome.RiverWater ||
              tile.region === Region.River ? 1 : 0;
            bridgeablePixels[pixel] = tile.region !== Region.Ocean &&
              tile.biome !== Biome.DeepWater &&
              tile.biome !== Biome.ShallowWater ? 1 : 0;
            image.data[index] = clamp(color[0] * shade, 0, 255);
            image.data[index + 1] = clamp(color[1] * shade, 0, 255);
            image.data[index + 2] = clamp(color[2] * shade, 0, 255);
            image.data[index + 3] = 255;
          }
          if ((imageY & 3) === 3) {
            if (id !== this.generation) return;
            this.status.textContent =
              `Drawing world ${Math.round((imageY + 1) / size * 100)}%`;
            await nextTurn();
          }
        }
        if (this.layer === "terrain") {
          this.smoothRiverContinuity(
            image.data, riverPixels, bridgeablePixels, size);
        }
        this.context.putImageData(image, 0, 0);
        this.status.textContent = `Seed ${seed} · ${Math.round(
          performance.now() - started)} ms · ${Math.round(this.span)} tile span`;
      } catch (error) {
        if (error.name !== "AbortError") {
          this.status.textContent = "Generation failed";
          console.error(error);
        }
      } finally {
        if (id === this.generation) this.root.classList.remove("generating");
      }
    }

    smoothRiverContinuity(rgba, river, bridgeable, size) {
      const additions = new Uint8Array(river.length);
      const directions = [[1, 0], [0, 1], [1, 1], [1, -1]];
      for (let y = 0; y < size; y++) {
        for (let x = 0; x < size; x++) {
          if (!river[y * size + x]) continue;
          for (const [dx, dy] of directions) {
            for (let distance = 2; distance <= 3; distance++) {
              const endX = x + dx * distance;
              const endY = y + dy * distance;
              if (endX < 0 || endX >= size || endY < 0 ||
                  endY >= size || !river[endY * size + endX]) continue;
              let canBridge = true;
              for (let step = 1; step < distance; step++) {
                canBridge &&= Boolean(bridgeable[
                  (y + dy * step) * size + x + dx * step]);
              }
              if (!canBridge) break;
              for (let step = 1; step < distance; step++) {
                additions[(y + dy * step) * size + x + dx * step] = 1;
              }
              break;
            }
          }
        }
      }
      for (let pixel = 0; pixel < additions.length; pixel++) {
        if (!additions[pixel]) continue;
        const index = pixel * 4;
        rgba[index] = Math.trunc((rgba[index] + 45 * 3) / 4);
        rgba[index + 1] = Math.trunc((rgba[index + 1] + 125 * 3) / 4);
        rgba[index + 2] = Math.trunc((rgba[index + 2] + 171 * 3) / 4);
        rgba[index + 3] = 255;
      }
    }
  }

  if (typeof module !== "undefined" && module.exports) {
    module.exports = { IslandWorldSampler, Biome, Region };
  }
  if (typeof document !== "undefined") {
    const root = document.querySelector("[data-world-generator]");
    if (root) new WorldGenerationDemo(root);
  }
})();
