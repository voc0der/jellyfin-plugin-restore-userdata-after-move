using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.UserDataRestore.Core.Planning;

/// <summary>A plan file on disk.</summary>
/// <param name="Path">The full path.</param>
/// <param name="FileName">The file name.</param>
/// <param name="ShortPlanId">The plan ID prefix embedded in the name.</param>
public readonly record struct StoredPlan(string Path, string FileName, string ShortPlanId);

/// <summary>
/// Publishes plans atomically and keeps a bounded number of them (DESIGN §8).
/// </summary>
/// <remarks>
/// Plans are audit artifacts, not a standing identity database. They are written
/// to a temporary file and renamed into place so a reader never sees a partial
/// plan, and pruning can be told to spare a named plan — which belongs with the
/// code that deletes files, whether or not a caller currently uses it.
/// </remarks>
public sealed class PlanStore(string directory)
{
    /// <summary>The number of characters of the plan ID that appear in a file name.</summary>
    public const int ShortIdLength = 12;

    private const string Prefix = "plan-";
    private const string Extension = ".json";

    private readonly string _directory = directory ?? throw new ArgumentNullException(nameof(directory));

    /// <summary>Gets the directory plans are written to.</summary>
    public string Directory => _directory;

    /// <summary>
    /// Writes a plan atomically.
    /// </summary>
    /// <param name="plan">The sealed plan.</param>
    /// <returns>The path it was written to.</returns>
    public string Write(PlanDocument plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrEmpty(plan.PlanId))
        {
            throw new ArgumentException("Plan has no ID; seal it with PlanCanonicalizer first.", nameof(plan));
        }

        System.IO.Directory.CreateDirectory(_directory);

        // Join, not Combine: Combine would drop _directory entirely if a file
        // name ever came back rooted, writing the plan somewhere else without
        // complaint.
        var path = Path.Join(_directory, BuildFileName(plan));
        var temporary = path + ".tmp";
        var json = PlanCanonicalizer.ToReadableJson(plan);

        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Lists stored plans, newest first.
    /// </summary>
    /// <returns>The stored plans.</returns>
    public IReadOnlyList<StoredPlan> List()
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            return [];
        }

        return [.. System.IO.Directory
            .EnumerateFiles(_directory, Prefix + "*" + Extension)
            .Select(path => new StoredPlan(path, Path.GetFileName(path), ExtractShortId(Path.GetFileName(path))))

            // File names start with a sortable UTC timestamp, so ordinal order is
            // chronological order without touching filesystem metadata.
            .OrderByDescending(stored => stored.FileName, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Reads a stored plan back by its ID.
    /// </summary>
    /// <param name="planId">The full plan ID.</param>
    /// <returns>The plan, or <see langword="null"/> if no stored file carries it.</returns>
    /// <remarks>
    /// Matched on the ID inside the file, not on the file name. The name carries a
    /// short prefix for humans; what authorizes an apply is the full ID.
    /// </remarks>
    public PlanDocument? Read(string planId)
    {
        if (string.IsNullOrEmpty(planId))
        {
            return null;
        }

        foreach (var stored in List())
        {
            PlanDocument plan;

            try
            {
                plan = PlanCanonicalizer.FromJson(File.ReadAllText(stored.Path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                continue;
            }

            if (string.Equals(plan.PlanId, planId, StringComparison.Ordinal))
            {
                return plan;
            }
        }

        return null;
    }

    /// <summary>
    /// Deletes all but the newest plans.
    /// </summary>
    /// <param name="keep">How many to keep. Values below one keep one.</param>
    /// <param name="protectedPlanId">A plan ID that must never be deleted.</param>
    /// <returns>The number of plans deleted.</returns>
    public int PruneToLatest(int keep, string? protectedPlanId = null)
    {
        var retain = Math.Max(1, keep);
        var protectedShortId = protectedPlanId is null or "" ? null : Shorten(protectedPlanId);
        var deleted = 0;

        foreach (var stored in List().Skip(retain))
        {
            if (protectedShortId is not null
                && string.Equals(stored.ShortPlanId, protectedShortId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(stored.Path);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Retention is housekeeping. A file that will not delete is not a
                // reason to fail an analysis run that already succeeded.
            }
        }

        return deleted;
    }

    /// <summary>
    /// Builds the file name for a plan.
    /// </summary>
    /// <param name="plan">The sealed plan.</param>
    /// <returns>A sortable, ID-bearing file name.</returns>
    public static string BuildFileName(PlanDocument plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var timestamp = plan.CreatedUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture, $"{Prefix}{timestamp}-{Shorten(plan.PlanId)}{Extension}");
    }

    /// <summary>
    /// Shortens a plan ID to the form used in file names and confirmation phrases.
    /// </summary>
    /// <param name="planId">The full plan ID.</param>
    /// <returns>The first <see cref="ShortIdLength"/> characters.</returns>
    public static string Shorten(string planId)
    {
        ArgumentNullException.ThrowIfNull(planId);
        return planId.Length <= ShortIdLength ? planId : planId[..ShortIdLength];
    }

    private static string ExtractShortId(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var separator = stem.LastIndexOf('-');
        return separator < 0 ? string.Empty : stem[(separator + 1)..];
    }
}
