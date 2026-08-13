using System.Diagnostics;

namespace IslandRpg.NetworkingChecks;

internal sealed class CheckRunner
{
    private readonly List<CheckCase> _checks = [];

    public void Add(string name, Action check)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(check);
        _checks.Add(new CheckCase(
            name,
            _ =>
            {
                check();
                return ValueTask.CompletedTask;
            }));
    }

    public void Add(string name, Func<CancellationToken, ValueTask> check)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(check);
        _checks.Add(new CheckCase(name, check));
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var filter = Environment.GetEnvironmentVariable("ISLAND_RPG_CHECK_FILTER");
        var failures = 0;
        var executed = 0;
        var timer = Stopwatch.StartNew();

        foreach (var check in _checks)
        {
            if (!string.IsNullOrEmpty(filter) &&
                !check.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            executed++;
            cancellationToken.ThrowIfCancellationRequested();
            var checkTimer = Stopwatch.StartNew();
            try
            {
                await check.Execute(cancellationToken);
                Console.WriteLine(
                    $"PASS {check.Name} ({checkTimer.ElapsedMilliseconds} ms)");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine(
                    $"FAIL {check.Name}: {exception.Message}");
            }
        }

        Console.WriteLine(
            $"Networking checks: {executed - failures}/{executed} passed " +
            $"in {timer.ElapsedMilliseconds} ms.");
        return failures == 0 ? 0 : 1;
    }

    private sealed record CheckCase(
        string Name,
        Func<CancellationToken, ValueTask> Execute);
}

internal static class CheckAssert
{
    public static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static void False(bool value, string message) =>
        True(!value, message);

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"{message} Expected: {expected}; actual: {actual}.");
    }

    public static void SequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string message)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException(message);
    }

    public static TException Throws<TException>(
        Action action,
        string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException(message);
    }
}
