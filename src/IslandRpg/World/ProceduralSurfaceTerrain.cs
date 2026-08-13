namespace IslandRpg.World;

/// <summary>
/// Coordinate-stable surface sampling shared by chunk generation, client
/// presentation and the headless authority. It intentionally owns no chunks,
/// persistence, textures or render resources.
/// </summary>
internal static class ProceduralSurfaceTerrain
{
    private const int IslandCellSize = 192;

    internal enum Material
    {
        DeepWater,
        ShallowWater,
        RiverWater,
        MangroveShallows,
        Beach,
        Grassland,
        DryGrass,
        Mud,
        Forest,
        JungleFloor,
        Highland,
        Rock,
        Tundra,
        Snow,
        DesertSand,
        CrackedEarth
    }

    internal enum Region
    {
        Ocean,
        Coast,
        River,
        Wetland,
        TemperateGrassland,
        TemperateForest,
        Rainforest,
        Savanna,
        Desert,
        Taiga,
        Tundra,
        Alpine
    }

    internal readonly record struct Classification(
        Material Material,
        Region Region);

    internal static Classification ClassifyAt(
        long seed,
        int x,
        int y)
    {
        var average =
            (RawHeightAt(seed, x, y) +
             RawHeightAt(seed, x + 1, y) +
             RawHeightAt(seed, x + 1, y + 1) +
             RawHeightAt(seed, x, y + 1)) / 4f;
        return ClassifyAt(seed, x, y, average);
    }

    internal static byte RawHeightAt(long seed, int x, int y)
    {
        var elevation = BaseElevationAt(seed, x, y);
        var drainage = MacroHydrology.At(seed, x, y);
        if (elevation > .35f)
        {
            var channelCarve =
                drainage.River * MathF.Min(6.5f, elevation - .25f);
            var lakeCarve =
                drainage.Lake * MathF.Min(3.2f, elevation - .2f);
            elevation -= Math.Max(channelCarve, lakeCarve);
        }
        return (byte)Math.Clamp((int)MathF.Floor(elevation), 0, 22);
    }

    internal static byte SampleSurfaceHeight(long seed, int x, int y) =>
        Surface(RawHeightAt(seed, x, y));

    internal static byte RawSurfaceHeightAt(long seed, int x, int y) =>
        Surface(RawHeightAt(seed, x, y));

    internal static float SampleRenderedHeight(long seed, float x, float y)
    {
        var tileX = (int)MathF.Floor(x);
        var tileY = (int)MathF.Floor(y);
        var fractionX = x - tileX;
        var fractionY = y - tileY;
        var northWest = SmoothedVertex(tileX, tileY);
        var northEast = SmoothedVertex(tileX + 1, tileY);
        var southWest = SmoothedVertex(tileX, tileY + 1);
        var southEast = SmoothedVertex(tileX + 1, tileY + 1);
        var north = Lerp(northWest, northEast, fractionX);
        var south = Lerp(southWest, southEast, fractionX);
        return Lerp(north, south, fractionY);

        float SmoothedVertex(int vertexX, int vertexY)
        {
            var weightedHeight = 0f;
            var totalWeight = 0f;
            for (var offsetY = -1; offsetY <= 1; offsetY++)
            for (var offsetX = -1; offsetX <= 1; offsetX++)
            {
                var weight = (offsetX == 0 ? 2 : 1) *
                             (offsetY == 0 ? 2 : 1);
                weightedHeight += SampleSurfaceHeight(
                    seed, vertexX + offsetX, vertexY + offsetY) * weight;
                totalWeight += weight;
            }
            return weightedHeight / totalWeight;
        }
    }

