using System.Reflection;

namespace AirlyClient;

public static class ClientConfig
{
    public const string ApiBaseUrl = "https://airly-network.vercel.app/";
    public static string Version => Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
    public const string ReleaseRepository = "APGSwizzVR/Airly-Client";
}
