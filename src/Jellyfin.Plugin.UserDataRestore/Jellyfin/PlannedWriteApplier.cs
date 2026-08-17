using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Model;
using Jellyfin.Plugin.UserDataRestore.Core.Planning;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UserDataRestore.Jellyfin;

/// <summary>
/// One planned write, from the checks that guard it to the verification that
/// follows it (DESIGN §9.2).
/// </summary>
/// <remarks>
/// <para>A class rather than two methods on the scheduled task, and the reason is
/// testability rather than tidiness. Everything that decides whether a restore is
/// safe happens in here, and while it lived inside the task the only way to reach
/// it was to run a Jellyfin server: the analyzer's rules were tested exhaustively,
/// the reader's queries eventually were, and the glue that consults them at the
/// moment of writing was covered by nothing at all. Three defects in a row landed
/// in that glue.</para>
/// <para>The wrapper and the core belong together. Which side of the save an
/// exception arrived on is the whole difference between
/// <see cref="WriteOutcome.Failed"/> and <see cref="WriteOutcome.Uncertain"/>, and
/// that distinction lives in the boundary between the two — extracting either
/// alone would leave the interesting part still unreachable.</para>
/// </remarks>
internal sealed class PlannedWriteApplier
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly UserDataWriter _writer;
    private readonly UserDataReader _reader;
    private readonly LibraryItemCollector _collector;
    private readonly AnalysisOptions _options;
    private readonly KeyOwnership _ownership;
    private readonly ILogger _logger;

    public PlannedWriteApplier(
        IUserManager userManager,
        ILibraryManager libraryManager,
        UserDataWriter writer,
        UserDataReader reader,
        LibraryItemCollector collector,
        AnalysisOptions options,
        KeyOwnership ownership,
        ILogger logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _writer = writer;
        _reader = reader;
        _collector = collector;
        _options = options;
        _ownership = ownership;
        _logger = logger;
    }

    /// <summary>
    /// Applies one planned write, returning what became of it.
    /// </summary>
    /// <param name="write">The write to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome, never an exception except cancellation.</returns>
    /// <remarks>
    /// <para>Nothing in here is allowed to escape except cancellation. A write
    /// that throws does stop the batch, but it must stop it by returning — the
    /// plan is written after this pass, so an exception escaping here would leave
    /// user state changed with no record of what changed it.</para>
    /// <para>Which side of the save it threw on is the whole of the difference
    /// between <see cref="WriteOutcome.Failed"/> and
    /// <see cref="WriteOutcome.Uncertain"/>. Everything before the save leaves the
    /// target provably untouched. The save itself does not: it can throw after the
    /// database has already committed, so once it has been entered the honest
    /// answer about that item is that nobody knows.</para>
    /// </remarks>
    public async Task<WriteResult> ApplyAsync(PlannedWrite write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);

        var saveEntered = false;

        try
        {
            return await ApplyCoreAsync(write, () => saveEntered = true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (saveEntered)
            {
                // Cancellation included. Once the save has been entered the run
                // owes an answer about this item, and "the operator pressed stop"
                // is not one - the write may already have committed.
                _logger.LogError(
                    ex,
                    "Restoring user {UserId} item {ItemId} threw during the save. Whether it landed is unknown - the save can throw "
                    + "after the database has committed. The stranded row is untouched, so the state is recoverable either way.",
                    write.UserId,
                    write.ItemId);
                return new WriteResult(write, WriteOutcome.Uncertain, "save_threw");
            }

            if (ex is OperationCanceledException)
            {
                throw;
            }

            _logger.LogError(
                ex,
                "Restoring user {UserId} item {ItemId} threw before the save was attempted, so the item is untouched. The next run retries it.",
                write.UserId,
                write.ItemId);
            return new WriteResult(write, WriteOutcome.Failed, "threw_before_save");
        }
    }

    private async Task<WriteResult> ApplyCoreAsync(
        PlannedWrite write,
        Action enteringSave,
        CancellationToken cancellationToken)
    {
        var user = _userManager.GetUserById(write.UserId);
        var item = _libraryManager.GetItemById(write.ItemId);

        if (user is null || item is null)
        {
            _logger.LogWarning(
                "User {UserId} or item {ItemId} disappeared between analysis and write; skipping.",
                write.UserId,
                write.ItemId);
            return new WriteResult(write, WriteOutcome.Skipped, user is null ? "user_gone" : "item_gone");
        }

        // Everything that admitted this target, asked again of the item as it is
        // now. The analysis ran seconds ago, but a metadata refresh can rewrite an
        // item's provider IDs in that time — and the keys those IDs produce are the
        // entire identity argument for writing here. An item that has stopped
        // answering to them is not the item the evidence was about. Membership and
        // path come off the live item too, not out of a map built before the loop.
        var snapshot = _collector.Snapshot(item, _options.EligibleLibraryIds, _options.RequirePathExists);
        if (TargetRevalidation.Evaluate(snapshot, _options, write.SourceKeys, _ownership) is { } disqualification)
        {
            _logger.LogWarning(
                "Item {ItemId} no longer qualifies as the target for user {UserId} ({Reason}); skipping. "
                + "The stranded row is untouched, so the next run reconsiders it.",
                write.ItemId,
                write.UserId,
                disqualification);
            return new WriteResult(write, WriteOutcome.Skipped, disqualification);
        }

        // Uniqueness again, now, against the catalogue as it is rather than as it
        // was at the top of the loop. The index above is a full pass over every
        // movie and episode and can only be afforded once per run, which leaves
        // drift inside the loop uncovered — a metadata refresh giving a second
        // item this key needs no library scan and so trips none of the guards
        // that abandon the batch. This asks the same question about this one
        // item, from an index built over the handful of items that could
        // plausibly collide with it.
        var contenders = KeyOwnership.Build([snapshot, .. _collector.FindKeyContenders(item, cancellationToken)]);
        if (TargetRevalidation.Evaluate(snapshot, _options, write.SourceKeys, contenders) is { } contested)
        {
            _logger.LogWarning(
                "Item {ItemId} stopped being the only item answering to the keys that identified it for user {UserId} ({Reason}); skipping. "
                + "Something re-identified an item during this run. The stranded row is untouched, so the next run reconsiders it.",
                write.ItemId,
                write.UserId,
                contested);
            return new WriteResult(write, WriteOutcome.Skipped, contested);
        }

        // Cheap second opinion through the manager, which sees state the database
        // read below cannot contradict but may have cached differently.
        if (!_writer.Read(user, item).IsDefault)
        {
            _logger.LogInformation(
                "Item {ItemId} gained user state for {UserId} since the analysis; leaving it alone.",
                write.ItemId,
                write.UserId);
            return new WriteResult(write, WriteOutcome.Skipped, "target_gained_state");
        }

        // The other half of the question. Everything above asks whether this is
        // still the right item to write to; this asks whether the state about to
        // be written is still the state the sentinel holds.
        //
        // The stranded rows were read once, at the top of the run, and nothing
        // this plugin does touches them. Other things do: Jellyfin's own cleanup
        // task deletes sentinel rows past a retention age, and deleting another
        // item can replace the row under the same (user, key) with a newer
        // snapshot. Neither needs a library scan, so neither trips the guard that
        // abandons the batch. Restoring a deleted source writes a copy of state
        // the server has finished with; restoring a superseded one is worse,
        // because the newer source then reads as current_state_conflict against
        // what this run left behind and is never restored at all.
        //
        // Asked of every key the target answers to, not only the ones that
        // authorised the write, and of the live key set as well as the recorded
        // one. A deletion elsewhere can strand a newer snapshot under a key this
        // item reports but that contributed nothing here; a re-read narrowed to
        // the contributing keys never asks about it, and Jellyfin would then fan
        // the older state out across that key too.
        var sentinelKeys = write.TargetKeys.Union(snapshot.UserDataKeys, StringComparer.Ordinal).ToArray();
        var sources = await _reader.ReadDetachedAsync(write.UserId, sentinelKeys, cancellationToken).ConfigureAwait(false);
        if (SourceRevalidation.Evaluate(write.SentinelFingerprints, sources) is { } stale)
        {
            _logger.LogWarning(
                "The stranded rows that authorised restoring user {UserId} item {ItemId} are not what the analysis read ({Reason}); skipping. "
                + "Something else changed the sentinel during this run. Nothing here has modified them, so the next run reads them as they are now.",
                write.UserId,
                write.ItemId,
                stale);
            return new WriteResult(write, WriteOutcome.Skipped, stale);
        }

        // Row existence, straight from the database, because the manager cannot
        // answer it: it reports a pair with no row and a pair whose row holds
        // nothing but defaults identically. An unwatch or an unfavourite performed
        // since the analysis writes exactly such a row, and reading it as
        // "untouched" is how a scheduled run would undo a deliberate act.
        //
        // Last of the checks, immediately before the save, and in that order on
        // purpose: it is the authoritative one, so nothing else belongs between it
        // and the write it guards. What remains is a genuine race — the manager
        // offers no conditional save, so another thread clearing this pair in the
        // gap would still be overwritten — but the gap is one round trip, and
        // reaching it at all requires the clear to land on a pair that has no row,
        // which from the user's side is clearing something already clear.
        if (await _reader.RowExistsAsync(write.UserId, write.ItemId, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "User {UserId} gained a user-data row for item {ItemId} since the analysis; leaving it alone. "
                + "A row holding default values is an explicit clear, not an absence.",
                write.UserId,
                write.ItemId);
            return new WriteResult(write, WriteOutcome.Skipped, "row_exists");
        }

        // Past this line nothing about the target is provable any more, which is
        // why it is the last one: everything above declines cleanly, and the item
        // is untouched whatever happens.
        enteringSave();
        _writer.Save(user, item, write.State);

        // Read back through the manager rather than trusting the call: the point
        // of the exercise is that the state is there afterwards.
        if (!RecoveryStateComparer.Semantic.Equals(_writer.Read(user, item), write.State))
        {
            _logger.LogError(
                "Wrote user {UserId} item {ItemId} but read back different state. What the item holds now is not what was asked for "
                + "and not necessarily what it held before. The stranded row is untouched.",
                write.UserId,
                write.ItemId);
            return new WriteResult(write, WriteOutcome.Uncertain, "verification_mismatch");
        }

        return await VerifyPersistedAsync(write, snapshot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Confirms the save reached the database, not just the manager's cache.
    /// </summary>
    /// <remarks>
    /// <para>The read-back above goes through <c>IUserDataManager</c>, which on
    /// 10.11 answers from a cache it populated during the save. That proves the
    /// manager accepted the state; it does not prove a row exists. DESIGN §9.2 has
    /// always asked for the database to be consulted too, and until now it was
    /// not.</para>
    /// <para>Rows carrying something other than what was asked for make the
    /// outcome <see cref="WriteOutcome.Uncertain"/>, which stops the batch: the
    /// item holds state this run cannot account for.</para>
    /// <para>A key with no row at all is reported and does not. Jellyfin fans a
    /// save across every key the item reports, and both supported lines do so in
    /// one transaction, so a narrower fan-out is not a failed write — it is a
    /// write that will be harder to find after the <i>next</i> move. That is worth
    /// an operator's attention and not worth abandoning the remaining restores
    /// for, and the distinction is the difference between "this did not work" and
    /// "this worked less well than expected".</para>
    /// </remarks>
    private async Task<WriteResult> VerifyPersistedAsync(
        PlannedWrite write,
        CurrentItemSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var rows = await _reader.ReadCurrentAsync([(write.UserId, write.ItemId)], cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0)
        {
            _logger.LogError(
                "The manager reports user {UserId} item {ItemId} as restored, but the database holds no row for that pair. "
                + "The save did not reach storage, so what survives a restart is unknown. The stranded row is untouched.",
                write.UserId,
                write.ItemId);
            return new WriteResult(write, WriteOutcome.Uncertain, "not_persisted");
        }

        var disagreeing = rows
            .Where(row => !RecoveryStateComparer.Semantic.Equals(row.State, write.State))
            .Select(row => row.CustomDataKey ?? "(no key)")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (disagreeing.Length > 0)
        {
            _logger.LogError(
                "Wrote user {UserId} item {ItemId}, and the database holds different state under {Count} of its keys ({Keys}). "
                + "The manager reported the write as good, so what the item holds now is not what either of them says. The stranded row is untouched.",
                write.UserId,
                write.ItemId,
                disagreeing.Length,
                string.Join(", ", disagreeing));
            return new WriteResult(write, WriteOutcome.Uncertain, "persisted_state_mismatch");
        }

        var written = rows.Select(row => row.CustomDataKey).Where(key => key is not null).ToHashSet(StringComparer.Ordinal);
        var absent = snapshot.UserDataKeys
            .Where(key => !string.IsNullOrEmpty(key) && !written.Contains(key))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (absent.Length > 0)
        {
            _logger.LogWarning(
                "Restored user {UserId} item {ItemId}, but {Count} of the keys it reports carry no row ({Keys}). "
                + "The state is back and correct; it is simply not stored under every name this item answers to, so a future move that "
                + "strands one of those keys will not find it.",
                write.UserId,
                write.ItemId,
                absent.Length,
                string.Join(", ", absent));
        }

        return new WriteResult(write, WriteOutcome.Restored, null);
    }
}
