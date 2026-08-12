namespace Jellyfin.Plugin.UserDataRestore.Core.Model;

/// <summary>
/// The result of annotating one key against one current item.
/// </summary>
/// <param name="Evidence">How strongly the key identifies the item.</param>
/// <param name="ProviderName">The provider whose ID produced the key, when known.</param>
/// <param name="SeriesGuidDerived">
/// Whether the key is the current series' GUID plus this episode's padded season
/// and episode numbers. Recorded for the go/no-go review only; DESIGN §7.3 does
/// not list it among the sufficient identity cases.
/// </param>
public readonly record struct KeyEvidenceResult(KeyEvidence Evidence, string? ProviderName, bool SeriesGuidDerived);
