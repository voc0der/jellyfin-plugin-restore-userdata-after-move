using System.Globalization;
using System.Text;
using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Model;

namespace Jellyfin.Plugin.UserDataRestore.Sweep;

/// <summary>
/// Runs the analyzer across a range of library shapes and reports how recovery
/// responds to each (PLAN §2).
/// </summary>
/// <remarks>
/// <para>This is a simulation of the analyzer against a model of how Jellyfin
/// strands rows. It establishes what the rules in DESIGN §7.3 imply for a library
/// of a given shape. It is <b>not</b> evidence about how real libraries are
/// shaped, and no arrangement of it could be.</para>
/// <para>In particular, the multiplicative relationship it exhibits is a property
/// of the generator: coverage, duplication and pre-existing state are drawn as
/// independent events, so their effects compose. Reporting that as though the
/// simulation had discovered it would be circular. What the comparison against the
/// analytical expectation does establish is narrower and still worth having — that
/// the analyzer, the generator, and the arithmetic all agree, so a curve that bends
/// unexpectedly means something real rather than a bug.</para>
/// </remarks>
public static class Program
{
    // Twenty populations per point. One seed cannot distinguish the effect being
    // swept from the luck of which series drew an IMDb ID — and with coverage
    // assigned per series, a single unlucky long-running show moves the rate by
    // percentage points.
    private const int SeedsPerPoint = 20;

    private static readonly ReasonCode[] CandidateCodes =
    [
        ReasonCode.Ready,
        ReasonCode.AlreadyApplied,
        ReasonCode.CurrentStateConflict,
        ReasonCode.InsufficientIdentityEvidence,
        ReasonCode.SourceHasNoEffect,
    ];

    private static readonly ReasonCode[] RowCodes =
    [
        ReasonCode.AmbiguousCurrentKey,
        ReasonCode.NoCurrentKeyMatch,
    ];

    /// <summary>
    /// Entry point.
    /// </summary>
    /// <param name="args">Optional output path for the CSV.</param>
    public static void Main(string[] args)
    {
        var csvPath = args is [var first, ..] ? first : "evidence/sweep/sweep.csv";
        var baseline = new LibraryShape();
        var results = new List<SweepPoint> { Run("baseline", "-", "-", baseline) };

        foreach (var value in Fractions())
        {
            results.Add(Run("imdb_coverage", "imdbCoverage", value, baseline with { ImdbCoverage = value }));
        }

        foreach (var value in Fractions())
        {
            results.Add(Run("tmdb_only", "tmdbCoverage", value, baseline with { ImdbCoverage = 0, TmdbCoverage = value }));
        }

        foreach (var value in new[] { 0.0, 0.05, 0.1, 0.2, 0.35, 0.5 })
        {
            results.Add(Run("duplication", "duplication", value, baseline with { Duplication = value }));
        }

        foreach (var value in new[] { 1, 2, 3, 5, 8 })
        {
            results.Add(Run("moves", "movesPerTitle", value, baseline with { MovesPerTitle = value }));
        }

        foreach (var value in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            results.Add(Run("current_state", "currentStateFraction", value, baseline with { CurrentStateFraction = value }));
        }

        foreach (var value in new[] { 0.0, 0.5, 1.0 })
        {
            results.Add(Run("episode_share", "episodeShare", value, baseline with { EpisodeShare = value }));
        }

        // Series length drives how much watch history hangs off a single coverage
        // draw, and therefore how far a run can sit from its own nominal coverage.
        foreach (var value in new[] { 1.0, 6.0, 18.0, 60.0, 150.0 })
        {
            results.Add(Run("series_length", "meanEpisodesPerSeries", value, baseline with { MeanEpisodesPerSeries = value }));
        }

        Write(csvPath, results);
        Print(results);
        CheckAgainstExpectation();
    }

    private static IEnumerable<double> Fractions()
    {
        for (var step = 0; step <= 10; step++)
        {
            yield return step / 10.0;
        }
    }

