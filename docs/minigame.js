(() => {
  const canvas = document.querySelector("[data-mini-canvas]");
  if (!canvas) return;
  const ctx = canvas.getContext("2d");
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
    frame: document.querySelector("[data-mini-frame]")
  };

  const W = 30;
  const H = 30;
  const TW = 56;
  const TH = 28;
  const INV = 10;
  const RANGE = 1.55;

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
    campfire: { name: "Campfire", color: "#c47a3a" },
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
  let sprites = {};

  const hash = (x, y, s) => {
    let n = (x * 374761393 + y * 668265263 + s * 1274126177) | 0;
    n = (n ^ (n >>> 13)) * 1274126177;
    return ((n ^ (n >>> 16)) >>> 0) / 4294967296;
  };

  const walkable = (tile) => tile === "beach" || tile === "grass" || tile === "forest";

  function generate(seed) {
    const tiles = [];
    const trees = [];
    const bushes = [];
    const rocks = [];
    const items = [];
    const objects = [];
    let spawn = { x: 15, y: 24 };
    for (let y = 0; y < H; y++) {
      tiles[y] = [];
      for (let x = 0; x < W; x++) {
        const dx = x - 14.5;
        const dy = y - 14.5;
        const d = Math.hypot(dx, dy) + (hash(x, y, seed) - .5) * 1.6;
        let tile = "deep";
        if (d < 13.4) tile = "shallow";
        if (d < 11.6) tile = "beach";
        if (d < 10.1) tile = hash(x, y, seed + 3) > .78 ? "rock" : "grass";
        if (d < 8.2 && hash(x, y, seed + 7) > .42) tile = "forest";
        tiles[y][x] = tile;
        if (tile === "beach" && y > 18) spawn = { x, y };
        if (tile === "forest" && hash(x, y, seed + 11) > .55)
          trees.push({ x, y, hp: 8, kind: "tree" });
        if (tile === "grass" && hash(x, y, seed + 17) > .86)
          bushes.push({ x, y, hp: 3, kind: "bush" });
        if ((tile === "rock" || tile === "grass") && hash(x, y, seed + 23) > .9)
          rocks.push({ x, y, hp: 10, kind: "node" });
        if (tile === "beach" && hash(x, y, seed + 29) > .82)
          items.push({ x: x + .3, y: y + .2, id: "sticks", qty: 1 });
        if (tile === "grass" && hash(x, y, seed + 31) > .93)
          items.push({ x: x + .4, y: y + .35, id: "large_rock", qty: 1 });
      }
    }
    const slimeTile = firstOpen(tiles, 12, 12, 8);
    return {
      seed, tiles, trees, bushes, rocks, items, objects,
      player: { x: spawn.x + .5, y: spawn.y + .5, facing: 1, hp: 100, hunger: 100, busy: 0, path: [], act: null },
      slime: slimeTile ? { x: slimeTile.x + .5, y: slimeTile.y + .5, hp: 36, cd: 0, alive: true } : null,
      inv: Array(INV).fill(null),
      selected: 0,
      skills: Object.fromEntries(SKILL_NAMES.map((n) => [n, 0])),
      day: .28,
      log: [],
      quest: { fire: false, meal: false, slime: false }
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
    const occ = [...state.trees, ...state.rocks, ...state.objects];
    return occ.some((o) => o.x === tx && o.y === ty);
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
        const nx = x + dx, ny = y + dy;
        const key = `${nx},${ny}`;
        if (seen.has(key) || nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
        const dest = key === goal;
        if (!dest && blocked(nx + .5, ny + .5)) continue;
        if (dest && !walkable(state.tiles[ny]?.[nx]) && !nearWater(nx, ny)) {
          if (!entityAt(nx, ny) && !itemAt(nx, ny)) continue;
        }
        seen.set(key, [x, y]);
        q.push([nx, ny]);
      }
    }
    if (!seen.has(goal)) return [];
    const path = [];
    let cur = goal;
    while (cur && cur !== start) {
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

  function dist(a, b) {
    return Math.hypot(a.x - b.x, a.y - b.y);
  }

  function log(text) {
    state.log.unshift(text);
    state.log = state.log.slice(0, 4);
    paintLog();
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

  function dropSelected() {
    const slot = state.inv[state.selected];
    if (!slot) return;
    state.items.push({ x: state.player.x, y: state.player.y, id: slot.id, qty: 1 });
    slot.qty -= 1;
    if (slot.qty <= 0) state.inv[state.selected] = null;
    log(`You drop ${ITEMS[slot.id].name.toLowerCase()}.`);
    paintInv();
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
      log(recipe.fire && !litFireNear()
        ? "Cook that beside a lit campfire."
        : "You are missing ingredients.");
      return;
    }
    for (const [id, n] of Object.entries(recipe.need)) takeItem(id, n);
    if (!giveItem(recipe.out, recipe.qty)) {
      for (const [id, n] of Object.entries(recipe.need)) giveItem(id, n);
      log("Your pack is full.");
      return;
    }
    gain(recipe.skill, recipe.xp);
    if (recipe.fire) state.quest.meal = true;
    log(`You make ${ITEMS[recipe.out].name.toLowerCase()}.`);
    paintInv();
    paintCraft();
    paintSkills();
  }

  function gain(skill, xp) {
    state.skills[skill] += xp;
  }

  function inspect(tx, ty) {
    if (state.slime?.alive && Math.floor(state.slime.x) === tx && Math.floor(state.slime.y) === ty)
      return { kind: "attack", label: "Attack slime", x: tx, y: ty };
    const obj = state.objects.find((o) => o.x === tx && o.y === ty);
    if (obj?.kind === "campfire")
      return { kind: obj.lit ? "fuel" : "light", label: obj.lit ? "Add sticks" : "Light fire", x: tx, y: ty, obj };
    if (state.trees.some((t) => t.x === tx && t.y === ty))
      return { kind: "chop", label: "Chop tree", x: tx, y: ty };
    if (state.rocks.some((r) => r.x === tx && r.y === ty))
      return { kind: "mine", label: "Mine rock", x: tx, y: ty };
    if (state.bushes.some((b) => b.x === tx && b.y === ty))
      return { kind: "pick", label: "Pick berries", x: tx, y: ty };
    const ground = itemAt(tx, ty);
    if (ground) return { kind: "loot", label: `Take ${ITEMS[ground.id].name.toLowerCase()}`, x: tx, y: ty };
    const tile = state.tiles[ty]?.[tx];
    if ((tile === "shallow" || tile === "beach") && nearWater(tx, ty))
      return { kind: "fish", label: "Fish here", x: tx, y: ty };
    const held = state.inv[state.selected];
    if (held?.id === "campfire" && walkable(tile) && !entityAt(tx, ty))
      return { kind: "place", label: "Place campfire", x: tx, y: ty };
    if (walkable(tile)) return { kind: "walk", label: "Walk here", x: tx, y: ty };
    return { kind: "look", label: "Nothing to do", x: tx, y: ty };
  }

  function order(action) {
    if (!action || action.kind === "look") return;
    const stand = adjacentStand(action.x, action.y, action.kind === "walk" || action.kind === "place" || action.kind === "fish");
    if (!stand) { log("You cannot reach that."); return; }
    state.player.path = pathTo(state.player.x, state.player.y, stand.x, stand.y);
    state.player.act = action;
    if (!state.player.path.length) {
      if (dist(state.player, { x: action.x + .5, y: action.y + .5 }) <= RANGE + .35)
        tryAct();
      else {
        state.player.act = null;
        log("You cannot reach that.");
      }
    }
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

  function tryAct() {
    const act = state.player.act;
    if (!act) return;
    const target = { x: act.x + .5, y: act.y + .5 };
    if (dist(state.player, target) > RANGE + .35) return;
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
      const power = countItem("stone_axe") ? 3 : 1;
      tree.hp -= power;
      gain("woodcutting", 6);
      if (tree.hp <= 0) {
        state.trees = state.trees.filter((t) => t !== tree);
        state.items.push({ x: act.x + .3, y: act.y + .2, id: "logs", qty: 1 });
        state.items.push({ x: act.x + .6, y: act.y + .45, id: "sticks", qty: 2 });
        log("The tree falls. Logs and sticks drop.");
      } else log(countItem("stone_axe") ? "You hew the trunk." : "You break branches by hand.");
      paintSkills();
      return;
    }
    if (act.kind === "mine") {
      const node = state.rocks.find((r) => r.x === act.x && r.y === act.y);
      if (!node) return;
      const power = countItem("stone_pickaxe") ? 4 : 1;
      node.hp -= power;
      gain("mining", 7);
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
      gain("farming", 4);
      log("You pick wild berries.");
      if (bush.hp <= 0) state.bushes = state.bushes.filter((b) => b !== bush);
      paintInv();
      return;
    }
    if (act.kind === "fish") {
      if (!nearWater(act.x, act.y)) return;
      if (hash(act.x, act.y, (state.day * 1000) | 0) < .35) {
        log("The line comes up empty.");
        return;
      }
      if (!giveItem("raw_fish", 1)) { log("Your pack is full."); return; }
      gain("fishing", 9);
      log("You land a raw fish.");
      paintInv();
      paintSkills();
      return;
    }
    if (act.kind === "place") {
      const slot = state.inv[state.selected];
      if (slot?.id !== "campfire") return;
      takeItem("campfire", 1);
      state.objects.push({ x: act.x, y: act.y, kind: "campfire", fuel: 0, lit: false });
      log("You set a stone fire ring.");
      paintInv();
      return;
    }
    if (act.kind === "light" || act.kind === "fuel") {
      const fire = state.objects.find((o) => o.x === act.x && o.y === act.y);
      if (!fire) return;
      if (act.kind === "fuel" || (!fire.lit && countItem("sticks"))) {
        if (!takeItem("sticks", 1)) { log("You need sticks for fuel."); return; }
        fire.fuel += 18;
        log("You add sticks to the ring.");
        paintInv();
      }
      if (!fire.lit && fire.fuel > 0) {
        fire.lit = true;
        state.quest.fire = true;
        gain("firemaking", 15);
        log("The campfire catches.");
        paintSkills();
      }
      return;
    }
    if (act.kind === "attack" && state.slime?.alive) {
      state.player.busy = .55;
      state.slime.hp -= 7 + (countItem("stone_axe") ? 5 : 0);
      gain("attack", 8);
      log("You strike the slime.");
      if (state.slime.hp <= 0) {
        state.slime.alive = false;
        state.quest.slime = true;
        state.items.push({ x: state.slime.x, y: state.slime.y, id: "slime_gel", qty: 1 });
        log("The slime collapses. Gel drops.");
      }
      paintSkills();
    }
  }

  function eatSelected() {
    const slot = state.inv[state.selected];
    const food = slot && ITEMS[slot.id].food;
    if (!food) { log("That is not food."); return; }
    takeItem(slot.id, 1);
    state.player.hunger = Math.min(100, state.player.hunger + food);
    state.player.hp = Math.min(100, state.player.hp + food * .25);
    if (slot.id === "cooked_berries" || slot.id === "cooked_fish") state.quest.meal = true;
    log(`You eat ${ITEMS[slot.id].name.toLowerCase()}.`);
    paintInv();
    paintVitals();
  }

  function worldFromEvent(event) {
    const rect = canvas.getBoundingClientRect();
    const sx = (event.clientX - rect.left) * (canvas.width / rect.width);
    const sy = (event.clientY - rect.top) * (canvas.height / rect.height);
    const cam = camera();
    const ix = sx - canvas.width / 2 + cam.x;
    const iy = sy - canvas.height / 2 + cam.y;
    const wx = (ix / (TW / 2) + iy / (TH / 2)) / 2;
    const wy = (iy / (TH / 2) - ix / (TW / 2)) / 2;
    return { x: Math.floor(wx), y: Math.floor(wy) };
  }

  function camera() {
    return {
      x: (state.player.x - state.player.y) * (TW / 2),
      y: (state.player.x + state.player.y) * (TH / 2)
    };
  }

  function project(x, y) {
    const cam = camera();
    return {
      x: (x - y) * (TW / 2) - cam.x + canvas.width / 2,
      y: (x + y) * (TH / 2) - cam.y + canvas.height / 2
    };
  }

  function update(dt) {
    const p = state.player;
    p.busy = Math.max(0, p.busy - dt);
    state.day = (state.day + dt / 90) % 1;
    p.hunger = Math.max(0, p.hunger - dt * 1.15);
    if (p.hunger <= 0) p.hp = Math.max(0, p.hp - dt * 4);
    else if (p.hp < 100) p.hp = Math.min(100, p.hp + dt * 1.4);

    let mx = 0, my = 0;
    if (keys.KeyW || keys.ArrowUp) { mx -= 1; my -= 1; }
    if (keys.KeyS || keys.ArrowDown) { mx += 1; my += 1; }
    if (keys.KeyA || keys.ArrowLeft) { mx -= 1; my += 1; }
    if (keys.KeyD || keys.ArrowRight) { mx += 1; my -= 1; }
    if (mx || my) {
      p.path = [];
      p.act = null;
      const len = Math.hypot(mx, my) || 1;
      const nx = p.x + (mx / len) * dt * 3.1;
      const ny = p.y + (my / len) * dt * 3.1;
      if (!blocked(nx, p.y)) p.x = nx;
      if (!blocked(p.x, ny)) p.y = ny;
      p.facing = mx >= 0 ? 1 : -1;
    } else if (p.path.length) {
      const step = p.path[0];
      const dx = step.x - p.x, dy = step.y - p.y;
      const d = Math.hypot(dx, dy);
      if (d < .06) p.path.shift();
      else {
        p.x += (dx / d) * dt * 3.2;
        p.y += (dy / d) * dt * 3.2;
        p.facing = dx >= 0 ? 1 : -1;
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
      if (d < 6) {
        const ang = Math.atan2(p.y - s.y, p.x - s.x);
        const nx = s.x + Math.cos(ang) * dt * 1.35;
        const ny = s.y + Math.sin(ang) * dt * 1.35;
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
      const open = firstOpen(state.tiles, 15, 24, 6);
      if (open) { p.x = open.x + .5; p.y = open.y + .5; }
    }
    paintVitals();
  }

  function diamond(x, y, w, h, fill, stroke) {
    ctx.beginPath();
    ctx.moveTo(x, y - h / 2);
    ctx.lineTo(x + w / 2, y);
    ctx.lineTo(x, y + h / 2);
    ctx.lineTo(x - w / 2, y);
    ctx.closePath();
    ctx.fillStyle = fill;
    ctx.fill();
    if (stroke) { ctx.strokeStyle = stroke; ctx.lineWidth = 1; ctx.stroke(); }
  }

  function draw() {
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    const dusk = Math.sin(state.day * Math.PI * 2);
    const sky = dusk > 0
      ? `rgb(${70 + dusk * 40},${110 + dusk * 40},${140 + dusk * 20})`
      : `rgb(${18},${22 + (-dusk) * 8},${36})`;
    ctx.fillStyle = sky;
    ctx.fillRect(0, 0, canvas.width, canvas.height);

    for (let y = 0; y < H; y++)
      for (let x = 0; x < W; x++) {
        const p = project(x, y);
        if (p.x < -TW || p.x > canvas.width + TW || p.y < -TH || p.y > canvas.height + TH) continue;
        const tile = state.tiles[y][x];
        const fills = {
          deep: "#1a3a48",
          shallow: "#2b6a74",
          beach: "#c2a56a",
          grass: "#4f7a45",
          forest: "#355834",
          rock: "#6d7178"
        };
        diamond(p.x, p.y, TW, TH, fills[tile], "rgba(0,0,0,.18)");
        if (hover && hover.x === x && hover.y === y)
          diamond(p.x, p.y, TW, TH, "rgba(240,220,150,.22)");
      }

    const spritesList = [];
    for (const t of state.trees) spritesList.push({ ...t, sort: t.x + t.y, draw: drawTree });
    for (const b of state.bushes) spritesList.push({ ...b, sort: b.x + b.y, draw: drawBush });
    for (const r of state.rocks) spritesList.push({ ...r, sort: r.x + r.y, draw: drawNode });
    for (const o of state.objects) spritesList.push({ ...o, sort: o.x + o.y, draw: drawObject });
    for (const i of state.items) spritesList.push({ ...i, sort: i.x + i.y, draw: drawItem });
    spritesList.push({ ...state.player, sort: state.player.x + state.player.y, draw: drawPlayer });
    if (state.slime?.alive)
      spritesList.push({ ...state.slime, sort: state.slime.x + state.slime.y, draw: drawSlime });
    spritesList.sort((a, b) => a.sort - b.sort);
    for (const s of spritesList) s.draw(s);

    const night = dusk < 0 ? -dusk * .38 : 0;
    if (night > 0) {
      ctx.fillStyle = `rgba(6,8,16,${night})`;
      ctx.fillRect(0, 0, canvas.width, canvas.height);
      for (const o of state.objects) {
        if (!o.lit) continue;
        const p = project(o.x + .15, o.y + .15);
        const g = ctx.createRadialGradient(p.x, p.y, 4, p.x, p.y, 90);
        g.addColorStop(0, "rgba(255,170,70,.28)");
        g.addColorStop(1, "transparent");
        ctx.fillStyle = g;
        ctx.beginPath();
        ctx.arc(p.x, p.y, 90, 0, Math.PI * 2);
        ctx.fill();
      }
    }
    if (hud.cursor && hover) hud.cursor.textContent = inspect(hover.x, hover.y).label;
  }

  function drawTree(t) {
    const p = project(t.x + .15, t.y + .15);
    ctx.fillStyle = "#5a3a22";
    ctx.fillRect(p.x - 4, p.y - 6, 8, 16);
    ctx.beginPath();
    ctx.fillStyle = "#2f5a32";
    ctx.ellipse(p.x, p.y - 18, 16, 14, 0, 0, Math.PI * 2);
    ctx.fill();
    ctx.beginPath();
    ctx.fillStyle = "#3f7240";
    ctx.ellipse(p.x + 2, p.y - 26, 11, 10, 0, 0, Math.PI * 2);
    ctx.fill();
  }

  function drawBush(b) {
    const p = project(b.x + .2, b.y + .2);
    ctx.beginPath();
    ctx.fillStyle = "#3d6a38";
    ctx.ellipse(p.x, p.y, 12, 8, 0, 0, Math.PI * 2);
    ctx.fill();
    ctx.fillStyle = "#a33a55";
    ctx.fillRect(p.x - 4, p.y - 2, 3, 3);
    ctx.fillRect(p.x + 3, p.y + 1, 3, 3);
  }

  function drawNode(r) {
    const p = project(r.x + .2, r.y + .2);
    ctx.fillStyle = "#8b9098";
    ctx.beginPath();
    ctx.moveTo(p.x - 12, p.y + 4);
    ctx.lineTo(p.x - 4, p.y - 10);
    ctx.lineTo(p.x + 10, p.y - 6);
    ctx.lineTo(p.x + 12, p.y + 6);
    ctx.closePath();
    ctx.fill();
  }

  function drawObject(o) {
    const p = project(o.x + .15, o.y + .15);
    const img = sprites.campfire;
    if (img) {
      ctx.drawImage(img, p.x - 18, p.y - 22, 36, 28);
    } else {
      ctx.fillStyle = "#6b6f76";
      ctx.beginPath();
      ctx.arc(p.x, p.y, 10, 0, Math.PI * 2);
      ctx.fill();
    }
    if (o.lit) {
      ctx.fillStyle = `rgba(255,140,40,${.55 + Math.sin(performance.now() / 120) * .2})`;
      ctx.beginPath();
      ctx.moveTo(p.x, p.y - 22);
      ctx.lineTo(p.x - 7, p.y - 6);
      ctx.lineTo(p.x + 7, p.y - 6);
      ctx.fill();
    }
  }

  function drawItem(i) {
    const p = project(i.x, i.y);
    ctx.fillStyle = ITEMS[i.id]?.color || "#ddd";
    ctx.beginPath();
    ctx.arc(p.x, p.y, 5, 0, Math.PI * 2);
    ctx.fill();
    ctx.strokeStyle = "rgba(0,0,0,.4)";
    ctx.stroke();
  }

  function drawPlayer(pl) {
    const p = project(pl.x, pl.y);
    ctx.fillStyle = "#2a241c";
    ctx.beginPath();
    ctx.ellipse(p.x, p.y + 8, 8, 4, 0, 0, Math.PI * 2);
    ctx.fill();
    ctx.fillStyle = "#3f6d8a";
    ctx.fillRect(p.x - 6 * pl.facing, p.y - 16, 12, 18);
    ctx.fillStyle = "#e6c7a0";
    ctx.beginPath();
    ctx.arc(p.x, p.y - 20, 6, 0, Math.PI * 2);
    ctx.fill();
    ctx.fillStyle = "#1d1a16";
    ctx.fillRect(p.x - 6, p.y - 26, 12, 4);
  }

  function drawSlime(s) {
    const p = project(s.x, s.y);
    const bob = Math.sin(performance.now() / 180) * 2;
    ctx.fillStyle = "#67b85c";
    ctx.beginPath();
    ctx.ellipse(p.x, p.y + bob, 12, 9, 0, 0, Math.PI * 2);
    ctx.fill();
    ctx.fillStyle = "#163016";
    ctx.beginPath();
    ctx.arc(p.x - 4, p.y - 1 + bob, 2, 0, Math.PI * 2);
    ctx.arc(p.x + 4, p.y - 1 + bob, 2, 0, Math.PI * 2);
    ctx.fill();
  }

  function paintLog() {
    if (!hud.log) return;
    hud.log.innerHTML = state.log.map((line) => `<p>${line}</p>`).join("")
      || "<p>Click the island to walk. Click trees, rocks, bushes, water and the slime.</p>";
  }

  function paintInv() {
    if (!hud.inv) return;
    paintCraft();
    hud.inv.innerHTML = state.inv.map((slot, i) => {
      const on = i === state.selected ? " on" : "";
      if (!slot) return `<button type="button" class="mini-slot${on}" data-slot="${i}" aria-label="Empty slot ${i + 1}"></button>`;
      return `<button type="button" class="mini-slot${on}" data-slot="${i}" title="${ITEMS[slot.id].name}">
        <span class="swatch" style="background:${ITEMS[slot.id].color}"></span>
        <span class="qty">${slot.qty}</span>
        <span class="name">${ITEMS[slot.id].name}</span>
      </button>`;
    }).join("");
  }

  function paintCraft() {
    if (!hud.craft) return;
    hud.craft.innerHTML = RECIPES.map((recipe) => {
      const ok = canCraft(recipe);
      const need = Object.entries(recipe.need).map(([id, n]) => `${n} ${ITEMS[id].name.toLowerCase()}`).join(", ");
      return `<button type="button" class="mini-recipe${ok ? "" : " off"}" data-recipe="${recipe.id}" ${ok ? "" : "disabled"}>
        <strong>${recipe.name}</strong>
        <span>${need}${recipe.fire ? " · lit fire" : ""}</span>
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
    if (hud.clock) {
      const hour = Math.floor(state.day * 24);
      hud.clock.textContent = `${String(hour).padStart(2, "0")}:00`;
    }
  }

  function tick(now) {
    if (!running) return;
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
    hud.frame?.classList.add("live");
    paintLog();
    paintInv();
    paintCraft();
    paintSkills();
    paintVitals();
    log("You wash up on the south beach. Gather, craft an axe, light a fire.");
    requestAnimationFrame(tick);
  }

  canvas.addEventListener("pointermove", (event) => {
    if (!state) return;
    const w = worldFromEvent(event);
    hover = (w.x >= 0 && w.y >= 0 && w.x < W && w.y < H) ? w : null;
  });
  canvas.addEventListener("pointerdown", (event) => {
    if (!state) return;
    const w = worldFromEvent(event);
    if (w.x < 0 || w.y < 0 || w.x >= W || w.y >= H) return;
    order(inspect(w.x, w.y));
  });
  addEventListener("keydown", (event) => {
    keys[event.code] = true;
    if (!state) return;
    if (event.code.startsWith("Digit")) {
      const n = event.key === "0" ? 9 : Number(event.key) - 1;
      if (n >= 0 && n < INV) { state.selected = n; paintInv(); }
    }
    if (event.code === "KeyE") eatSelected();
    if (event.code === "KeyQ") dropSelected();
    if (event.code === "KeyC") hud.craft?.classList.toggle("open");
  });
  addEventListener("keyup", (event) => { keys[event.code] = false; });
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) running = false;
    else if (state) { running = true; last = performance.now(); requestAnimationFrame(tick); }
  });
  hud.inv?.addEventListener("click", (event) => {
    const btn = event.target.closest("[data-slot]");
    if (!btn || !state) return;
    state.selected = Number(btn.dataset.slot);
    paintInv();
  });
  hud.inv?.addEventListener("dblclick", eatSelected);
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

  ["campfire", "stone-pickaxe-item"].forEach((name) => {
    const img = new Image();
    img.src = `assets/${name === "stone-pickaxe-item" ? "stone-pickaxe-item" : name}.png`;
    img.onload = () => { sprites[name === "stone-pickaxe-item" ? "pick" : "campfire"] = img; };
  });
})();
