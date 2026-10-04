using System.Runtime.InteropServices;

using AttcksMergeTool.Models;

namespace AttcksMergeTool.Services;

/// <summary>One voice clip placed on a scene's timeline.</summary>
/// <param name="StartMs">Offset from the start of the encoded scene, so after any trim.</param>
public readonly record struct VoicePlacement(string Path, int StartMs, int DurationMs)
{
    public int EndMs => StartMs + DurationMs;
}

/// <summary>
/// Decides which voice clips play when inside one scene: random clips separated by random
/// gaps, back to back until the scene runs out of room.
/// </summary>
public static class VoicePlanner
{
    /// <summary>
    /// How many clips in a row may turn out too long for the time left before the scene is
    /// considered full. Without a limit a pool of nothing but long clips would be probed end
    /// to end looking for one that fits.
    /// </summary>
    public const int MaxConsecutiveMisses = 8;

    /// <param name="pool">The clips to draw from, as full paths.</param>
    /// <param name="windowMs">How long the scene is once trimmed. Nothing is placed past it.</param>
    /// <param name="durationOf">How long a clip plays, or <c>null</c> when it cannot be read.</param>
    public static async Task<IReadOnlyList<VoicePlacement>> PlanAsync(
        IReadOnlyList<string> pool,
        int windowMs,
        VoiceInjection settings,
        Random random,
        Func<string, CancellationToken, Task<int?>> durationOf,
        CancellationToken cancellationToken = default) {
        var placements = new List<VoicePlacement>();

        if (pool.Count == 0 || windowMs <= 0) return placements;

        int minGapMs = (int)Math.Max(0, settings.MinGapSeconds * 1000);
        int maxGapMs = Math.Max(minGapMs, (int)(settings.MaxGapSeconds * 1000));

        int NextGap() => random.Next(minGapMs, maxGapMs + 1);

        var unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bag = new List<string>();
        int next = 0;
        int misses = 0;
        string? last = null;

        // Opens on a gap too, so a scene does not always start mid-sentence.
        int cursor = NextGap();

        while (cursor < windowMs && misses < MaxConsecutiveMisses) {
            cancellationToken.ThrowIfCancellationRequested();

            if (next >= bag.Count) {
                // Drawn without replacement, so nothing repeats until the whole pool has played.
                bag = [.. pool.Where(path => !unreadable.Contains(path))];
                if (bag.Count == 0) break;

                random.Shuffle(CollectionsMarshal.AsSpan(bag));

                // A reshuffle could put the clip that just played straight back on.
                if (bag.Count > 1 && string.Equals(bag[0], last, StringComparison.OrdinalIgnoreCase)) {
                    (bag[0], bag[^1]) = (bag[^1], bag[0]);
                }

                next = 0;
            }

            string path = bag[next++];
            int? durationMs = await durationOf(path, cancellationToken);

            if (durationMs is not > 0) {
                unreadable.Add(path);
                continue;
            }

            // Only whole clips: one cut off at the scene's end would stop mid-word.
            if (cursor + durationMs.Value > windowMs) {
                misses++;
                continue;
            }

            placements.Add(new VoicePlacement(path, cursor, durationMs.Value));
            last = path;
            misses = 0;
            cursor += durationMs.Value + NextGap();
        }

        return placements;
    }
}
