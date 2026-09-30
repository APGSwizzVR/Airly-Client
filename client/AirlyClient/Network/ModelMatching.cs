namespace AirlyClient.Network;
public sealed record ModelMatchEntry(string ModelCode,string Manufacturer,string AircraftType,string SimulatorPackage,string PackageIdentifier,string? FallbackModelCode=null);
public sealed class ModelMatcher
{
    private readonly Dictionary<string,ModelMatchEntry> _entries=new(StringComparer.OrdinalIgnoreCase);
    public void ReplaceCatalog(IEnumerable<ModelMatchEntry> entries){_entries.Clear();foreach(var e in entries)_entries[e.ModelCode]=e;}
    public ModelMatchEntry? Resolve(string code,string simulator){if(_entries.TryGetValue(code,out var e)&&e.SimulatorPackage.Equals(simulator,StringComparison.OrdinalIgnoreCase))return e;if(_entries.TryGetValue(code,out var f)&&f.FallbackModelCode is not null&&_entries.TryGetValue(f.FallbackModelCode,out var x))return x;return null;}
}