    internal static float BaseElevationAt(long seed, int x, int y)
    {
        var continental = FractalNoise(
            seed ^ 0x6a09e667f3bcc909L,
            x / 720f,
            y / 720f,
            4);
        var continentalDetail = FractalNoise(
            seed ^ unchecked((long)0xbb67ae8584caa73bUL),
            x / 280f,
            y / 280f,
            3);
        var continentHeight =
            (continental + continentalDetail * .22f + .12f) * 5.4f;

        var cellX = FloorDiv(x, IslandCellSize);
        var cellY = FloorDiv(y, IslandCellSize);
        var island = -1f;
        for (var cy = cellY - 1; cy <= cellY + 1; cy++)
        for (var cx = cellX - 1; cx <= cellX + 1; cx++)
        {
            var centerX =
                (cx + .18f + UnitHash(seed, cx, cy, 11) * .64f) *
                IslandCellSize;
            var centerY =
                (cy + .18f + UnitHash(seed, cx, cy, 17) * .64f) *
                IslandCellSize;
            var radiusX =
                IslandCellSize *
                (.25f + UnitHash(seed, cx, cy, 23) * .20f);
            var radiusY =
                IslandCellSize *
                (.23f + UnitHash(seed, cx, cy, 29) * .19f);
            var deltaX = (x - centerX) / radiusX;
            var deltaY = (y - centerY) / radiusY;
            var distance = MathF.Sqrt(
                deltaX * deltaX + deltaY * deltaY);
            var warp = FractalNoise(
                seed ^ 0x243f6a8885a308d3L,
                x / 48f,
                y / 48f,
                3) * .28f;
            island = MathF.Max(island, 1f - distance + warp);
        }

        var islandHeight = (island - .08f) * 7.2f;
        var (rangeRamp, mountainCore) = MountainProfileAt(seed, x, y);
        var mountainGate =
            Math.Clamp((continental + .15f) * 1.7f, 0, 1);
        var passNoise = FractalNoise(
            seed ^ 0x428a2f98d728ae22L,
            x / 115f,
            y / 115f,
            2);
        var passCut = Math.Clamp((passNoise - .42f) * 2.3f, 0, .72f);
        var mountains =
            mountainCore * mountainGate * 12.5f * (1f - passCut);
        var foothills =
            rangeRamp * mountainGate * 6f * (1f - passCut * .55f);
        var hillNoise = MathF.Max(
            0,
            FractalNoise(
                seed ^ 0x7137449123ef65cdL,
                x / 92f,
                y / 92f,
                3));
        var hills = hillNoise * hillNoise *
                    Math.Clamp((continental + .3f) * 1.25f, 0, 1) *
                    2.6f;
        var detail = FractalNoise(
            seed ^ 0x13198a2e03707344L,
            x / 22f,
            y / 22f,
            3) * .8f;
        return MathF.Max(continentHeight, islandHeight) +
               mountains + foothills + hills + detail;
    }

    internal static float RainfallAt(long seed, int x, int y)
    {
        var broad = FractalNoise(
            seed ^ 0x5deece66dL,
            x / 430f,
            y / 430f,
            4);
        var detail = FractalNoise(
            seed ^ unchecked((long)0xa54ff53a5f1d36f1UL),
            x / 105f,
            y / 105f,
            2);
        var windAngle = UnitHash(seed, 0, 0, 557) * MathF.Tau;
        var windX = MathF.Cos(windAngle);
        var windY = MathF.Sin(windAngle);
        var localElevation = BaseElevationAt(seed, x, y);
        var upwindNear = BaseElevationAt(
            seed,
            (int)(x - windX * 72),
            (int)(y - windY * 72));
        var upwindFar = BaseElevationAt(
            seed,
            (int)(x - windX * 152),
            (int)(y - windY * 152));
        var barrier = MathF.Max(upwindNear, upwindFar) - localElevation;
        var rainShadow = Math.Clamp(barrier * .045f, 0, .48f);
        var oceanMoisture = upwindFar < .5f ? .16f : 0;
        return Math.Clamp(
            .65f + broad * .28f + detail * .12f +
            oceanMoisture - rainShadow,
            .10f,
            1.2f);
    }

