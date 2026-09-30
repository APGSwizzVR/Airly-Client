namespace AirlyClient.Network;

public sealed record ModelMatchEntry(string ModelCode, string Manufacturer, string AircraftType, string SimulatorPackage, string? FallbackModelCode = null);

public sealed class ModelMatcher
{
    private readonly Dictionary<string, ModelMatchEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public void ReplaceCatalog(IEnumerable<ModelMatchEntry> entries)
    {
        _entries.Clear();
        foreach (var entry in entries)
            _entries[entry.ModelCode] = entry;
    }

    public ModelMatchEntry? Resolve(string modelCode, string simulatorPackage)
    {
        if (_entries.TryGetValue(modelCode, out var exact) &&
            exact.SimulatorPackage.Equals(simulatorPackage, StringComparison.OrdinalIgnoreCase))
            return exact;

        if (_entries.TryGetValue(modelCode, out var fallback) && !string.IsNullOrWhiteSpace(fallback.FallbackModelCode) &&
            _entries.TryGetValue(fallback.FallbackModelCode, out var fallbackEntry))
            return fallbackEntry;

        return _entries.Values.FirstOrDefault(x => x.SimulatorPackage.Equals(simulatorPackage, StringComparison.OrdinalIgnoreCase));
    }
}
