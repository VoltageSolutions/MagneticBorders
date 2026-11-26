using System.Reflection;

namespace MagneticBorders.Settings.Core;

/// <summary>Application version read from the build-stamped AssemblyInformationalVersionAttribute; Version has any "+" build metadata removed. Shown in the About card.</summary>
public sealed record AppInfo(string Version)
{
    /// <summary>Reads the version from an assembly, or "0.0.0" when the attribute is missing.</summary>
    public static AppInfo FromAssembly(Assembly assembly)
    {
        var v = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = v.IndexOf('+');
        return new AppInfo(plus >= 0 ? v[..plus] : v);
    }

    /// <summary>Version of the running exe.</summary>
    public static AppInfo FromEntryAssembly() => FromAssembly(Assembly.GetEntryAssembly()!);
}
