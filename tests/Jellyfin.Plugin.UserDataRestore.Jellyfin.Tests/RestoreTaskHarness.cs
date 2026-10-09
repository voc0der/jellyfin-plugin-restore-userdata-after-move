using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.UserDataRestore.Configuration;
using Jellyfin.Plugin.UserDataRestore.Core.Planning;
using Jellyfin.Plugin.UserDataRestore.ScheduledTasks;
using Jellyfin.Plugin.UserDataRestore.Tests.Fixtures;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Jellyfin.Plugin.UserDataRestore.Jellyfin.Tests;

/// <summary>
/// Runs the public scheduled-task entry point with real SQLite reads, media
/// files and audit artifacts. Only the host managers are substituted.
/// </summary>
internal sealed class RestoreTaskHarness : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "restore-task-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLibrary _library = FakeLibrary.Create();
    private readonly Dictionary<Guid, UserItemData> _saved = [];

    public RestoreTaskHarness()
    {
        Directory.CreateDirectory(MediaDirectory);
        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(Path.Join(_directory, "configuration"));
        Serializer = Substitute.For<IXmlSerializer>();
        Serializer.DeserializeFromFile(typeof(PluginConfiguration), Arg.Any<string>()).Returns(new PluginConfiguration());
        Plugin = new Plugin(paths, Serializer);
        Plugin.SetAttributes(typeof(Plugin).Assembly.Location, Path.Join(_directory, "plugin"), new Version(1, 0, 0, 0));
        Plugin.Configuration.EligibleLibraryIds = [LibraryId.ToString("D")];

        Folder = new VirtualFolderInfo
        {
            ItemId = LibraryId.ToString("D"),
            CollectionType = CollectionTypeOptions.movies,
            Locations = [MediaDirectory],
        };
        LibraryManager.GetVirtualFolders().Returns([Folder]);

        User = new User("restore-task", "Default", "Default") { Id = Guid.NewGuid() };
        UserManager.GetUsersIds().Returns([User.Id]);
        UserManager.GetUserById(User.Id).Returns(User);
        UserDataManager.GetUserData(Arg.Any<User>(), Arg.Any<BaseItem>())
            .Returns(call => _saved.GetValueOrDefault(call.Arg<BaseItem>().Id));
        UserDataManager.When(manager => manager.SaveUserData(
                Arg.Any<User>(), Arg.Any<BaseItem>(), Arg.Any<UpdateUserItemDataDto>(), Arg.Any<UserDataSaveReason>()))
            .Do(call => Persist(call.Arg<BaseItem>(), call.Arg<UpdateUserItemDataDto>()));

        Scan.ScheduledTask.Returns(Substitute.For<IScheduledTask>());
        Scan.ScheduledTask.Key.Returns("RefreshLibrary");
        Scan.ScheduledTask.Name.Returns("Localized scan name");
        Scan.State.Returns(TaskState.Idle);
        TaskManager.ScheduledTasks.Returns([Scan]);
        Host.SystemId.Returns("coverage-server");
        Host.ApplicationVersion.Returns(new Version(12, 1, 0));
        Host.ApplicationVersionString.Returns("12.1.0");

        Logger.When(logger => logger.Log(
                Arg.Any<LogLevel>(), Arg.Any<EventId>(), Arg.Any<Arg.AnyType>(),
                Arg.Any<Exception?>(), Arg.Any<Func<Arg.AnyType, Exception?, string>>()))
            .Do(call => Messages.Add((call.Arg<LogLevel>(), call[2].ToString()!)));

        // Progress<T> queues callbacks; these tests need to inject faults at the
        // observed phase before the task continues, without sleeps or races.
        Progress.When(progress => progress.Report(Arg.Any<double>())).Do(call =>
        {
            var value = call.Arg<double>();
            ProgressValues.Add(value);
            OnProgress?.Invoke(value);
        });

        Task = new RestoreUserDataTask(Database.Factory, LibraryManager, UserManager, UserDataManager, Host, TaskManager, Logger);
    }

    public UserDataDatabase Database { get; } = UserDataDatabase.Create();
    public Guid LibraryId { get; } = Guid.NewGuid();
    public string MediaDirectory => Path.Join(_directory, "media");
    public Plugin Plugin { get; }
    public VirtualFolderInfo Folder { get; }
    public User User { get; }
    public ILibraryManager LibraryManager => _library.Manager;
    public IUserManager UserManager { get; } = Substitute.For<IUserManager>();
    public IUserDataManager UserDataManager { get; } = Substitute.For<IUserDataManager>();
    public IXmlSerializer Serializer { get; }
    public IApplicationHost Host { get; } = Substitute.For<IApplicationHost>();
    public IScheduledTaskWorker Scan { get; } = Substitute.For<IScheduledTaskWorker>();
    public ITaskManager TaskManager { get; } = Substitute.For<ITaskManager>();
    public ILogger<RestoreUserDataTask> Logger { get; } = Substitute.For<ILogger<RestoreUserDataTask>>();
    public List<(LogLevel Level, string Message)> Messages { get; } = [];
    public IProgress<double> Progress { get; } = Substitute.For<IProgress<double>>();
    public List<double> ProgressValues { get; } = [];
    public Action<double>? OnProgress { get; set; }
    public Exception? SaveThrows { get; set; }
    public RestoreUserDataTask Task { get; }

    public Movie AddMovie(string imdb = "tt0133093")
    {
        var path = Path.Join(MediaDirectory, imdb + ".mkv");
        File.WriteAllText(path, "media fixture");
        var movie = _library.AddMovie(imdb, LibraryId, new() { ["Imdb"] = imdb }, path);
        Database.AddDetached(User.Id, imdb);
        return movie;
    }

    public Task ExecuteAsync(CancellationToken cancellationToken) => Task.ExecuteAsync(Progress, cancellationToken);

    public PlanDocument ReadPlan() => PlanCanonicalizer.FromJson(
        File.ReadAllText(Assert.Single(new PlanStore(Plugin.PlanDirectory).List()).Path));

    public IReadOnlyList<JsonElement> ReadLedger() => File.ReadAllLines(Assert.Single(RunLedger.List(Plugin.PlanDirectory)))
        .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();

    public void Dispose()
    {
        Database.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private void Persist(BaseItem item, UpdateUserItemDataDto dto)
    {
        if (SaveThrows is { } failure)
        {
            throw failure;
        }

        _saved[item.Id] = new UserItemData
        {
            Key = item.Id.ToString("D"),
            Played = dto.Played!.Value,
            PlayCount = dto.PlayCount!.Value,
            PlaybackPositionTicks = dto.PlaybackPositionTicks!.Value,
            IsFavorite = dto.IsFavorite!.Value,
            LastPlayedDate = dto.LastPlayedDate,
            Rating = dto.Rating,
        };

        // Simulate the host's key fan-out so both verification reads and the
        // next task run see the saved state. The live harness tests the host's
        // actual save implementation separately.
        foreach (var key in item.GetUserDataKeys().Distinct(StringComparer.Ordinal))
        {
            Database.AddCurrent(User.Id, item.Id, key, dto.Played.Value, dto.PlayCount.Value,
                dto.PlaybackPositionTicks.Value, dto.IsFavorite.Value, dto.Rating);
        }
    }
}
