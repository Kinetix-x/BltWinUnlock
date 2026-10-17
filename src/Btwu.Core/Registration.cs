using System.Security.Principal;
using Microsoft.Win32;

namespace Btwu.Core;

/// <summary>
/// Registration of the native credential provider with Windows.
///
/// Two registry locations have to agree:
///   * <c>HKLM\SOFTWARE\Classes\CLSID\{clsid}\InprocServer32</c> - COM activation, points at the DLL
///     in System32 and declares the Apartment threading model LogonUI requires;
///   * <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{clsid}</c>
///     - the list Winlogon enumerates when it builds the logon screen.
///
/// Removing only one of them leaves the machine in a state where LogonUI may fail to build the
/// sign-in UI, so <see cref="Unregister"/> always removes both.
/// </summary>
public static class Registration
{
    // {B7C4E1A2-3D58-4F61-8A9E-2C7B5D40E913}
    public static readonly Guid ProviderClsid = new("B7C4E1A2-3D58-4F61-8A9E-2C7B5D40E913");
    public const string ProviderName = "Btwu 蓝牙在场解锁";
    public const string DllFileName = "BtwuCredProv.dll";

    const string ProvidersKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers";

    public static string ClsidText => "{" + ProviderClsid.ToString("D").ToUpperInvariant() + "}";
    static string ClsidKeyPath => @"SOFTWARE\Classes\CLSID\" + ClsidText;
    static string ProviderKeyPath => ProvidersKeyPath + "\\" + ClsidText;

    /// <summary>Where the DLL has to live: System32, because LogonUI loads it before any user profile exists.</summary>
    public static string SystemDllPath => Path.Combine(Environment.SystemDirectory, DllFileName);

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>DLL path COM would load for our CLSID, or null when the CLSID is not registered.</summary>
    public static string? RegisteredDllPath
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(ClsidKeyPath + @"\InprocServer32");
                return key?.GetValue(null) as string;
            }
            catch
            {
                return null;
            }
        }
    }

    public static string? RegisteredThreadingModel
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(ClsidKeyPath + @"\InprocServer32");
                return key?.GetValue("ThreadingModel") as string;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>True when the CLSID appears in the credential provider list Winlogon enumerates.</summary>
    public static bool IsListedWithWinlogon
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(ProviderKeyPath);
                return key is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool IsRegistered => RegisteredDllPath is not null && IsListedWithWinlogon;

    /// <summary>Registers (or re-points) the credential provider. Requires administrator rights.</summary>
    public static void Register(string dllPath)
    {
        if (!File.Exists(dllPath))
            throw new FileNotFoundException($"credential provider DLL not found: {dllPath}", dllPath);
        if (!IsElevated)
            throw new UnauthorizedAccessException("registering a credential provider requires administrator rights");

        using (var key = Registry.LocalMachine.CreateSubKey(ClsidKeyPath, writable: true))
        {
            key.SetValue(null, ProviderName, RegistryValueKind.String);
        }

        using (var inproc = Registry.LocalMachine.CreateSubKey(ClsidKeyPath + @"\InprocServer32", writable: true))
        {
            inproc.SetValue(null, dllPath, RegistryValueKind.String);
            // LogonUI creates the provider on an STA thread; anything else breaks the events marshaling.
            inproc.SetValue("ThreadingModel", "Apartment", RegistryValueKind.String);
        }

        using (var listed = Registry.LocalMachine.CreateSubKey(ProviderKeyPath, writable: true))
        {
            listed.SetValue(null, ProviderName, RegistryValueKind.String);
        }
    }

    /// <summary>Removes both registry footprints. Safe to call when nothing is registered.</summary>
    public static void Unregister()
    {
        if (!IsElevated)
            throw new UnauthorizedAccessException("unregistering a credential provider requires administrator rights");

        TryDelete(() => Registry.LocalMachine.DeleteSubKeyTree(ClsidKeyPath, throwOnMissingSubKey: false));
        TryDelete(() => Registry.LocalMachine.DeleteSubKeyTree(ProviderKeyPath, throwOnMissingSubKey: false));
    }

    static void TryDelete(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Warn($"registry cleanup failed: {ex.Message}"); }
    }
}
