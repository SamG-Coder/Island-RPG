namespace IslandRpg.Gameplay;

internal static class MedievalDemographics
{
    private static readonly string[] MaleNames =
    [
        "Adam", "Aldric", "Ansel", "Baldwin", "Bennet", "Bertram", "Conrad", "Crispin",
        "Edmund", "Everard", "Felix", "Fulk", "Gareth", "Gavin", "Gerard", "Giles",
        "Godfrey", "Henry", "Hugh", "Ivo", "Lambert", "Leofric", "Martin", "Nicholas",
        "Osric", "Owen", "Peter", "Philip", "Piers", "Ralph", "Reynard", "Richard",
        "Robert", "Roger", "Simon", "Stephen", "Theobald", "Thomas", "Tristan", "Walter",
        "Warin", "Wilfred", "William", "Wulfric", "Geoffrey", "Gilbert", "Osbert", "Ranulf",
        "Reginald", "Wymond"
    ];

    private static readonly string[] FemaleNames =
    [
        "Adela", "Agnes", "Alina", "Amabel", "Aveline", "Beatrice", "Blanche", "Branwen",
        "Cecily", "Clara", "Edith", "Eleanor", "Emeline", "Ethel", "Gisela", "Heloise",
        "Ida", "Isolde", "Joan", "Jocelyn", "Juliana", "Linnet", "Lucia", "Mabel",
        "Margery", "Matilda", "Maud", "Muriel", "Nesta", "Odilia", "Petronilla", "Rosamund",
        "Sabine", "Serena", "Sibyl", "Sybil", "Theodora", "Ursula", "Winifred", "Ysabel",
        "Alice", "Avice", "Christina", "Emma", "Hawise", "Isabella", "Leticia", "Millicent",
        "Philippa", "Yvette"
    ];

    private static readonly string[] MaleTrades =
    [
        "Carpenter", "Quarry worker", "Woodcutter", "Mason", "Smith", "Fisher",
        "Shepherd", "Farmer", "Hunter", "Cooper", "Miller", "Sailor"
    ];

    private static readonly string[] FemaleTrades =
    [
        "Alewife", "Spinner", "Weaver", "Dairy worker", "Baker", "Cook",
        "Herbalist", "Healer", "Fisher", "Shepherd", "Farmer", "Market trader"
    ];

    public static int NameCount => MaleNames.Length + FemaleNames.Length;

    public static EntityGender[] GendersForPopulation(int population, long seed)
    {
        population = Math.Clamp(population, 0, VillagerSimulation.MaximumPopulation);
        var first = PositiveMod(Hash(seed, 1543), 2) == 0
            ? EntityGender.Female
            : EntityGender.Male;
        return Enumerable.Range(0, population)
            .Select(index => index % 2 == 0
                ? first
                : Opposite(first))
            .ToArray();
    }

    public static IReadOnlyList<string> NamesForPopulation(int population, long seed)
    {
        var genders = GendersForPopulation(population, seed);
        var maleStart = PositiveMod(Hash(seed, 3253), MaleNames.Length);
        var femaleStart = PositiveMod(Hash(seed, 5279), FemaleNames.Length);
        var maleIndex = 0;
        var femaleIndex = 0;
        var result = new string[genders.Length];
        for (var index = 0; index < result.Length; index++)
            result[index] = genders[index] == EntityGender.Male
                ? MaleNames[(maleStart + maleIndex++ * 17) % MaleNames.Length]
                : FemaleNames[(femaleStart + femaleIndex++ * 17) % FemaleNames.Length];
        return result;
    }

    public static bool IsNameCompatible(string name, EntityGender gender) =>
        Names(gender).Contains(name, StringComparer.OrdinalIgnoreCase);

    public static bool TryGenderForName(
        string name, out EntityGender gender)
    {
        if (FemaleNames.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            gender = EntityGender.Female;
            return true;
        }
        if (MaleNames.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            gender = EntityGender.Male;
            return true;
        }
        gender = default;
        return false;
    }

    public static EntityGender[] GendersForNames(
        IReadOnlyList<string> names, long seed)
    {
        var result = GendersForPopulation(names.Count, seed);
        for (var index = 0; index < result.Length; index++)
            if (TryGenderForName(names[index], out var gender))
                result[index] = gender;
        return result;
    }

    public static string TradeFor(EntityGender gender, int index, long seed = 0)
    {
        var trades = Trades(gender);
        return trades[PositiveMod(Hash(seed, index + 7919), trades.Length)];
    }

    public static bool IsTradeCompatible(string trade, EntityGender gender) =>
        Trades(gender).Contains(trade.Trim(), StringComparer.OrdinalIgnoreCase);

    public static string AllowedTrades(EntityGender gender) =>
        string.Join(", ", Trades(gender));

    private static string[] Names(EntityGender gender) =>
        gender == EntityGender.Female ? FemaleNames : MaleNames;

    private static string[] Trades(EntityGender gender) =>
        gender == EntityGender.Female ? FemaleTrades : MaleTrades;

    private static EntityGender Opposite(EntityGender gender) =>
        gender == EntityGender.Female ? EntityGender.Male : EntityGender.Female;

    private static int Hash(long seed, int salt)
    {
        unchecked
        {
            var value = (uint)(seed ^ (seed >> 32));
            value ^= (uint)salt * 0x9E3779B9u;
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            return (int)(value & 0x7fffffff);
        }
    }

    private static int PositiveMod(int value, int modulus) =>
        (value % modulus + modulus) % modulus;
}