    private static SweepPoint Run(string series, string parameter, object value, LibraryShape shape)
    {
        var runs = new List<RunOutcome>(SeedsPerPoint);

        for (var seed = 1; seed <= SeedsPerPoint; seed++)
        {
            runs.Add(RunOnce(shape with { Seed = seed }));
        }

        return new SweepPoint(
            series,
            parameter,
            Format(value),
            runs.Count,
            (int)runs.Average(run => run.Opportunities),
            runs.Average(run => run.RecoveryRate),
            runs.Min(run => run.RecoveryRate),
            runs.Max(run => run.RecoveryRate),
            runs.Average(run => run.OpportunityWeightedImdb),
            runs.Average(run => run.ItemWeightedImdb),
            CandidateCodes.ToDictionary(code => code, code => runs.Average(run => run.ByCandidate[code])),
            RowCodes.ToDictionary(code => code, code => runs.Average(run => run.ByRow[code])));
    }

    private static RunOutcome RunOnce(LibraryShape shape)
    {
        var population = PopulationGenerator.Generate(shape);

        var candidates = DetachedUserDataAnalyzer.BuildCandidates(new AnalysisInput
        {
            DetachedRows = population.DetachedRows,
            CurrentItems = population.Items,
            KnownUserIds = population.UserIds,
            Options = PopulationGenerator.Options(),
        });

        var result = DetachedUserDataAnalyzer.Complete(candidates, population.CurrentRows);
        var byCandidate = CandidateCodes.ToDictionary(code => code, code => (double)result.CandidateCounts.GetValueOrDefault(code));
        var byRow = RowCodes.ToDictionary(code => code, code => (double)result.RowCounts.GetValueOrDefault(code));

        // Opportunities, not candidates: state that never produced a candidate at
        // all — because every key it had was a dead GUID — is exactly the loss
        // being measured.
        var ready = byCandidate[ReasonCode.Ready];
        var rate = population.Opportunities == 0 ? 0 : ready / population.Opportunities;

        return new RunOutcome(
            population.Opportunities,
            rate,
            population.OpportunityWeightedImdbCoverage,
            population.ItemWeightedImdbCoverage,
            byCandidate,
            byRow);
    }

    private static void Write(string path, IReadOnlyList<SweepPoint> points)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var csv = new StringBuilder();
        csv.Append("series,parameter,value,seeds,opportunities,recoveryMean,recoveryMin,recoveryMax,")
           .Append("opportunityWeightedImdbCoverage,itemWeightedImdbCoverage");

        foreach (var code in CandidateCodes)
        {
            csv.Append(",candidates.").Append(ReasonCodes.ToWire(code));
        }

        foreach (var code in RowCodes)
        {
            csv.Append(",rows.").Append(ReasonCodes.ToWire(code));
        }

        csv.AppendLine();

        foreach (var point in points)
        {
            csv.Append(point.Series).Append(',')
               .Append(point.Parameter).Append(',')
               .Append(point.Value).Append(',')
               .Append(point.Seeds.ToString(CultureInfo.InvariantCulture)).Append(',')
               .Append(point.Opportunities.ToString(CultureInfo.InvariantCulture)).Append(',')
               .Append(F(point.RecoveryMean)).Append(',')
               .Append(F(point.RecoveryMin)).Append(',')
               .Append(F(point.RecoveryMax)).Append(',')
               .Append(F(point.OpportunityWeightedImdb)).Append(',')
               .Append(F(point.ItemWeightedImdb));

            foreach (var code in CandidateCodes)
            {
                csv.Append(',').Append(point.ByCandidate[code].ToString("F1", CultureInfo.InvariantCulture));
            }

            foreach (var code in RowCodes)
            {
                csv.Append(',').Append(point.ByRow[code].ToString("F1", CultureInfo.InvariantCulture));
            }

            csv.AppendLine();
        }

