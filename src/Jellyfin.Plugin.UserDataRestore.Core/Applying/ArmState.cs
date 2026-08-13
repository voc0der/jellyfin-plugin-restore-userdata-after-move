using System.Globalization;

namespace Jellyfin.Plugin.UserDataRestore.Core.Applying;

/// <summary>
/// The one-time authorization an apply run consumes (DESIGN §6.3).
/// </summary>
/// <remarks>
/// <para>Arming is not a confirmation dialog. It binds a specific plan, a
/// specific write count, and a specific server to a short window, so that an
/// apply which runs against anything else refuses rather than doing its best.
/// The apply task clears it before its first write, so a crash cannot be retried
/// blind.</para>
/// <para>Everything needed to judge an arm is stored with it. A future build
/// reading an old arm must be able to reject it on its own terms rather than by
/// inference.</para>
/// </remarks>
public sealed record ArmState
{
    /// <summary>Gets the plan this arm authorizes, in full.</summary>
    public string PlanId { get; init; } = string.Empty;

    /// <summary>Gets the exact number of writes the plan contained when armed.</summary>
    public int ExpectedWriteCount { get; init; }

    /// <summary>Gets the server this arm was created on.</summary>
    public string ServerId { get; init; } = string.Empty;

    /// <summary>Gets the exact server version at arm time.</summary>
    public string ServerVersion { get; init; } = string.Empty;

    /// <summary>Gets when the arm was created.</summary>
    public DateTimeOffset ArmedUtc { get; init; }

    /// <summary>Gets when the arm stops being valid.</summary>
    public DateTimeOffset ExpiresUtc { get; init; }

    /// <summary>Gets a value indicating whether the operator acknowledged holding a backup.</summary>
    public bool BackupAcknowledged { get; init; }

    /// <summary>Gets a value indicating whether anything is armed at all.</summary>
    public bool IsPresent => !string.IsNullOrEmpty(PlanId);

    /// <summary>
    /// The phrase an operator types to arm, which encodes what they are
    /// authorizing rather than asking them to agree in the abstract.
    /// </summary>
    /// <param name="planId">The plan to authorize.</param>
    /// <param name="writeCount">The number of writes it contains.</param>
    /// <returns>The expected confirmation phrase.</returns>
    public static string Phrase(string planId, int writeCount)
    {
        ArgumentNullException.ThrowIfNull(planId);

        var shortId = planId.Length <= 12 ? planId : planId[..12];
        return string.Create(CultureInfo.InvariantCulture, $"APPLY {shortId} {writeCount}");
    }
}

/// <summary>Why an arm was refused, or that it was accepted.</summary>
/// <param name="IsValid">Whether the apply may proceed.</param>
/// <param name="Reason">The refusal, in words an operator can act on.</param>
public readonly record struct ArmVerdict(bool IsValid, string Reason)
{
    /// <summary>Gets a verdict that permits the apply.</summary>
    public static ArmVerdict Valid => new(true, string.Empty);
}

/// <summary>
/// Judges an arm against the plan and server in front of it (DESIGN §9.1 step 1).
/// </summary>
public static class ArmValidator
{
    /// <summary>
    /// Validates an arm.
    /// </summary>
    /// <param name="arm">The stored arm.</param>
    /// <param name="planId">The plan about to be applied.</param>
    /// <param name="writeCount">The writes that plan currently contains.</param>
    /// <param name="serverId">The running server's identity.</param>
    /// <param name="serverVersion">The running server's exact version.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <returns>The verdict.</returns>
    public static ArmVerdict Validate(
        ArmState? arm,
        string planId,
        int writeCount,
        string serverId,
        string serverVersion,
        DateTimeOffset nowUtc)
    {
        if (arm is null || !arm.IsPresent)
        {
            return new ArmVerdict(false, "Nothing is armed. Arm the plan on the plugin's configuration page first.");
        }

        if (!arm.BackupAcknowledged)
        {
            return new ArmVerdict(false, "The arm does not carry a backup acknowledgement.");
        }

        if (nowUtc > arm.ExpiresUtc)
        {
            return new ArmVerdict(false, "The arm has expired. Review the plan and arm it again.");
        }

        if (!string.Equals(arm.PlanId, planId, StringComparison.Ordinal))
        {
            return new ArmVerdict(false, "The armed plan is not the plan about to be applied.");
        }

        // A plan cannot change without changing its ID, so a mismatch here means
        // the count was recorded wrong rather than that the plan drifted. Refuse
        // anyway: the number is what the operator was shown and agreed to.
        if (arm.ExpectedWriteCount != writeCount)
        {
            return new ArmVerdict(
                false,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The plan contains {writeCount} writes but {arm.ExpectedWriteCount} were authorized."));
        }

        if (!string.Equals(arm.ServerId, serverId, StringComparison.Ordinal))
        {
            return new ArmVerdict(false, "The arm was created on a different server.");
        }

        if (!string.Equals(arm.ServerVersion, serverVersion, StringComparison.Ordinal))
        {
            return new ArmVerdict(
                false,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The server was {arm.ServerVersion} when armed and is {serverVersion} now."));
        }

        return ArmVerdict.Valid;
    }
}
