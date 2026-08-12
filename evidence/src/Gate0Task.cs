using System.Text.Json;
using Jellyfin.Database.Implementations;
using MediaBrowser.Model.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Gate0;

/// <summary>
/// Gate 0 probe: resolve the host database context, then dump every UserData
/// row so the detach behaviour can be observed rather than assumed. Read-only.
/// </summary>
public class Gate0Task : IScheduledTask
{
    private static readonly Guid Sentinel = new("00000000-0000-0000-0000-000000000001");

    private readonly IDbContextFactory<JellyfinDbContext> _dbFactory;
    private readonly ILogger<Gate0Task> _logger;

    public Gate0Task(IDbContextFactory<JellyfinDbContext> dbFactory, ILogger<Gate0Task> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public string Name => "Gate 0 probe";

    public string Key => "Gate0Probe";

    public string Description => "Dumps UserData rows via the host database context.";

    public string Category => "Diagnostics";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var rows = await db.UserData.AsNoTracking()
            .Select(u => new
            {
                u.ItemId,
                u.UserId,
                u.CustomDataKey,
                u.Played,
                u.PlayCount,
                u.PlaybackPositionTicks,
                u.IsFavorite,
                u.LastPlayedDate,
                u.Rating,
                u.RetentionDate,
                u.AudioStreamIndex,
                u.SubtitleStreamIndex,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var detached = rows.Count(r => r.ItemId == Sentinel);

        _logger.LogInformation(
            "GATE0 RESULT: total UserData rows = {Total}, detached (sentinel) rows = {Detached}",
            rows.Count,
            detached);

        foreach (var r in rows)
        {
            _logger.LogInformation(
                "GATE0 ROW {Kind}: {Json}",
                r.ItemId == Sentinel ? "DETACHED" : "live",
                JsonSerializer.Serialize(r));
        }

        progress.Report(100);
    }
}
