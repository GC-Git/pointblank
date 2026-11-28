using System.Runtime.ExceptionServices;

namespace PointBlank.Sim.Tests;

/// <summary>
/// Runs independent seeds of a battle on every core (item 2.12). Only for bodies that build their own world per seed and
/// write no shared state; the sim itself stays single-threaded. Results come back in seed order, so reports and
/// statistics are the same as a serial loop's. Every seed runs even when one fails, and the failure reported is always
/// the lowest failing seed, so a red run reads the same every time.
/// </summary>
internal static class SeedRuns
{
    /// <summary>Seeds 1 to <paramref name="count"/>.</summary>
    public static List<T> Map<T>(int count, Func<ulong, T> body)
    {
        var results = new T[count];
        var errors = new Exception?[count];
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            try
            {
                results[i] = body((ulong)(i + 1));
            }
            catch (Exception e)
            {
                errors[i] = e;
            }
        });
        var failed = Enumerable.Range(0, count).Where(i => errors[i] != null).ToList();
        if (failed.Count > 0)
        {
            var first = errors[failed[0]]!;
            var failure = new SeedFailedException(
                $"seed {failed[0] + 1} failed ({failed.Count} of {count} seeds failed: {string.Join(", ", failed.Take(20).Select(i => i + 1))}): {first.Message}", first);
            ExceptionDispatchInfo.Throw(failure);
        }

        return [.. results];
    }

    /// <summary>Seeds 1 to <paramref name="count"/>, assertions only.</summary>
    public static void ForEach(int count, Action<ulong> body) => Map(count, seed =>
    {
        body(seed);
        return true;
    });
}

internal sealed class SeedFailedException(string message, Exception inner) : Exception(message, inner);
