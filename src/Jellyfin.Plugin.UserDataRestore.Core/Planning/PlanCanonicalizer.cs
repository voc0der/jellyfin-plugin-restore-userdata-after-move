using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.UserDataRestore.Core.Planning;

/// <summary>
/// Canonical serialization and the plan ID (DESIGN §8).
/// </summary>
/// <remarks>
/// <para>The plan ID is a SHA-256 over a canonical rendering of the whole
/// document with <c>planId</c> removed: object properties sorted ordinally by
/// name, no insignificant whitespace, <b>array order preserved</b>.</para>
/// <para>Array order is deliberately part of the hash. DESIGN §8 requires the
/// plan to carry "the exact ordered list of <c>ready</c> writes", and a hash that
/// sorted arrays before digesting them would let that list be reordered without
/// changing the plan ID — so a reviewed plan and an applied plan could differ in
/// execution order while claiming the same identity. Determinism instead comes
/// from <see cref="PlanBuilder"/>, which emits every array in a defined order and
/// deduplicates the ones that mean a set, so the same inputs still produce the
/// same ID no matter what order the analyzer visited rows in or how many times
/// the host repeated itself.</para>
/// <para>Everything except the ID is covered. Picking a subset of
/// "safety-relevant" fields would mean deciding in advance which parts of a plan
/// an operator is allowed to have tampered with.</para>
/// </remarks>
public static class PlanCanonicalizer
{
    private const string PlanIdProperty = "planId";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.Strict,
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions ReadableOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    /// <summary>
    /// Computes the canonical plan ID.
    /// </summary>
    /// <param name="plan">The plan to identify.</param>
    /// <returns>A lowercase hex SHA-256 digest.</returns>
    public static string ComputePlanId(PlanDocument plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var canonical = Canonicalize(plan);
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// Returns the plan with its computed ID attached.
    /// </summary>
    /// <param name="plan">The plan to seal.</param>
    /// <returns>The same plan, with <see cref="PlanDocument.PlanId"/> set.</returns>
    public static PlanDocument Seal(PlanDocument plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan with { PlanId = ComputePlanId(plan) };
    }

    /// <summary>
    /// Verifies that a plan's ID matches its contents.
    /// </summary>
    /// <param name="plan">The plan to check.</param>
    /// <returns><see langword="true"/> when the plan has not been altered since it was sealed.</returns>
    public static bool VerifyPlanId(PlanDocument plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return !string.IsNullOrEmpty(plan.PlanId)
            && string.Equals(plan.PlanId, ComputePlanId(plan), StringComparison.Ordinal);
    }

    /// <summary>
    /// Renders the canonical form used for hashing.
    /// </summary>
    /// <param name="plan">The plan to render.</param>
    /// <returns>Canonical JSON, without the plan ID.</returns>
    public static string Canonicalize(PlanDocument plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var node = JsonSerializer.SerializeToNode(plan, SerializerOptions)
            ?? throw new InvalidOperationException("Plan serialized to null.");

        if (node is JsonObject root)
        {
            root.Remove(PlanIdProperty);
        }

        return Sort(node)!.ToJsonString(SerializerOptions);
    }

    /// <summary>
    /// Renders the plan as it is written to disk: indented, ID included.
    /// </summary>
    /// <param name="plan">The plan to render.</param>
    /// <returns>Human-reviewable JSON.</returns>
    public static string ToReadableJson(PlanDocument plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return JsonSerializer.Serialize(plan, ReadableOptions);
    }

    /// <summary>
    /// Parses a plan from disk.
    /// </summary>
    /// <param name="json">The plan JSON.</param>
    /// <returns>The parsed plan.</returns>
    public static PlanDocument FromJson(string json) =>
        JsonSerializer.Deserialize<PlanDocument>(json, ReadableOptions)
            ?? throw new InvalidOperationException("Plan JSON parsed to null.");

    // Every branch returns a fresh node rather than reparenting the original: a
    // JsonNode may have only one parent, and moving them in place turns a sort
    // into a mutation of the document being sorted.
    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject obj => SortObject(obj),

        // Arrays are rebuilt in place, not reordered. See the class remarks.
        JsonArray array => CloneArray(array),
        _ => node?.DeepClone(),
    };

    private static JsonNode SortObject(JsonObject obj)
    {
        var sorted = new JsonObject();
        foreach (var (name, value) in obj.OrderBy(property => property.Key, StringComparer.Ordinal))
        {
            sorted[name] = Sort(value);
        }

        return sorted;
    }

    private static JsonNode CloneArray(JsonArray array)
    {
        var clone = new JsonArray();
        foreach (var child in array)
        {
            clone.Add(Sort(child));
        }

        return clone;
    }
}