    internal static Classification ClassifyAt(
        long seed,
        int x,
        int y,
        float elevation)
    {
        var baseElevation = BaseElevationAt(seed, x, y);
        if (baseElevation < -.35f)
            return new(Material.DeepWater, Region.Ocean);
        if (baseElevation < .9f)
            return new(Material.ShallowWater, Region.Ocean);

        var drainage = MacroHydrology.At(seed, x, y);
        var river = drainage.River;
        var continental = FractalNoise(
            seed ^ 0x6a09e667f3bcc909L,
            x / 720f,
            y / 720f,
            4);
        if (drainage.Lake > .48f && elevation < 5.5f)
        {
            var warmBand =
                MathF.Sin((y + seed % 10_000) / 1450f) > -.05f;
            var coastalMangrove =
                baseElevation < 1.7f && warmBand &&
                RainfallAt(seed, x, y) > .72f;
            return new(
                coastalMangrove
                    ? Material.MangroveShallows
                    : Material.RiverWater,
                Region.Wetland);
        }
        if (river > .48f && continental > -.18f)
            return new(Material.RiverWater, Region.River);
        if (elevation < 1.45f)
            return new(Material.Beach, Region.Coast);

        var moisture = Math.Clamp(
            .5f +
            FractalNoise(
                seed ^ 0x5deece66dL,
                x / 430f,
                y / 430f,
                4) * .34f +
            FractalNoise(
                seed ^ unchecked((long)0xa54ff53a5f1d36f1UL),
                x / 105f,
                y / 105f,
                2) * .16f +
            river * .24f,
            0,
            1);
        var climateBand = MathF.Sin((y + seed % 10_000) / 1450f);
        var temperature = Math.Clamp(
            .55f + climateBand * .24f +
            FractalNoise(
                seed ^ 0x510e527fade682d1L,
                x / 610f,
                y / 610f,
                3) * .22f -
            MathF.Max(0, elevation - 3) * .032f,
            0,
            1);

        if (elevation > 13f)
            return temperature < .43f && moisture > .34f
                ? new(Material.Snow, Region.Alpine)
                : new(Material.Rock, Region.Alpine);
        if (elevation > 9f)
            return temperature < .30f && moisture > .42f
                ? new(Material.Snow, Region.Alpine)
                : new(Material.Rock, Region.Alpine);
        if (elevation > 6f)
            return temperature < .24f && moisture > .48f
                ? new(Material.Snow, Region.Alpine)
                : new(Material.Highland, Region.TemperateGrassland);
        if (temperature < .20f)
            return new(Material.Tundra, Region.Tundra);
        if (temperature < .36f)
            return moisture > .43f
                ? new(Material.Forest, Region.Taiga)
                : new(Material.Tundra, Region.Tundra);
        if (moisture < .18f && temperature > .58f)
            return new(Material.CrackedEarth, Region.Desert);
        if (moisture < .30f && temperature > .5f)
            return new(Material.DesertSand, Region.Desert);
        if (moisture < .43f && temperature > .55f)
            return new(Material.DryGrass, Region.Savanna);
        if (river > .24f && moisture > .62f)
            return new(Material.Mud, Region.Wetland);
        if (moisture > .72f && temperature > .58f)
            return new(Material.JungleFloor, Region.Rainforest);
        if (moisture > .53f)
            return new(Material.Forest, Region.TemperateForest);
        return new(Material.Grassland, Region.TemperateGrassland);
    }

