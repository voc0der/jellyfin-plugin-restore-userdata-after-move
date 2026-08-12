namespace Jellyfin.Plugin.UserDataRestore.Sweep;

/// <summary>
/// Counter-based deterministic draws, so a population is a pure function of
/// <c>(seed, entity, property)</c> rather than of the order things were generated
/// in.
/// </summary>
/// <remarks>
/// <para>A single sequential <see cref="Random"/> makes sweeps incomparable: raising
/// the duplication probability consumes a different number of draws, so every
/// later title gets different provider IDs too, and the curve mixes the effect
/// being measured with a reshuffled population.</para>
/// <para>Each property of each entity therefore gets its own stream. Changing one
/// parameter changes only the draws that parameter governs; every other attribute
/// of every title is identical across the sweep.</para>
/// </remarks>
public static class Deterministic
{
    /// <summary>Property slots. Distinct values keep streams independent.</summary>
    public enum Slot
    {
        /// <summary>Whether the title carries an IMDb ID.</summary>
        Imdb = 1,

        /// <summary>Whether the title carries a TMDb ID.</summary>
        Tmdb = 2,

        /// <summary>Whether a second current item reports the same keys.</summary>
        Duplicate = 3,

        /// <summary>How many episodes a series has.</summary>
        EpisodeCount = 4,

        /// <summary>Whether a user has stranded state for this title.</summary>
        Watched = 5,

        /// <summary>Whether the current item already holds state for this user.</summary>
        CurrentState = 6,

        /// <summary>Item identity.</summary>
        Identity = 7,
    }

    /// <summary>Entity kinds, so a movie and a series with the same index differ.</summary>
    public enum Kind
    {
        /// <summary>A movie.</summary>
        Movie = 1,

        /// <summary>A series.</summary>
        Series = 2,

        /// <summary>An episode of a series.</summary>
        Episode = 3,

        /// <summary>
        /// An item that was removed by a move. Its GUID must never coincide with a
        /// live item's, or the stranded row it left behind would match by DESIGN
        /// §7.3 case 1 and count as recovered.
        /// </summary>
        RemovedItem = 4,

        /// <summary>A second current item reporting another item's provider keys.</summary>
        Duplicate = 5,
    }

    /// <summary>
    /// Draws a uniform value in [0, 1).
    /// </summary>
    /// <param name="seed">The population seed.</param>
    /// <param name="kind">The entity kind.</param>
    /// <param name="index">The entity index.</param>
    /// <param name="slot">Which property is being drawn.</param>
    /// <param name="sub">A sub-index, for per-user or per-episode draws.</param>
    /// <returns>A deterministic value in [0, 1).</returns>
    public static double NextDouble(int seed, Kind kind, int index, Slot slot, int sub = 0)
    {
        var bits = Mix(seed, kind, index, slot, sub);

        // 53 significant bits, the same precision Random.NextDouble offers.
        return (bits >> 11) * (1.0 / 9007199254740992.0);
    }

    /// <summary>
    /// Derives a stable GUID for an entity.
    /// </summary>
    /// <param name="seed">The population seed.</param>
    /// <param name="kind">The entity kind.</param>
    /// <param name="index">The entity index.</param>
    /// <param name="sub">A sub-index, for episodes within a series.</param>
    /// <returns>A deterministic GUID.</returns>
    public static Guid Identity(int seed, Kind kind, int index, int sub = 0)
    {
        var low = Mix(seed, kind, index, Slot.Identity, sub);
        var high = Mix(seed, kind, index, Slot.Identity, sub + 1_000_000);
        var bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 8), low);
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 8), high);
        return new Guid(bytes);
    }

    // SplitMix64. Chosen because it is a stateless function of its input, unlike
    // HashCode.Combine, which is randomized per process and would make a "seeded"
    // run irreproducible across machines.
    private static ulong Mix(int seed, Kind kind, int index, Slot slot, int sub)
    {
        var z = unchecked(
            ((ulong)(uint)seed * 0x9E3779B97F4A7C15UL)
            ^ ((ulong)(uint)index * 0xBF58476D1CE4E5B9UL)
            ^ ((ulong)(uint)kind << 56)
            ^ ((ulong)(uint)slot << 48)
            ^ ((ulong)(uint)sub * 0x94D049BB133111EBUL));

        unchecked
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