        File.WriteAllText(path, csv.ToString());
        Console.WriteLine("Wrote " + path + " (" + points.Count + " points x " + SeedsPerPoint + " seeds)");
    }

    private static void Print(IReadOnlyList<SweepPoint> points)
    {
        Console.WriteLine();
        Console.WriteLine("| series | value | opportunities | recovery (mean) | range over 20 seeds | imdb: opportunity-weighted | imdb: item-weighted |");
        Console.WriteLine("|---|---|---|---|---|---|---|");

        foreach (var point in points)
        {
            Console.WriteLine(string.Join(" | ",
                "| " + point.Series,
                point.Value,
                point.Opportunities.ToString(CultureInfo.InvariantCulture),
                point.RecoveryMean.ToString("P1", CultureInfo.InvariantCulture),
                point.RecoveryMin.ToString("P1", CultureInfo.InvariantCulture) + " – " + point.RecoveryMax.ToString("P1", CultureInfo.InvariantCulture),
                point.OpportunityWeightedImdb.ToString("P1", CultureInfo.InvariantCulture),
                point.ItemWeightedImdb.ToString("P1", CultureInfo.InvariantCulture) + " |"));
        }
    }

    // Not a validation of the model against reality — it cannot be, since both
    // sides come from the same assumptions. It checks that the analyzer and the
    // generator agree with the arithmetic those assumptions imply, which is what
    // makes an unexpected bend in a curve worth investigating.
    private static void CheckAgainstExpectation()
    {
        LibraryShape[] cases =
        [
            new() { ImdbCoverage = 0.7, Duplication = 0.2, CurrentStateFraction = 0.3 },
            new() { ImdbCoverage = 0.4, Duplication = 0.35, CurrentStateFraction = 0.1 },
            new() { ImdbCoverage = 0.95, Duplication = 0.05, CurrentStateFraction = 0.5 },
            new() { ImdbCoverage = 0.55, Duplication = 0.1, CurrentStateFraction = 0.75, EpisodeShare = 0.2 },
        ];

        Console.WriteLine();
        Console.WriteLine("Self-consistency: simulated rate vs the generator's own expectation");
        Console.WriteLine("(both sides assume independence; this is not evidence about real libraries)");
        Console.WriteLine();
        Console.WriteLine("| imdb | duplication | currentState | expected | simulated | delta |");
        Console.WriteLine("|---|---|---|---|---|---|");

        foreach (var shape in cases)
        {
            var expected = shape.ImdbCoverage * (1 - shape.Duplication) * (1 - shape.CurrentStateFraction);
            var simulated = Enumerable.Range(1, SeedsPerPoint)
                .Select(seed => RunOnce(shape with { Seed = seed }).RecoveryRate)
                .Average();

            Console.WriteLine(string.Join(" | ",
                "| " + shape.ImdbCoverage.ToString("0.##", CultureInfo.InvariantCulture),
                shape.Duplication.ToString("0.##", CultureInfo.InvariantCulture),
                shape.CurrentStateFraction.ToString("0.##", CultureInfo.InvariantCulture),
                expected.ToString("P1", CultureInfo.InvariantCulture),
                simulated.ToString("P1", CultureInfo.InvariantCulture),
                (simulated - expected).ToString("+0.0 %;-0.0 %;0.0 %", CultureInfo.InvariantCulture) + " |"));
        }
    }

    private static string F(double value) => value.ToString("F4", CultureInfo.InvariantCulture);

    private static string Format(object value) => value switch
    {
        double d => d.ToString("0.##", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "-",
    };

    private sealed record RunOutcome(
        int Opportunities,
        double RecoveryRate,
        double OpportunityWeightedImdb,
        double ItemWeightedImdb,
        IReadOnlyDictionary<ReasonCode, double> ByCandidate,
        IReadOnlyDictionary<ReasonCode, double> ByRow);

    private sealed record SweepPoint(
        string Series,
        string Parameter,
        string Value,
        int Seeds,
        int Opportunities,
        double RecoveryMean,
        double RecoveryMin,
        double RecoveryMax,
        double OpportunityWeightedImdb,
        double ItemWeightedImdb,
        IReadOnlyDictionary<ReasonCode, double> ByCandidate,
        IReadOnlyDictionary<ReasonCode, double> ByRow);
}
