namespace EstateHub.Application.Features;

public record FeatureDefinition(string Key, bool DefaultEnabled, IReadOnlyList<string> Requires);

/// <summary>
/// Every feature an estate can switch on or off (PHASE1.md §3). Defined in code: a new module adds
/// its key here and puts [RequiresFeature("key")] on its gRPC service. Estates override the default
/// per feature (EstateFeature rows); a feature whose dependencies are off is effectively off.
/// </summary>
public static class FeatureCatalog
{
    public const string Billing = "billing";
    public const string OnlinePayments = "online_payments";
    public const string Maintenance = "maintenance";
    public const string Notices = "notices";
    public const string VisitorRegister = "visitor_register";
    public const string Parking = "parking";
    public const string GateControl = "gate_control";
    public const string ShortStays = "short_stays";
    public const string Biometrics = "biometrics";
    public const string Electricity = "electricity";

    private static readonly FeatureDefinition[] Definitions =
    [
        new(Billing, DefaultEnabled: true, Requires: []),
        new(OnlinePayments, DefaultEnabled: false, Requires: [Billing]),
        new(Maintenance, DefaultEnabled: true, Requires: []),
        new(Notices, DefaultEnabled: true, Requires: []),
        new(VisitorRegister, DefaultEnabled: true, Requires: []),
        new(Parking, DefaultEnabled: false, Requires: []),
        new(GateControl, DefaultEnabled: false, Requires: [VisitorRegister]),
        new(ShortStays, DefaultEnabled: false, Requires: [OnlinePayments]),
        new(Biometrics, DefaultEnabled: false, Requires: [GateControl]),
        new(Electricity, DefaultEnabled: false, Requires: []),
    ];

    private static readonly Dictionary<string, FeatureDefinition> ByKey =
        Definitions.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public static IReadOnlyList<FeatureDefinition> All => Definitions;

    public static bool Exists(string key) => ByKey.ContainsKey(key);

    public static FeatureDefinition Get(string key) =>
        ByKey.TryGetValue(key, out var definition)
            ? definition
            : throw new ArgumentException($"Unknown feature '{key}'.", nameof(key));

    /// <summary>
    /// Effective features: catalogue defaults, then estate overrides, then any feature whose
    /// dependencies are not all enabled is dropped (transitively).
    /// </summary>
    public static IReadOnlySet<string> Resolve(IReadOnlyDictionary<string, bool> overrides)
    {
        var requested = Definitions
            .Where(d => overrides.TryGetValue(d.Key, out var enabled) ? enabled : d.DefaultEnabled)
            .Select(d => d.Key)
            .ToHashSet(StringComparer.Ordinal);

        bool removed;
        do
        {
            removed = requested.RemoveWhere(key => ByKey[key].Requires.Any(r => !requested.Contains(r))) > 0;
        } while (removed);

        return requested;
    }
}
