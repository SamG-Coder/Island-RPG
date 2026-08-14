(() => {
  const canvas = document.querySelector("[data-mini-canvas]");
  if (!canvas) return;
  const ctx = canvas.getContext("2d", { alpha: false });
  const hud = {
    hp: document.querySelector("[data-mini-hp]"),
    hunger: document.querySelector("[data-mini-hunger]"),
    clock: document.querySelector("[data-mini-clock]"),
    log: document.querySelector("[data-mini-log]"),
    inv: document.querySelector("[data-mini-inv]"),
    skills: document.querySelector("[data-mini-skills]"),
    craft: document.querySelector("[data-mini-craft]"),
    cursor: document.querySelector("[data-mini-cursor]"),
    seed: document.querySelector("[data-mini-seed]"),
    start: document.querySelector("[data-mini-start]"),
    ctxMenu: document.querySelector("[data-ctx]"),
    examine: document.querySelector("[data-examine]")
  };

  const W = 32;
  const H = 32;
  const TW = 72;
  const TH = 36;
  const INV = 10;
  const RANGE = 1.55;
  const EXAMINE = {
    sticks: "Dry kindling. Good for a fire, an axe haft, or nothing much else.",
    large_rock: "A heavy stone. Break it, sharpen it, or leave it on the beach.",
    small_rocks: "A handful of chips. Three of these make a fire ring.",
    sharpened_rock: "One edge will cut. Lash it to sticks for a real tool.",
    stone_axe: "A bound stone axe. Trees fall faster with this in the pack.",
    stone_pickaxe: "A crude pick. Outcrops give up their rock if you keep swinging.",
    logs: "Fresh timber from a felled tree.",
    berries: "Tart wild berries. Roast them on a lit fire.",
    cooked_berries: "Warm and sweet. Right-click and Eat.",
    raw_fish: "Still wet. Cook it beside a fire before you starve.",
    cooked_fish: "A proper meal. Right-click and Eat.",
    campfire: "A stone ring. Right-click and Place it on clear ground.",
    slime_gel: "Wobbly and faintly green. Edible, if you must."
  };

  const ITEMS = {
    sticks: { name: "Sticks", color: "#8a6230" },
    large_rock: { name: "Large rock", color: "#7b7f86" },
    small_rocks: { name: "Small rocks", color: "#9aa0a8" },
    sharpened_rock: { name: "Sharpened rock", color: "#b9b4a6" },
    stone_axe: { name: "Stone axe", color: "#6d5340" },
    stone_pickaxe: { name: "Stone pickaxe", color: "#5c6570" },
    logs: { name: "Logs", color: "#6b4423" },
    berries: { name: "Wild berries", color: "#8b2f4a" },
    cooked_berries: { name: "Roasted berries", color: "#c45a3a", food: 28 },
    raw_fish: { name: "Raw fish", color: "#6f8ea3" },
    cooked_fish: { name: "Cooked fish", color: "#d09a55", food: 42 },
    campfire: { name: "Campfire", color: "#c47a3a", place: true },
    slime_gel: { name: "Slime gel", color: "#6db36a", food: 8 }
  };

  const RECIPES = [
    { id: "break", name: "Break rock", out: "small_rocks", qty: 2, need: { large_rock: 1 }, skill: "crafting", xp: 8 },
    { id: "sharpen", name: "Sharpened rock", out: "sharpened_rock", qty: 1, need: { small_rocks: 2 }, skill: "crafting", xp: 12 },
    { id: "axe", name: "Stone axe", out: "stone_axe", qty: 1, need: { sharpened_rock: 1, sticks: 1 }, skill: "crafting", xp: 20 },
    { id: "pick", name: "Stone pickaxe", out: "stone_pickaxe", qty: 1, need: { sharpened_rock: 1, large_rock: 1, sticks: 1 }, skill: "crafting", xp: 24 },
    { id: "fire", name: "Campfire", out: "campfire", qty: 1, need: { small_rocks: 3 }, skill: "crafting", xp: 16 },
    { id: "cook-b", name: "Roast berries", out: "cooked_berries", qty: 1, need: { berries: 1 }, fire: true, skill: "cooking", xp: 10 },
    { id: "cook-f", name: "Cook fish", out: "cooked_fish", qty: 1, need: { raw_fish: 1 }, fire: true, skill: "cooking", xp: 14 }
  ];

  const SKILL_NAMES = ["woodcutting", "mining", "fishing", "farming", "crafting", "cooking", "firemaking", "attack"];

  let running = false;
  let last = 0;
  let state = null;
  let hover = null;
  let keys = {};
  let art = { tiles: {}, objects: {}, actors: [], slime: null, items: {}, campfire: null };
  let dragSlot = -1;

  const hash = (x, y, s) => {
    let n = (x * 374761393 + y * 668265263 + s * 1274126177) | 0;
    n = (n ^ (n >>> 13)) * 1274126177;
    return ((n ^ (n >>> 16)) >>> 0) / 4294967296;
  };
  const walkable = (tile) => tile === "beach" || tile === "grass" || tile === "forest";
  const view = () => ({ w: canvas.clientWidth || canvas.width, h: canvas.clientHeight || canvas.height });

  function loadImage(src) {
    return new Promise((resolve) => {
      const img = new Image();
      img.onload = () => resolve(img);
      img.onerror = () => resolve(null);
      img.src = src;
    });
  }

  function chroma(img, cropBottom = .09) {
    if (!img) return null;
    const c = document.createElement("canvas");
    c.width = img.width;
    c.height = Math.floor(img.height * (1 - cropBottom));
    const g = c.getContext("2d");
    g.drawImage(img, 0, 0);
    const data = g.getImageData(0, 0, c.width, c.height);
    const d = data.data;
    for (let i = 0; i < d.length; i += 4) {
      const r = d[i], gr = d[i + 1], b = d[i + 2];
      if (gr > 130 && gr > r + 35 && gr > b + 35) d[i + 3] = 0;
    }
    g.putImageData(data, 0, 0);
    return c;
  }

  function crop(src, x, y, w, h) {
    const c = document.createElement("canvas");
    c.width = Math.max(1, w);
    c.height = Math.max(1, h);
    c.getContext("2d").drawImage(src, x, y, w, h, 0, 0, w, h);
    return c;
  }

  function blobs(sheet, min = 280) {
    if (!sheet) return [];
    const g = sheet.getContext("2d");
    const { width, height } = sheet;
    const data = g.getImageData(0, 0, width, height).data;
    const seen = new Uint8Array(width * height);
    const out = [];
    const idx = (x, y) => y * width + x;
    for (let y = 0; y < height; y++)
      for (let x = 0; x < width; x++) {
        const i = idx(x, y);
        if (seen[i] || data[i * 4 + 3] < 20) continue;
        const q = [[x, y]];
        seen[i] = 1;
        let minX = x, minY = y, maxX = x, maxY = y, area = 0, r = 0, gr = 0, b = 0;
        while (q.length) {
          const [cx, cy] = q.pop();
          area++;
          const p = idx(cx, cy) * 4;
          r += data[p]; gr += data[p + 1]; b += data[p + 2];
          minX = Math.min(minX, cx); minY = Math.min(minY, cy);
          maxX = Math.max(maxX, cx); maxY = Math.max(maxY, cy);
          for (const [ox, oy] of [[1,0],[-1,0],[0,1],[0,-1]]) {
            const nx = cx + ox, ny = cy + oy;
            if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
            const ni = idx(nx, ny);
            if (seen[ni] || data[ni * 4 + 3] < 20) continue;
            seen[ni] = 1;
            q.push([nx, ny]);
          }
        }
        if (area < min) continue;
        out.push({
          canvas: crop(sheet, minX, minY, maxX - minX + 1, maxY - minY + 1),
          x: minX, y: minY, area,
          r: r / area, g: gr / area, b: b / area
        });
      }
    return out.sort((a, b) => a.x - b.x || a.y - b.y);
  }

  function sliceGrid(sheet, cols, rows) {
    if (!sheet) return [];
    const cw = Math.floor(sheet.width / cols);
    const ch = Math.floor(sheet.height / rows);
    const cells = [];
    for (let y = 0; y < rows; y++)
      for (let x = 0; x < cols; x++)
        cells.push(crop(sheet, x * cw, y * ch, cw, ch));
    return cells;
  }

  async function loadArt() {
    const [tiles, objects, actors, items, food, campfire] = await Promise.all([
      loadImage("assets/mini/tiles.jpg"),
      loadImage("assets/mini/objects.jpg"),
      loadImage("assets/mini/actors.jpg"),
      loadImage("assets/mini/items.jpg"),
      loadImage("assets/mini/food.jpg"),
      loadImage("assets/campfire.png")
    ]);
    const tileBlobs = blobs(chroma(tiles));
    for (const blob of tileBlobs) {
      const { r, g, b } = blob;
      if (b > r + 15 && b > g - 10 && r < 90) art.tiles.deep = blob.canvas;
      else if (g > 140 && b > 140 && r < 140) art.tiles.shallow = blob.canvas;
      else if (r > 160 && g > 120 && b < 110) art.tiles.beach = blob.canvas;
      else if (g > r + 20 && g > b + 20 && g > 90 && r < 140) {
        if (!art.tiles.grass || blob.g > 120) art.tiles.grass = blob.canvas;
        else art.tiles.forest = blob.canvas;
      } else if (Math.abs(r - g) < 25 && r > 90) art.tiles.rock = blob.canvas;
      else if (g > 70 && r < 120) art.tiles.forest = art.tiles.forest || blob.canvas;
    }
    if (!art.tiles.forest) art.tiles.forest = art.tiles.grass;
    const objBlobs = blobs(chroma(objects), 500);
    for (const blob of objBlobs) {
      if (blob.r > blob.b + 20 && blob.g > 80 && blob.r > 70 && blob.area > 4000)
        art.objects.tree = blob.canvas;
      else if (blob.r > 90 && blob.g > 70) art.objects.bush = blob.canvas;
      else art.objects.rock = blob.canvas;
    }
    const actorBlobs = blobs(chroma(actors), 400);
    art.actors = actorBlobs.filter((b) => !(b.g > b.r + 40 && b.g > b.b + 20 && b.area < 3500))
      .slice(0, 4).map((b) => b.canvas);
    art.slime = actorBlobs.find((b) => b.g > b.r + 30 && b.g > b.b + 15)?.canvas || null;
    const itemCells = sliceGrid(chroma(items, .08), 4, 2);
    const itemIds = ["sticks", "large_rock", "small_rocks", "sharpened_rock", "stone_axe", "stone_pickaxe", "logs", "berries"];
    itemIds.forEach((id, i) => { if (itemCells[i]) art.items[id] = itemCells[i]; });
    const foodCells = sliceGrid(chroma(food, .08), 2, 2);
    if (foodCells[0]) art.items.raw_fish = foodCells[0];
    if (foodCells[1]) art.items.cooked_fish = foodCells[1];
    if (foodCells[2]) art.items.cooked_berries = foodCells[2];
    if (foodCells[3]) art.items.slime_gel = foodCells[3];
    art.campfire = campfire;
    art.items.campfire = campfire;
  }

  function generate(seed) {
    const tiles = [];
    const trees = [];
    const bushes = [];
    const rocks = [];
    const items = [];
    const objects = [];
    let spawn = { x: 16, y: 25 };
    for (let y = 0; y < H; y++) {
      tiles[y] = [];
      for (let x = 0; x < W; x++) {
        const d = Math.hypot(x - 15.2, y - 14.8) + (hash(x, y, seed) - .5) * 1.5;
        let tile = "deep";
        if (d < 14.2) tile = "shallow";
        if (d < 12.3) tile = "beach";
        if (d < 10.8) tile = hash(x, y, seed + 3) > .8 ? "rock" : "grass";
        if (d < 8.6 && hash(x, y, seed + 7) > .4) tile = "forest";
        tiles[y][x] = tile;
        if (tile === "beach" && y > 20) spawn = { x, y };
        if (tile === "forest" && hash(x, y, seed + 11) > .52)
          trees.push({ x, y, hp: 8 });
        if (tile === "grass" && hash(x, y, seed + 17) > .87)
          bushes.push({ x, y, hp: 3 });
        if ((tile === "rock" || tile === "grass") && hash(x, y, seed + 23) > .91)
          rocks.push({ x, y, hp: 10 });
        if (tile === "beach" && hash(x, y, seed + 29) > .84)
          items.push({ x: x + .35, y: y + .25, id: "sticks", qty: 1 });
        if (tile === "grass" && hash(x, y, seed + 31) > .94)
          items.push({ x: x + .4, y: y + .35, id: "large_rock", qty: 1 });
      }
    }
    const slimeAt = firstOpen(tiles, 13, 12, 7);
    return {
      seed, tiles, trees, bushes, rocks, items, objects,
      player: { x: spawn.x + .5, y: spawn.y + .5, facing: 0, hp: 100, hunger: 100, path: [], act: null, moving: false },
      slime: slimeAt ? { x: slimeAt.x + .5, y: slimeAt.y + .5, hp: 36, cd: 0, alive: true } : null,
      inv: Array(INV).fill(null),
      selected: 0,
      skills: Object.fromEntries(SKILL_NAMES.map((n) => [n, 0])),
      day: .3,
      log: []
    };
  }

  function firstOpen(tiles, cx, cy, r) {
    for (let y = cy - r; y <= cy + r; y++)
      for (let x = cx - r; x <= cx + r; x++)
        if (tiles[y]?.[x] && walkable(tiles[y][x])) return { x, y };
    return null;
  }

  function blocked(x, y) {
    const tx = Math.floor(x), ty = Math.floor(y);
    if (!walkable(state.tiles[ty]?.[tx])) return true;
    return [...state.trees, ...state.rocks, ...state.objects].some((o) => o.x === tx && o.y === ty);
  }

  function pathTo(sx, sy, gx, gy) {
    const start = `${Math.floor(sx)},${Math.floor(sy)}`;
    const goal = `${gx},${gy}`;
    if (start === goal) return [];
    const q = [[Math.floor(sx), Math.floor(sy)]];
    const seen = new Map([[start, null]]);
    const dirs = [[1,0],[-1,0],[0,1],[0,-1],[1,1],[1,-1],[-1,1],[-1,-1]];
    while (q.length) {
      const [x, y] = q.shift();
      if (`${x},${y}` === goal) break;
      for (const [dx, dy] of dirs) {
        const nx = x + dx, ny = y + dy, key = `${nx},${ny}`;
        if (seen.has(key) || nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
        const dest = key === goal;
        if (!dest && blocked(nx + .5, ny + .5)) continue;
        if (dest && !walkable(state.tiles[ny]?.[nx]) && !nearWater(nx, ny) && !entityAt(nx, ny) && !itemAt(nx, ny))
          continue;
        seen.set(key, [x, y]);
        q.push([nx, ny]);
      }
    }
    if (!seen.has(goal)) return [];
    const path = [];
    for (let cur = goal; cur && cur !== start; ) {
      const [x, y] = cur.split(",").map(Number);
      path.push({ x: x + .5, y: y + .5 });
      const p = seen.get(cur);
      cur = p ? `${p[0]},${p[1]}` : null;
    }
    return path.reverse();
  }

  function nearWater(x, y) {
    for (let oy = -1; oy <= 1; oy++)
      for (let ox = -1; ox <= 1; ox++) {
        const t = state.tiles[y + oy]?.[x + ox];
        if (t === "shallow" || t === "deep") return true;
      }
    return false;
  }
  function entityAt(x, y) {
    return state.trees.find((t) => t.x === x && t.y === y)
      || state.bushes.find((b) => b.x === x && b.y === y)
      || state.rocks.find((r) => r.x === x && r.y === y)
      || state.objects.find((o) => o.x === x && o.y === y)
      || null;
  }
  function itemAt(x, y) {
    return state.items.find((i) => Math.floor(i.x) === x && Math.floor(i.y) === y) || null;
  }
  const dist = (a, b) => Math.hypot(a.x - b.x, a.y - b.y);

  function log(text) {
    state.log.unshift(text);
    state.log = state.log.slice(0, 4);
    if (hud.log) hud.log.innerHTML = state.log.map((line) => `<p>${line}</p>`).join("");
  }

  function countItem(id) {
    return state.inv.reduce((n, s) => s?.id === id ? n + s.qty : n, 0);
  }
  function takeItem(id, qty) {
    let left = qty;
    for (const slot of state.inv) {
      if (!slot || slot.id !== id) continue;
      const n = Math.min(slot.qty, left);
      slot.qty -= n;
      left -= n;
    }
    state.inv = state.inv.map((s) => s && s.qty > 0 ? s : null);
    return left === 0;
  }
  function giveItem(id, qty) {
    for (const slot of state.inv) {
      if (slot?.id === id) { slot.qty += qty; return true; }
    }
    const empty = state.inv.findIndex((s) => !s);
    if (empty < 0) return false;
    state.inv[empty] = { id, qty };
    return true;
  }

  function litFireNear() {
    return state.objects.some((o) => o.kind === "campfire" && o.lit
      && dist(o, { x: Math.floor(state.player.x), y: Math.floor(state.player.y) }) < 2.2);
  }
  function canCraft(recipe) {
    if (recipe.fire && !litFireNear()) return false;
    return Object.entries(recipe.need).every(([id, n]) => countItem(id) >= n);
  }
  function craft(recipe) {
    if (!canCraft(recipe)) {
      log(recipe.fire && !litFireNear() ? "Cook that beside a lit campfire." : "You are missing ingredients.");
      return;
    }
    for (const [id, n] of Object.entries(recipe.need)) takeItem(id, n);
    if (!giveItem(recipe.out, recipe.qty)) {
      for (const [id, n] of Object.entries(recipe.need)) giveItem(id, n);
      log("Your pack is full.");
      return;
    }
    state.skills[recipe.skill] += recipe.xp;
    log(`You make ${ITEMS[recipe.out].name.toLowerCase()}.`);
    paintInv();
    paintSkills();
  }

  function inspect(tx, ty) {
    const actions = [];
    if (state.slime?.alive && Math.floor(state.slime.x) === tx && Math.floor(state.slime.y) === ty)
      actions.push({ kind: "attack", label: "Attack slime", x: tx, y: ty });
    const obj = state.objects.find((o) => o.x === tx && o.y === ty);
    if (obj?.kind === "campfire") {
      actions.push({ kind: obj.lit ? "fuel" : "light", label: obj.lit ? "Add sticks" : "Light fire", x: tx, y: ty });
      actions.push({ kind: "look", label: "Examine fire", x: tx, y: ty, text: obj.lit ? "The ring is burning." : "A cold stone ring." });
    }
    if (state.trees.some((t) => t.x === tx && t.y === ty)) {
      actions.push({ kind: "chop", label: "Chop tree", x: tx, y: ty });
      actions.push({ kind: "look", label: "Examine tree", x: tx, y: ty, text: "A shoreline oak. An axe would help." });
    }
    if (state.rocks.some((r) => r.x === tx && r.y === ty)) {
      actions.push({ kind: "mine", label: "Mine rock", x: tx, y: ty });
      actions.push({ kind: "look", label: "Examine rock", x: tx, y: ty, text: "A stubborn outcrop." });
    }
    if (state.bushes.some((b) => b.x === tx && b.y === ty))
      actions.push({ kind: "pick", label: "Pick berries", x: tx, y: ty });
    const ground = itemAt(tx, ty);
    if (ground) actions.push({ kind: "loot", label: `Take ${ITEMS[ground.id].name.toLowerCase()}`, x: tx, y: ty });
    const tile = state.tiles[ty]?.[tx];
    if ((tile === "shallow" || tile === "beach") && nearWater(tx, ty))
      actions.push({ kind: "fish", label: "Fish here", x: tx, y: ty });
    const held = state.inv[state.selected];
    if (held?.id === "campfire" && walkable(tile) && !entityAt(tx, ty))
      actions.push({ kind: "place", label: "Place campfire", x: tx, y: ty });
    if (walkable(tile)) actions.push({ kind: "walk", label: "Walk here", x: tx, y: ty });
    if (!actions.length) actions.push({ kind: "look", label: "Examine", x: tx, y: ty, text: "Empty ground." });
    return actions;
  }

  function worldMenu(tx, ty, clientX, clientY) {
    const actions = inspect(tx, ty);
    openMenu(actions.map((a) => ({ label: a.label, run: () => order(a) })), clientX, clientY);
  }

  function bagMenu(slot, clientX, clientY) {
    const item = state.inv[slot];
    if (!item) return;
    const def = ITEMS[item.id];
    const actions = [];
    if (def.food) actions.push({ label: "Eat", run: () => useSlot(slot, "eat") });
    if (def.place) actions.push({ label: "Place", run: () => { state.selected = slot; paintInv(); log("Left-click clear ground, or right-click and Place campfire."); } });
    actions.push({ label: "Drop", run: () => useSlot(slot, "drop") });
    actions.push({ label: "Examine", run: () => showExamine(def.name, EXAMINE[item.id] || "An island thing.") });
    openMenu(actions, clientX, clientY);
  }

  function openMenu(actions, x, y) {
    if (!hud.ctxMenu) return;
    hud.ctxMenu.hidden = false;
    hud.ctxMenu.innerHTML = actions.map((a, i) => `<button type="button" data-i="${i}">${a.label}</button>`).join("");
    hud.ctxMenu.style.left = `${Math.min(x, innerWidth - 180)}px`;
    hud.ctxMenu.style.top = `${Math.min(y, innerHeight - 40 * actions.length)}px`;
    hud.ctxMenu.onclick = (event) => {
      const btn = event.target.closest("[data-i]");
      if (!btn) return;
      actions[Number(btn.dataset.i)].run();
      closeMenus();
    };
  }

  function showExamine(title, text) {
    if (!hud.examine) return;
    hud.examine.hidden = false;
    hud.examine.innerHTML = `<strong>${title}</strong><p>${text}</p>`;
    const box = hud.ctxMenu?.getBoundingClientRect();
    hud.examine.style.left = `${(box?.right || 80) + 8}px`;
    hud.examine.style.top = `${box?.top || 80}px`;
  }

  function closeMenus() {
    if (hud.ctxMenu) hud.ctxMenu.hidden = true;
    if (hud.examine) hud.examine.hidden = true;
  }

  function useSlot(slot, mode) {
    state.selected = slot;
    const item = state.inv[slot];
    if (!item) return;
    if (mode === "eat") {
      const food = ITEMS[item.id].food;
      if (!food) { log("That is not food."); return; }
      takeItem(item.id, 1);
      state.player.hunger = Math.min(100, state.player.hunger + food);
      state.player.hp = Math.min(100, state.player.hp + food * .25);
      log(`You eat ${ITEMS[item.id].name.toLowerCase()}.`);
    }
    if (mode === "drop") {
      state.items.push({ x: state.player.x, y: state.player.y, id: item.id, qty: 1 });
      takeItem(item.id, 1);
      log(`You drop ${ITEMS[item.id].name.toLowerCase()}.`);
    }
    paintInv();
  }

  function adjacentStand(x, y, onto) {
    if (onto && walkable(state.tiles[y]?.[x]) && !entityAt(x, y)) return { x, y };
    const opts = [];
    for (let oy = -1; oy <= 1; oy++)
      for (let ox = -1; ox <= 1; ox++) {
        if (!ox && !oy) continue;
        const nx = x + ox, ny = y + oy;
        if (walkable(state.tiles[ny]?.[nx]) && !entityAt(nx, ny))
          opts.push({ x: nx, y: ny, d: Math.hypot(nx + .5 - state.player.x, ny + .5 - state.player.y) });
      }
    opts.sort((a, b) => a.d - b.d);
    return opts[0] ? { x: opts[0].x, y: opts[0].y } : null;
  }

  function order(action) {
    if (!action || action.kind === "look") {
      if (action?.text) showExamine(action.label, action.text);
      return;
    }
    const onto = action.kind === "walk" || action.kind === "place" || action.kind === "fish";
    const stand = adjacentStand(action.x, action.y, onto);
    if (!stand) { log("You cannot reach that."); return; }
    state.player.path = pathTo(state.player.x, state.player.y, stand.x, stand.y);
    state.player.act = action;
    if (!state.player.path.length) {
      if (dist(state.player, { x: action.x + .5, y: action.y + .5 }) <= RANGE + .35) tryAct();
      else { state.player.act = null; log("You cannot reach that."); }
    }
  }

  function tryAct() {
    const act = state.player.act;
    if (!act) return;
    if (dist(state.player, { x: act.x + .5, y: act.y + .5 }) > RANGE + .35) return;
    state.player.act = null;
    if (act.kind === "walk") return;
    if (act.kind === "loot") {
      const item = itemAt(act.x, act.y);
      if (!item) return;
      if (!giveItem(item.id, item.qty)) { log("Your pack is full."); return; }
      state.items = state.items.filter((i) => i !== item);
      log(`You pick up ${ITEMS[item.id].name.toLowerCase()}.`);
      paintInv();
      return;
    }
    if (act.kind === "chop") {
      const tree = state.trees.find((t) => t.x === act.x && t.y === act.y);
      if (!tree) return;
      tree.hp -= countItem("stone_axe") ? 3 : 1;
      state.skills.woodcutting += 6;
      if (tree.hp <= 0) {
        state.trees = state.trees.filter((t) => t !== tree);
        state.items.push({ x: act.x + .3, y: act.y + .2, id: "logs", qty: 1 });
        state.items.push({ x: act.x + .6, y: act.y + .5, id: "sticks", qty: 2 });
        log("The tree falls. Logs and sticks drop.");
      } else log(countItem("stone_axe") ? "You hew the trunk." : "You break branches by hand.");
      paintSkills();
      return;
    }
    if (act.kind === "mine") {
      const node = state.rocks.find((r) => r.x === act.x && r.y === act.y);
      if (!node) return;
      node.hp -= countItem("stone_pickaxe") ? 4 : 1;
      state.skills.mining += 7;
      if (node.hp <= 0) {
        state.rocks = state.rocks.filter((r) => r !== node);
        state.items.push({ x: act.x + .4, y: act.y + .35, id: "large_rock", qty: 2 });
        log("The outcrop breaks into large rocks.");
      } else log(countItem("stone_pickaxe") ? "The pick bites." : "You knock flakes loose.");
      paintSkills();
      return;
    }
    if (act.kind === "pick") {
      const bush = state.bushes.find((b) => b.x === act.x && b.y === act.y);
      if (!bush) return;
      bush.hp -= 1;
      if (!giveItem("berries", 1)) { log("Your pack is full."); return; }
      state.skills.farming += 4;
      log("You pick wild berries.");
      if (bush.hp <= 0) state.bushes = state.bushes.filter((b) => b !== bush);
      paintInv();
      return;
    }
    if (act.kind === "fish") {
      if (hash(act.x, act.y, (state.day * 1000) | 0) < .35) { log("The line comes up empty."); return; }
      if (!giveItem("raw_fish", 1)) { log("Your pack is full."); return; }
      state.skills.fishing += 9;
      log("You land a raw fish.");
      paintInv();
      paintSkills();
      return;
    }
    if (act.kind === "place") {
      if (state.inv[state.selected]?.id !== "campfire") return;
      takeItem("campfire", 1);
      state.objects.push({ x: act.x, y: act.y, kind: "campfire", fuel: 0, lit: false });
      log("You set a stone fire ring.");
      paintInv();
      return;
    }
    if (act.kind === "light" || act.kind === "fuel") {
      const fire = state.objects.find((o) => o.x === act.x && o.y === act.y);
      if (!fire) return;
      if (!takeItem("sticks", 1)) { log("You need sticks for fuel."); return; }
      fire.fuel += 18;
      log("You add sticks to the ring.");
      if (!fire.lit && fire.fuel > 0) {
        fire.lit = true;
        state.skills.firemaking += 15;
        log("The campfire catches.");
        paintSkills();
      }
      paintInv();
      return;
    }
    if (act.kind === "attack" && state.slime?.alive) {
      state.slime.hp -= 7 + (countItem("stone_axe") ? 5 : 0);
      state.skills.attack += 8;
      log("You strike the slime.");
      if (state.slime.hp <= 0) {
        state.slime.alive = false;
        state.items.push({ x: state.slime.x, y: state.slime.y, id: "slime_gel", qty: 1 });
        log("The slime collapses. Gel drops.");
      }
      paintSkills();
    }
  }

  function camera() {
    return {
      x: (state.player.x - state.player.y) * (TW / 2),
      y: (state.player.x + state.player.y) * (TH / 2)
    };
  }
  function project(x, y) {
    const { w, h } = view();
    const cam = camera();
    return {
      x: (x - y) * (TW / 2) - cam.x + w / 2,
      y: (x + y) * (TH / 2) - cam.y + h * .52
    };
  }
  function worldFromEvent(event) {
    const rect = canvas.getBoundingClientRect();
    const { w, h } = view();
    const sx = (event.clientX - rect.left) * (w / rect.width);
    const sy = (event.clientY - rect.top) * (h / rect.height);
    const cam = camera();
    const ix = sx - w / 2 + cam.x;
    const iy = sy - h * .52 + cam.y;
    return {
      x: Math.floor((ix / (TW / 2) + iy / (TH / 2)) / 2),
      y: Math.floor((iy / (TH / 2) - ix / (TW / 2)) / 2)
    };
  }

  function update(dt) {
    const p = state.player;
    state.day = (state.day + dt / 110) % 1;
    p.hunger = Math.max(0, p.hunger - dt * 1.05);
    if (p.hunger <= 0) p.hp = Math.max(0, p.hp - dt * 4);
    else if (p.hp < 100) p.hp = Math.min(100, p.hp + dt * 1.3);
    let mx = 0, my = 0;
    if (keys.KeyW || keys.ArrowUp) { mx -= 1; my -= 1; }
    if (keys.KeyS || keys.ArrowDown) { mx += 1; my += 1; }
    if (keys.KeyA || keys.ArrowLeft) { mx -= 1; my += 1; }
    if (keys.KeyD || keys.ArrowRight) { mx += 1; my -= 1; }
    p.moving = false;
    if (mx || my) {
      p.path = [];
      p.act = null;
      const len = Math.hypot(mx, my) || 1;
      const nx = p.x + (mx / len) * dt * 3.15;
      const ny = p.y + (my / len) * dt * 3.15;
      if (!blocked(nx, p.y)) p.x = nx;
      if (!blocked(p.x, ny)) p.y = ny;
      p.facing = facingFrom(mx, my);
      p.moving = true;
    } else if (p.path.length) {
      const step = p.path[0];
      const dx = step.x - p.x, dy = step.y - p.y;
      const d = Math.hypot(dx, dy);
      if (d < .06) p.path.shift();
      else {
        p.x += (dx / d) * dt * 3.25;
        p.y += (dy / d) * dt * 3.25;
        p.facing = facingFrom(dx, dy);
        p.moving = true;
      }
      if (!p.path.length) tryAct();
    }
    for (const fire of state.objects) {
      if (!fire.lit) continue;
      fire.fuel -= dt;
      if (fire.fuel <= 0) { fire.fuel = 0; fire.lit = false; }
    }
    if (state.slime?.alive) {
      const s = state.slime;
      const d = dist(s, p);
      if (d < 6.5) {
        const ang = Math.atan2(p.y - s.y, p.x - s.x);
        const nx = s.x + Math.cos(ang) * dt * 1.3;
        const ny = s.y + Math.sin(ang) * dt * 1.3;
        if (!blocked(nx, s.y)) s.x = nx;
        if (!blocked(s.x, ny)) s.y = ny;
      }
      s.cd = Math.max(0, s.cd - dt);
      if (d < 1.15 && s.cd <= 0) {
        s.cd = 1.1;
        p.hp = Math.max(0, p.hp - 8);
        log("The slime lashes you.");
      }
    }
    if (p.hp <= 0) {
      log("You fall. The shore takes you back.");
      p.hp = 70;
      p.hunger = 55;
      const open = firstOpen(state.tiles, 16, 25, 6);
      if (open) { p.x = open.x + .5; p.y = open.y + .5; }
    }
    paintVitals();
  }

  function facingFrom(dx, dy) {
    if (Math.abs(dx) > Math.abs(dy)) return dx >= 0 ? 0 : 1;
    return dy >= 0 ? 0 : 2;
  }

  function drawSprite(img, x, y, w, h) {
    if (!img) return false;
    ctx.imageSmoothingEnabled = false;
    ctx.drawImage(img, x - w / 2, y - h, w, h);
    return true;
  }

  function draw() {
    const { w, h } = view();
    const dusk = Math.sin(state.day * Math.PI * 2);
    ctx.fillStyle = dusk > 0
      ? `rgb(${86 + dusk * 50},${138 + dusk * 30},${168})`
      : `rgb(${12},${16},${28})`;
    ctx.fillRect(0, 0, w, h);

    for (let y = 0; y < H; y++)
      for (let x = 0; x < W; x++) {
        const p = project(x, y);
        if (p.x < -TW || p.x > w + TW || p.y < -80 || p.y > h + 40) continue;
        const tile = state.tiles[y][x];
        const sprite = art.tiles[tile];
        if (sprite) {
          const bob = (tile === "shallow" || tile === "deep")
            ? Math.sin(performance.now() / 420 + x * .4 + y * .3) * 1.5
            : 0;
          drawSprite(sprite, p.x, p.y + 10 + bob, TW + 8, TH * 1.55);
        } else {
          const fills = { deep: "#18424e", shallow: "#2b7a82", beach: "#c4a66a", grass: "#4e7c43", forest: "#355833", rock: "#6d7178" };
          ctx.beginPath();
          ctx.moveTo(p.x, p.y - TH / 2);
          ctx.lineTo(p.x + TW / 2, p.y);
          ctx.lineTo(p.x, p.y + TH / 2);
          ctx.lineTo(p.x - TW / 2, p.y);
          ctx.closePath();
          ctx.fillStyle = fills[tile];
          ctx.fill();
        }
        if (hover && hover.x === x && hover.y === y) {
          ctx.globalAlpha = .28;
          ctx.fillStyle = "#f0dc96";
          ctx.beginPath();
          ctx.moveTo(p.x, p.y - TH / 2);
          ctx.lineTo(p.x + TW / 2, p.y);
          ctx.lineTo(p.x, p.y + TH / 2);
          ctx.lineTo(p.x - TW / 2, p.y);
          ctx.fill();
          ctx.globalAlpha = 1;
        }
      }

    const list = [];
    for (const t of state.trees) list.push({ sort: t.x + t.y, draw: () => drawProp(t, art.objects.tree, 92, 110) });
    for (const b of state.bushes) list.push({ sort: b.x + b.y, draw: () => drawProp(b, art.objects.bush, 64, 56) });
    for (const r of state.rocks) list.push({ sort: r.x + r.y, draw: () => drawProp(r, art.objects.rock, 70, 62) });
    for (const o of state.objects) list.push({ sort: o.x + o.y, draw: () => drawFire(o) });
    for (const i of state.items) list.push({ sort: i.x + i.y, draw: () => drawLoot(i) });
    list.push({ sort: state.player.x + state.player.y, draw: drawPlayer });
    if (state.slime?.alive) list.push({ sort: state.slime.x + state.slime.y, draw: drawSlime });
    list.sort((a, b) => a.sort - b.sort);
    for (const s of list) s.draw();

    const night = dusk < 0 ? -dusk * .42 : 0;
    if (night > 0) {
      ctx.fillStyle = `rgba(5,8,16,${night})`;
      ctx.fillRect(0, 0, w, h);
      for (const o of state.objects) {
        if (!o.lit) continue;
        const p = project(o.x + .2, o.y + .2);
        const g = ctx.createRadialGradient(p.x, p.y - 10, 6, p.x, p.y - 10, 110);
        g.addColorStop(0, "rgba(255,170,60,.32)");
        g.addColorStop(1, "transparent");
        ctx.fillStyle = g;
        ctx.beginPath();
        ctx.arc(p.x, p.y - 10, 110, 0, Math.PI * 2);
        ctx.fill();
      }
    }
    if (hud.cursor && hover) {
      const first = inspect(hover.x, hover.y)[0];
      hud.cursor.textContent = first ? `${first.label} · right-click for more` : "Right-click the bag or the world";
    }
  }

  function drawProp(o, img, w, h) {
    const p = project(o.x + .15, o.y + .2);
    if (!drawSprite(img, p.x, p.y + 6, w, h)) {
      ctx.fillStyle = "#355833";
      ctx.beginPath();
      ctx.ellipse(p.x, p.y - 16, 14, 18, 0, 0, Math.PI * 2);
      ctx.fill();
    }
  }

  function drawFire(o) {
    const p = project(o.x + .15, o.y + .15);
    drawSprite(art.campfire || art.items.campfire, p.x, p.y + 4, 42, 34);
    if (o.lit) {
      ctx.fillStyle = `rgba(255,140,40,${.5 + Math.sin(performance.now() / 110) * .2})`;
      ctx.beginPath();
      ctx.moveTo(p.x, p.y - 26);
      ctx.lineTo(p.x - 8, p.y - 6);
      ctx.lineTo(p.x + 8, p.y - 6);
      ctx.fill();
    }
  }

  function drawLoot(i) {
    const p = project(i.x, i.y);
    if (!drawSprite(art.items[i.id], p.x, p.y + 4, 28, 28)) {
      ctx.fillStyle = ITEMS[i.id].color;
      ctx.beginPath();
      ctx.arc(p.x, p.y, 5, 0, Math.PI * 2);
      ctx.fill();
    }
  }

  function drawPlayer() {
    const p = project(state.player.x, state.player.y);
    const frame = art.actors[state.player.facing] || art.actors[0];
    const bob = state.player.moving ? Math.sin(performance.now() / 90) * 2 : 0;
    if (!drawSprite(frame, p.x, p.y + 8 + bob, 44, 58)) {
      ctx.fillStyle = "#3f6d8a";
      ctx.fillRect(p.x - 7, p.y - 18, 14, 20);
    }
  }

  function drawSlime() {
    const p = project(state.slime.x, state.slime.y);
    const bob = Math.sin(performance.now() / 170) * 3;
    if (!drawSprite(art.slime, p.x, p.y + 6 + bob, 48, 40)) {
      ctx.fillStyle = "#67b85c";
      ctx.beginPath();
      ctx.ellipse(p.x, p.y + bob, 13, 9, 0, 0, Math.PI * 2);
      ctx.fill();
    }
  }

  function paintInv() {
    if (!hud.inv) return;
    hud.inv.innerHTML = state.inv.map((slot, i) => {
      const on = i === state.selected ? " on" : "";
      if (!slot) return `<button type="button" class="mini-slot${on}" data-slot="${i}" aria-label="Empty slot"></button>`;
      const icon = art.items[slot.id];
      const img = icon ? `<img alt="" src="${icon.toDataURL ? icon.toDataURL("image/png") : ""}">` : `<span class="swatch" style="background:${ITEMS[slot.id].color}"></span>`;
      return `<button type="button" class="mini-slot${on}" data-slot="${i}" draggable="true">
        ${icon ? `<canvas data-icon="${slot.id}" width="36" height="36"></canvas>` : img}
        <span class="qty">${slot.qty}</span>
        <span class="name">${ITEMS[slot.id].name}</span>
      </button>`;
    }).join("");
    hud.inv.querySelectorAll("canvas[data-icon]").forEach((c) => {
      const icon = art.items[c.dataset.icon];
      if (!icon) return;
      const g = c.getContext("2d");
      g.imageSmoothingEnabled = false;
      g.clearRect(0, 0, 36, 36);
      g.drawImage(icon, 2, 2, 32, 32);
    });
    paintCraft();
  }

  function paintCraft() {
    if (!hud.craft) return;
    hud.craft.innerHTML = RECIPES.map((recipe) => {
      const ok = canCraft(recipe);
      const need = Object.entries(recipe.need).map(([id, n]) => `${n} ${ITEMS[id].name.toLowerCase()}`).join(", ");
      return `<button type="button" class="mini-recipe${ok ? "" : " off"}" data-recipe="${recipe.id}" ${ok ? "" : "disabled"}>
        <strong>${recipe.name}</strong><span>${need}${recipe.fire ? " · lit fire" : ""}</span>
      </button>`;
    }).join("");
  }

  function paintSkills() {
    if (!hud.skills) return;
    hud.skills.innerHTML = SKILL_NAMES.map((name) =>
      `<li><span>${name}</span><b>${Math.floor(state.skills[name])}</b></li>`).join("");
  }

  function paintVitals() {
    if (hud.hp) hud.hp.style.width = `${state.player.hp}%`;
    if (hud.hunger) hud.hunger.style.width = `${state.player.hunger}%`;
    if (hud.clock) hud.clock.textContent = `${String(Math.floor(state.day * 24)).padStart(2, "0")}:00`;
  }

  function resize() {
    const dpr = Math.min(devicePixelRatio || 1, 2);
    const w = canvas.clientWidth || 1280;
    const h = canvas.clientHeight || 720;
    canvas.width = Math.floor(w * dpr);
    canvas.height = Math.floor(h * dpr);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  }

  function tick(now) {
    if (!running || !state) return;
    const dt = Math.min(.05, (now - last) / 1000 || .016);
    last = now;
    update(dt);
    draw();
    requestAnimationFrame(tick);
  }

  function start(seed) {
    state = generate(Number.isFinite(seed) ? seed : 2187);
    giveItem("sticks", 1);
    running = true;
    last = performance.now();
    paintInv();
    paintSkills();
    paintVitals();
    log("You wash up on the south beach. Right-click the bag. Left-click the world.");
    requestAnimationFrame(tick);
  }

  canvas.addEventListener("contextmenu", (event) => {
    event.preventDefault();
    if (!state) return;
    const w = worldFromEvent(event);
    if (w.x < 0 || w.y < 0 || w.x >= W || w.y >= H) return;
    worldMenu(w.x, w.y, event.clientX, event.clientY);
  });
  canvas.addEventListener("pointermove", (event) => {
    if (!state) return;
    const w = worldFromEvent(event);
    hover = (w.x >= 0 && w.y >= 0 && w.x < W && w.y < H) ? w : null;
    const first = hover ? inspect(hover.x, hover.y)[0] : null;
    canvas.style.cursor = first && first.kind !== "walk" ? "pointer" : "default";
  });
  canvas.addEventListener("pointerdown", (event) => {
    if (!state || event.button !== 0) return;
    closeMenus();
    const w = worldFromEvent(event);
    if (w.x < 0 || w.y < 0 || w.x >= W || w.y >= H) return;
    const actions = inspect(w.x, w.y);
    order(actions.find((a) => a.kind !== "look") || actions[0]);
  });

  addEventListener("keydown", (event) => {
    keys[event.code] = true;
    if (!state) return;
    if (event.code.startsWith("Digit")) {
      const n = event.key === "0" ? 9 : Number(event.key) - 1;
      if (n >= 0 && n < INV) { state.selected = n; paintInv(); }
    }
    if (event.code === "Escape") closeMenus();
  });
  addEventListener("keyup", (event) => { keys[event.code] = false; });
  addEventListener("pointerdown", (event) => {
    if (!event.target.closest("[data-ctx], [data-examine], [data-mini-inv]")) closeMenus();
  });
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) running = false;
    else if (state) { running = true; last = performance.now(); requestAnimationFrame(tick); }
  });
  addEventListener("resize", () => { resize(); if (state) draw(); });

  hud.inv?.addEventListener("click", (event) => {
    const btn = event.target.closest("[data-slot]");
    if (!btn || !state) return;
    state.selected = Number(btn.dataset.slot);
    paintInv();
  });
  hud.inv?.addEventListener("contextmenu", (event) => {
    event.preventDefault();
    if (!state) return;
    const btn = event.target.closest("[data-slot]");
    if (!btn) return;
    const slot = Number(btn.dataset.slot);
    state.selected = slot;
    paintInv();
    bagMenu(slot, event.clientX, event.clientY);
  });
  hud.inv?.addEventListener("dblclick", (event) => {
    const btn = event.target.closest("[data-slot]");
    if (!btn || !state) return;
    const slot = Number(btn.dataset.slot);
    const item = state.inv[slot];
    if (item && ITEMS[item.id].food) useSlot(slot, "eat");
  });
  hud.inv?.addEventListener("dragstart", (event) => {
    const btn = event.target.closest("[data-slot]");
    if (!btn) return;
    dragSlot = Number(btn.dataset.slot);
  });
  hud.inv?.addEventListener("dragover", (event) => event.preventDefault());
  hud.inv?.addEventListener("drop", (event) => {
    const btn = event.target.closest("[data-slot]");
    if (!btn || dragSlot < 0) return;
    const dest = Number(btn.dataset.slot);
    const a = state.inv[dragSlot];
    state.inv[dragSlot] = state.inv[dest];
    state.inv[dest] = a;
    state.selected = dest;
    dragSlot = -1;
    paintInv();
  });
  hud.craft?.addEventListener("click", (event) => {
    const btn = event.target.closest("[data-recipe]");
    if (!btn || !state) return;
    const recipe = RECIPES.find((r) => r.id === btn.dataset.recipe);
    if (recipe) craft(recipe);
  });
  hud.start?.addEventListener("click", () => {
    const seed = Number(hud.seed?.value);
    start(Number.isFinite(seed) ? seed : 2187);
  });

  resize();
  loadArt().then(() => {
    if (hud.cursor) hud.cursor.textContent = "Art loaded. Press Begin.";
  });
})();
