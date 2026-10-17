using System.Runtime.InteropServices;
using System.Text;

namespace Btwu.Core;

public sealed record StoredCredential(string UserName, string Domain, string Password, string Sid = "")
{
    public string DomainOrLocalMachine => string.IsNullOrWhiteSpace(Domain) || Domain == "."
        ? Environment.MachineName
        : Domain;
}

/// <summary>
/// Stores the Windows account password the credential provider replays at the lock screen.
///
/// Protected with DPAPI (machine scope) plus a fixed application entropy value. Machine scope is
/// deliberate: the credential provider runs as SYSTEM inside LogonUI and the enrollment CLI runs in
/// the administrator's token, and both must be able to read the same blob. Confidentiality therefore
/// rests on the file ACL applied by scripts/install.ps1, which leaves cred.bin readable only by
/// SYSTEM and Administrators.
///
/// Consequence, stated plainly: an administrator on this machine - or anyone who can copy the disk
/// and obtain the machine DPAPI keys - can recover the password. There is no way around that for a
/// fully automatic unlock, because the unlock itself has to replay the password.
/// </summary>
public static class CredentialStore
{
    const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    const int CRYPTPROTECT_LOCAL_MACHINE = 0x4;

    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Btwu.v1.credential");

    [StructLayout(LayoutKind.Sequential)]
    struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>Payload layout, also parsed by the C++ credential provider: user \n domain \n password \n sid.</summary>
    public static void Save(StoredCredential credential)
    {
        if (credential.Password.Contains('\n') || credential.UserName.Contains('\n')
            || credential.Domain.Contains('\n') || credential.Sid.Contains('\n'))
            throw new ArgumentException("user, domain, sid and password must not contain newlines");

        var plain = Encoding.UTF8.GetBytes(
            $"{credential.UserName}\n{credential.Domain}\n{credential.Password}\n{credential.Sid}");
        try
        {
            var blob = Protect(plain);
            Paths.EnsureDataDir();
            File.WriteAllBytes(Paths.CredentialFile, blob);
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    public static StoredCredential? TryLoad()
    {
        try
        {
            if (!File.Exists(Paths.CredentialFile)) return null;
            var plain = Unprotect(File.ReadAllBytes(Paths.CredentialFile));
            string text;
            try { text = Encoding.UTF8.GetString(plain); }
            finally { Array.Clear(plain); }

            var parts = text.Split('\n');
            if (parts.Length < 3) return null;
            return new StoredCredential(parts[0], parts[1], parts[2], parts.Length > 3 ? parts[3] : "");
        }
        catch (Exception ex)
        {
            Log.Error($"cannot decrypt stored credential: {ex.Message}");
            return null;
        }
    }

    public static bool Exists => File.Exists(Paths.CredentialFile);

    /// <summary>
    /// True when the blob is there but this process is not allowed to open it. After install.ps1 the
    /// ACL is SYSTEM + Administrators only, so an ordinary prompt lands here - and the fix is "elevate",
    /// not "re-enroll". Conflating the two sends people down the wrong path.
    /// </summary>
    public static bool ExistsButUnreadable()
    {
        if (!File.Exists(Paths.CredentialFile)) return false;
        try
        {
            using var probe = File.OpenRead(Paths.CredentialFile);
            return false;
        }
        catch (UnauthorizedAccessException) { return true; }
        catch { return false; }
    }

    public static void Delete()
    {
        try { if (File.Exists(Paths.CredentialFile)) File.Delete(Paths.CredentialFile); }
        catch (Exception ex) { Log.Warn($"cannot delete credential: {ex.Message}"); }
    }

    static byte[] Protect(byte[] plain)
    {
        var inBlob = ToBlob(plain);
        var entropy = ToBlob(Entropy);
        try
        {
            if (!CryptProtectData(ref inBlob, "Btwu credential", ref entropy, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN | CRYPTPROTECT_LOCAL_MACHINE, out var outBlob))
                throw new InvalidOperationException($"CryptProtectData failed: {Marshal.GetLastWin32Error()}");
            try { return FromBlob(outBlob); }
            finally { LocalFree(outBlob.pbData); }
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
            Marshal.FreeHGlobal(entropy.pbData);
        }
    }

    static byte[] Unprotect(byte[] protectedBytes)
    {
        var inBlob = ToBlob(protectedBytes);
        var entropy = ToBlob(Entropy);
        try
        {
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN | CRYPTPROTECT_LOCAL_MACHINE, out var outBlob))
                throw new InvalidOperationException($"CryptUnprotectData failed: {Marshal.GetLastWin32Error()}");
            try { return FromBlob(outBlob); }
            finally { LocalFree(outBlob.pbData); }
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
            Marshal.FreeHGlobal(entropy.pbData);
        }
    }

    static DataBlob ToBlob(byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return new DataBlob { cbData = data.Length, pbData = ptr };
    }

    static byte[] FromBlob(DataBlob blob)
    {
        var result = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, result, 0, blob.cbData);
        return result;
    }
}