    private static (float Ramp, float Core) MountainProfileAt(
        long seed,
        int x,
        int y)
    {
        const int rangeCellSize = 768;
        var warpedX = x + FractalNoise(
            seed ^ 0x3c6ef372fe94f82bL,
            x / 310f,
            y / 310f,
            3) * 42;
        var warpedY = y + FractalNoise(
            seed ^ 0x428a2f98d728ae22L,
            x / 310f,
            y / 310f,
            3) * 42;
        var cellX = FloorDiv(x, rangeCellSize);
        var cellY = FloorDiv(y, rangeCellSize);
        var ramp = 0f;
        var core = 0f;
        for (var cy = cellY - 1; cy <= cellY + 1; cy++)
        for (var cx = cellX - 1; cx <= cellX + 1; cx++)
        {
            var centerX =
                (cx + .5f +
                 (UnitHash(seed, cx, cy, 401) - .5f) * .34f) *
                rangeCellSize;
            var centerY =
                (cy + .5f +
                 (UnitHash(seed, cx, cy, 409) - .5f) * .34f) *
                rangeCellSize;
            var angle = UnitHash(seed, cx, cy, 419) * MathF.PI;
            var halfLength =
                300 + UnitHash(seed, cx, cy, 421) * 250;
            var halfWidth =
                125 + UnitHash(seed, cx, cy, 431) * 105;
            var axisX = MathF.Cos(angle);
            var axisY = MathF.Sin(angle);
            var relativeX = warpedX - centerX;
            var relativeY = warpedY - centerY;
            var along = Math.Clamp(
                relativeX * axisX + relativeY * axisY,
                -halfLength,
                halfLength);
            var nearestX = centerX + axisX * along;
            var nearestY = centerY + axisY * along;
            var distance = MathF.Sqrt(
                (warpedX - nearestX) * (warpedX - nearestX) +
                (warpedY - nearestY) * (warpedY - nearestY));
            var normalized = distance / halfWidth;
            ramp = Math.Max(
                ramp,
                1f - SmoothStep(.15f, 1f, normalized));
            core = Math.Max(
                core,
                1f - SmoothStep(.05f, .34f, normalized));
        }
        return (ramp, core);
    }

    private static byte Surface(byte height) =>
        height <= 2 ? (byte)0 : height;

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        var amount = Math.Clamp(
            (value - edge0) / (edge1 - edge0),
            0,
            1);
        return amount * amount * (3 - 2 * amount);
    }

    private static float FractalNoise(
        long seed,
        float x,
        float y,
        int octaves)
    {
        var value = 0f;
        var amplitude = 1f;
        var total = 0f;
        for (var octave = 0; octave < octaves; octave++)
        {
            value += ValueNoise(seed + octave * 1013, x, y) * amplitude;
            total += amplitude;
            amplitude *= .5f;
            x *= 2.03f;
            y *= 2.03f;
        }
        return value / total * 2f - 1f;
    }

    private static float ValueNoise(long seed, float x, float y)
    {
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var fractionX = x - x0;
        var fractionY = y - y0;
        fractionX = fractionX * fractionX * (3 - 2 * fractionX);
        fractionY = fractionY * fractionY * (3 - 2 * fractionY);
        var northWest = UnitHash(seed, x0, y0, 0);
        var northEast = UnitHash(seed, x0 + 1, y0, 0);
        var southWest = UnitHash(seed, x0, y0 + 1, 0);
        var southEast = UnitHash(seed, x0 + 1, y0 + 1, 0);
        return Lerp(
            Lerp(northWest, northEast, fractionX),
            Lerp(southWest, southEast, fractionX),
            fractionY);
    }

    private static float UnitHash(long seed, int x, int y, int salt)
    {
        unchecked
        {
            var value =
                (ulong)seed ^
                (ulong)(long)x * 0x9e3779b185ebca87UL ^
                (ulong)(long)y * 0xc2b2ae3d27d4eb4fUL ^
                (uint)salt;
            value ^= value >> 30;
            value *= 0xbf58476d1ce4e5b9UL;
            value ^= value >> 27;
            value *= 0x94d049bb133111ebUL;
            value ^= value >> 31;
            return (value >> 40) / 16777216f;
        }
    }

    private static int FloorDiv(int value, int divisor)
    {
        var quotient = value / divisor;
        return value < 0 && value % divisor != 0
            ? quotient - 1
            : quotient;
    }

    private static float Lerp(float first, float second, float amount) =>
        first + (second - first) * amount;
}
