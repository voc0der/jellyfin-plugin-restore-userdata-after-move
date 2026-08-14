using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.UserDataRestore.Core.Planning;

/// <summary>
/// An append-only record of what a run did, written as it does it (DESIGN §8,
/// §9.2 step 11).
/// </summary>
/// <remarks>
/// <para>The plan is the better artifact and the ledger is the one that
/// survives. A plan is composed after the last write and published in one
/// operation, so everything that can go wrong between the first save and that
/// moment — a full disk, a revoked permission, a process killed mid-run — takes
/// the entire record of the run with it, and user data has already changed by
/// then. This is the same account written the other way round: one line per
/// write, flushed to disk before the next write is attempted.</para>
/// <para>It is therefore deliberately dull. One JSON object per line, no
/// document to close, no hash over the whole, nothing that has to be correct
/// before the file is worth reading. A ledger truncated by a power cut is a
/// ledger missing its last line, which is exactly the property the plan does not
/// have.</para>
/// <para>Not a substitute for the plan: it carries no classification, no
/// diagnostics, and no fingerprints. It answers one question — which
/// <c>(user, item)</c> pairs did this run touch, and what happened to each — and
/// that is the question an operator has after a crash.</para>
/// </remarks>
public sealed class RunLedger : IDisposable
{
    private const string Prefix = "run-";
    private const string Extension = ".jsonl";

    private static readonly JsonSerializerOptions LineOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly FileStream _stream;

    private RunLedger(FileStream stream, string path)
    {
        _stream = stream;
        Path = path;
    }

    /// <summary>Gets the file this run is being recorded to.</summary>
    public string Path { get; }

    /// <summary>
    /// Opens a ledger for one run.
    /// </summary>
    /// <param name="directory">The directory to write it to.</param>
    /// <param name="startedUtc">When the run began, used for the file name.</param>
    /// <returns>The open ledger.</returns>
    public static RunLedger Open(string directory, DateTimeOffset startedUtc)
    {
        ArgumentNullException.ThrowIfNull(directory);

        System.IO.Directory.CreateDirectory(directory);

        var timestamp = startedUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        // Join, not Combine, for the reason PlanStore gives: a rooted right-hand
        // side would silently relocate the file out of the plugin's directory.
        var path = System.IO.Path.Join(directory, Prefix + timestamp + Extension);

        return new RunLedger(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
            path);
    }

    /// <summary>
    /// Lists stored ledgers, newest first.
    /// </summary>
    /// <param name="directory">The directory to look in.</param>
    /// <returns>The full paths.</returns>
    public static IReadOnlyList<string> List(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (!System.IO.Directory.Exists(directory))
        {
            return [];
        }

        // File names lead with a sortable UTC timestamp, so ordinal order is
        // chronological without touching filesystem metadata.
        return [.. System.IO.Directory
            .EnumerateFiles(directory, Prefix + "*" + Extension)
            .OrderByDescending(path => System.IO.Path.GetFileName(path), StringComparer.Ordinal)];
    }

    /// <summary>
    /// Deletes all but the newest ledgers.
    /// </summary>
    /// <param name="directory">The directory to prune.</param>
    /// <param name="keep">How many to keep. Values below one keep one.</param>
    /// <returns>The number deleted.</returns>
    public static int PruneToLatest(string directory, int keep)
    {
        var deleted = 0;

        foreach (var path in List(directory).Skip(Math.Max(1, keep)))
        {
            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Retention is housekeeping. A file that will not delete is not a
                // reason to fail a run that already succeeded.
            }
        }

        return deleted;
    }

    /// <summary>
    /// Appends one write's outcome and flushes it to disk.
    /// </summary>
    /// <param name="result">The write and what became of it.</param>
    /// <remarks>
    /// Flushed all the way down, before the caller attempts the next write. A
    /// buffered line is not a record of anything: the failure this exists for is
    /// the one that stops the process, and it would take the buffer with it.
    /// </remarks>
    public void Append(WriteResult result)
    {
        var line = JsonSerializer.Serialize(
            new LedgerLine
            {
                RecordedUtc = DateTimeOffset.UtcNow,
                UserId = result.Write.UserId.ToString("D", CultureInfo.InvariantCulture),
                ItemId = result.Write.ItemId.ToString("D", CultureInfo.InvariantCulture),
                Outcome = WriteOutcomes.ToWire(result.Outcome),
                Detail = result.Detail,
            },
            LineOptions);

        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        _stream.Write(bytes, 0, bytes.Length);
        _stream.Flush(flushToDisk: true);
    }

    /// <inheritdoc />
    public void Dispose() => _stream.Dispose();

    /// <summary>One line of the ledger.</summary>
    private sealed record LedgerLine
    {
        [JsonPropertyName("recordedUtc")]
        public required DateTimeOffset RecordedUtc { get; init; }

        [JsonPropertyName("userId")]
        public required string UserId { get; init; }

        [JsonPropertyName("itemId")]
        public required string ItemId { get; init; }

        [JsonPropertyName("outcome")]
        public required string Outcome { get; init; }

        [JsonPropertyName("detail")]
        public string? Detail { get; init; }
    }
}
