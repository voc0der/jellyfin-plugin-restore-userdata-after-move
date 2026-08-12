using System.Globalization;
using System.Text;
using Jellyfin.Plugin.UserDataRestore.Core.Analysis;
using Jellyfin.Plugin.UserDataRestore.Core.Model;

namespace Jellyfin.Plugin.UserDataRestore.Sweep;

/// <summary>
/// Runs the analyzer across a range of library shapes and reports how recovery
/// responds to each one (PLAN §2).
/// </summary>
/// <remarks>
/// The output is deliberately a curve rather than a number. Any single synthetic
/// library answers "what does the analyzer do on the library I invented", which
/// is not the go/no-go question. A response surface answers "which libraries is
/// this worth building the write path for", which a real installation can then
/// check itself against by measuring its own provider coverage.
/// </remarks>
public static class Program
{
    // Two levels, and they are not interchangeable. A row that matched nothing or
    // matched two things never becomes a candidate at all, so those codes only ever
    // appear in the row counts; the codes below them are verdicts on a candidate
    // that did form. Reading either off the wrong dictionary reports zero forever.
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
        ReasonCode.UnsupportedCurrentItem,
        ReasonCode.PathOutsideFinalScope,
    ];

    /// <summary>
    /// Entry point.
    /// </summary>
    /// <param name="args">Optional output path for the CSV.</param>
    public static void Main(string[] args)
    {
        var csvPath = args is [var first, ..] ? first : "evidence/sweep/sweep.csv";
        var baseline = new LibraryShape();
        var results = new List<SweepRow> { Run("baseline", "-", "-", baseline) };

        foreach (var value in Fractions())
        {
            results.Add(Run("imdb_coverage", "imdbCoverage", value, baseline with { ImdbCoverage = value }));
        }

        // With no IMDb anywhere, a movie's only provider key is a bare TMDb number
        // and DESIGN §7.3 refuses it. This series exists to show what that costs.
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

        Write(csvPath, results);
        Print(results);
        CheckModel();
    }

    // The one-at-a-time series suggest the three losses are independent and
    // multiplicative. That is a claim, so it gets tested on configurations the
    // series never visited rather than eyeballed off the curves.
    private static void CheckModel()
    {
        LibraryShape[] held =
        [
            new() { ImdbCoverage = 0.7, Duplication = 0.2, CurrentStateFraction = 0.3 },
            new() { ImdbCoverage = 0.4, Duplication = 0.35, CurrentStateFraction = 0.1 },
            new() { ImdbCoverage = 0.95, Duplication = 0.05, CurrentStateFraction = 0.5 },
            new() { ImdbCoverage = 0.55, Duplication = 0.1, CurrentStateFraction = 0.75, EpisodeShare = 0.2 },
        ];

        Console.WriteLine();
        Console.WriteLine("Model: rate ~= imdbCoverage x (1 - duplication) x (1 - currentStateFraction)");
        Console.WriteLine();
        Console.WriteLine("| imdb | duplication | currentState | predicted | actual | error |");
        Console.WriteLine("|---|---|---|---|---|---|");

        foreach (var shape in held)
        {
            var predicted = shape.ImdbCoverage * (1 - shape.Duplication) * (1 - shape.CurrentStateFraction);
            var actual = Run("model_check", "-", "-", shape).RecoveryRate;

            Console.WriteLine(string.Join(" | ",
                "| " + shape.ImdbCoverage.ToString("0.##", CultureInfo.InvariantCulture),
                shape.Duplication.ToString("0.##", CultureInfo.InvariantCulture),
                shape.CurrentStateFraction.ToString("0.##", CultureInfo.InvariantCulture),
                predicted.ToString("P1", CultureInfo.InvariantCulture),
                actual.ToString("P1", CultureInfo.InvariantCulture),
                (actual - predicted).ToString("+0.0 %;-0.0 %;0.0 %", CultureInfo.InvariantCulture) + " |"));
        }
    }

    private static IEnumerable<double> Fractions()
    {
        for (var step = 0; step <= 10; step++)
        {
            yield return step / 10.0;
        }
    }

    private static SweepRow Run(string series, string parameter, object value, LibraryShape shape)
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
        var byCandidate = CandidateCodes.ToDictionary(code => code, code => result.CandidateCounts.GetValueOrDefault(code));
        var byRow = RowCodes.ToDictionary(code => code, code => result.RowCounts.GetValueOrDefault(code));

        // Opportunities, not candidates, is the honest denominator: state that was
        // stranded and never produced a candidate at all — because every key it had
        // was a dead GUID — is exactly the loss the go/no-go is asking about.
        var ready = byCandidate[ReasonCode.Ready];
        var rate = population.Opportunities == 0 ? 0 : (double)ready / population.Opportunities;

        return new SweepRow(series, parameter, Format(value), population.Opportunities, byCandidate, byRow, rate);
    }

    private static void Write(string path, IReadOnlyList<SweepRow> rows)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var csv = new StringBuilder();
        csv.Append("series,parameter,value,opportunities,recoveryRate");
        foreach (var code in CandidateCodes)
        {
            csv.Append(",candidates.").Append(ReasonCodes.ToWire(code));
        }

        foreach (var code in RowCodes)
        {
            csv.Append(",rows.").Append(ReasonCodes.ToWire(code));
        }

        csv.AppendLine();

        foreach (var row in rows)
        {
            csv.Append(row.Series).Append(',')
               .Append(row.Parameter).Append(',')
               .Append(row.Value).Append(',')
               .Append(row.Opportunities.ToString(CultureInfo.InvariantCulture)).Append(',')
               .Append(row.RecoveryRate.ToString("F4", CultureInfo.InvariantCulture));

            foreach (var code in CandidateCodes)
            {
                csv.Append(',').Append(row.ByCandidate[code].ToString(CultureInfo.InvariantCulture));
            }

            foreach (var code in RowCodes)
            {
                csv.Append(',').Append(row.ByRow[code].ToString(CultureInfo.InvariantCulture));
            }

            csv.AppendLine();
        }

        File.WriteAllText(path, csv.ToString());
        Console.WriteLine("Wrote " + path);
    }

    private static void Print(IReadOnlyList<SweepRow> rows)
    {
        Console.WriteLine();
        Console.WriteLine("| series | value | opportunities | recovered | rate | insufficient | conflict | ambiguous rows | dead-guid rows |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|");

        foreach (var row in rows)
        {
            Console.WriteLine(string.Join(" | ",
                "| " + row.Series,
                row.Value,
                row.Opportunities.ToString(CultureInfo.InvariantCulture),
                row.ByCandidate[ReasonCode.Ready].ToString(CultureInfo.InvariantCulture),
                row.RecoveryRate.ToString("P1", CultureInfo.InvariantCulture),
                row.ByCandidate[ReasonCode.InsufficientIdentityEvidence].ToString(CultureInfo.InvariantCulture),
                row.ByCandidate[ReasonCode.CurrentStateConflict].ToString(CultureInfo.InvariantCulture),
                row.ByRow[ReasonCode.AmbiguousCurrentKey].ToString(CultureInfo.InvariantCulture),
                row.ByRow[ReasonCode.NoCurrentKeyMatch].ToString(CultureInfo.InvariantCulture) + " |"));
        }
    }

    private static string Format(object value) => value switch
    {
        double d => d.ToString("0.##", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "-",
    };

    private sealed record SweepRow(
        string Series,
        string Parameter,
        string Value,
        int Opportunities,
        IReadOnlyDictionary<ReasonCode, int> ByCandidate,
        IReadOnlyDictionary<ReasonCode, int> ByRow,
        double RecoveryRate);
}
