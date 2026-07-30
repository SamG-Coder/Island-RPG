using Microsoft.Win32;

namespace IslandRpg.Assets;

internal static class Age2InstallLocator
{
    private const string RelativeExe = "AoK HD.exe";

    public static string Find(string? explicitPath)
    {
        return TryFind(explicitPath, out var install)
            ? install
            : throw new DirectoryNotFoundException(
                "Age2HD was not found. Pass --age2-path or set AGE2HD_PATH.");
    }

    public static bool TryFind(string? explicitPath, out string install)
    {
        var candidates = new List<string?>();
        if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(explicitPath);
        candidates.Add(Environment.GetEnvironmentVariable("AGE2HD_PATH"));
        candidates.Add(@"C:\Program Files (x86)\Steam\steamapps\common\Age2HD");
        candidates.Add(@"C:\Program Files\Steam\steamapps\common\Age2HD");

        var steamPath = OperatingSystem.IsWindows()
            ? Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
            : null;
        if (steamPath is not null)
        {
            candidates.Add(Path.Combine(steamPath, "steamapps", "common", "Age2HD"));
            candidates.AddRange(ReadLibraryFolders(Path.Combine(steamPath, "steamapps", "libraryfolders.vdf")));
        }

        var found = candidates.Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(p!))
            .FirstOrDefault(p => File.Exists(Path.Combine(p, RelativeExe)));
        install = found ?? string.Empty;
        return found is not null;
    }

    private static IEnumerable<string> ReadLibraryFolders(string vdf)
    {
        if (!File.Exists(vdf)) yield break;
        foreach (var line in File.ReadLines(vdf))
        {
            var marker = "\"path\"";
            var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var value = line[(index + marker.Length)..].Trim().Trim('"').Replace(@"\\", @"\");
            if (value.Length > 0) yield return Path.Combine(value, "steamapps", "common", "Age2HD");
        }
    }
}
