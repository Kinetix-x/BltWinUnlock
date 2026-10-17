// Test harness for BtwuCredProv.dll.
//
// Loads the credential provider in-process and drives it the way LogonUI does, without registering
// anything. It prepares its own scratch state (a fake presence.dat + a DPAPI-protected cred.bin) in
// a temporary directory and points this process' ProgramData at it, so it never touches real state.
//
//   harness.exe [--keep]
//   harness.exe --live
//
// Default mode prepares its own scratch state in %TEMP% and asserts against it. `--live` instead
// drives the real DLL against the real %ProgramData%\BtwuUnlock and reports what the lock screen
// would decide right now. That is the closest thing to a lock-screen test that can run without
// registering the provider.
//
// `--live` never writes presence.dat, cred.bin, config.json or pause.until - the state under test is
// untouched. The one file it does touch is the DLL's own credprov.log, because that is what the DLL
// does on any SetUsageScenario call, exactly as it would inside LogonUI.
//
// The interesting assertion is the last block: the packed KERB_INTERACTIVE_UNLOCK_LOGON must carry
// byte OFFSETS in each UNICODE_STRING.Buffer, not addresses. LSA receives a copy of the blob, so an
// address there would dangle and the unlock would be rejected as malformed - a failure that is
// completely silent at the lock screen. This harness fails loudly instead.
//
// Output is ASCII on purpose: it is meant to be readable in any console code page.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <credentialprovider.h>
#include <ntsecapi.h>
#include <wincrypt.h>
#include <dpapi.h>
#include <shlwapi.h>

#include <cstdio>
#include <string>

// Shared with the DLL on purpose: the live mode has to report what the very same code decides.
#include "../PresenceState.h"

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "crypt32.lib")
#pragma comment(lib, "shlwapi.lib")

// {B7C4E1A2-3D58-4F61-8A9E-2C7B5D40E913}
static const CLSID CLSID_BtwuCredProv =
{ 0xb7c4e1a2, 0x3d58, 0x4f61, { 0x8a, 0x9e, 0x2c, 0x7b, 0x5d, 0x40, 0xe9, 0x13 } };

static const wchar_t* kHarnessUser = L"HarnessUser";
static const wchar_t* kHarnessPassword = L"HarnessPassw0rd!";

static int g_passed = 0;
static int g_failed = 0;

static void Check(bool condition, const char* format, ...)
{
    char message[512]{};
    va_list args;
    va_start(args, format);
    _vsnprintf_s(message, _TRUNCATE, format, args);
    va_end(args);

    if (condition)
    {
        g_passed++;
        printf("  [ok]   %s\n", message);
    }
    else
    {
        g_failed++;
        printf("  [FAIL] %s\n", message);
    }
}

static void Section(const char* name)
{
    printf("\n--- %s ---\n", name);
}

// ------------------------------------------------------------------------------- scratch state

static std::wstring g_scratch;

static bool WritePresence(bool present, long long suppressionSeconds)
{
    const std::wstring dir = g_scratch + L"\\BtwuUnlock";
    CreateDirectoryW(g_scratch.c_str(), nullptr);
    CreateDirectoryW(dir.c_str(), nullptr);

    FILETIME ft{};
    GetSystemTimeAsFileTime(&ft);
    ULARGE_INTEGER value;
    value.LowPart = ft.dwLowDateTime;
    value.HighPart = ft.dwHighDateTime;
    const long long now = static_cast<long long>(value.QuadPart) + 504911232000000000LL;

    char text[256]{};
    sprintf_s(text, "BTWU1 %d %lld %lld %lld 1 0\n", present ? 1 : 0, present ? now : 0, now,
              suppressionSeconds > 0 ? now + suppressionSeconds * 10000000LL : 0);

    const std::wstring path = dir + L"\\presence.dat";
    const HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                    nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    DWORD written = 0;
    const BOOL ok = WriteFile(file, text, static_cast<DWORD>(strlen(text)), &written, nullptr);
    CloseHandle(file);
    return ok == TRUE;
}

// The SID of the account this process runs as, taken straight from its own token. Deliberately an
// independent source of truth: the DLL matches cred.bin's SID against Btwu::SessionUserSid(), so if
// the harness only ever asked the same helper, a bug inside it would go unnoticed.
static std::wstring ProcessUserSid()
{
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return L"";

    BYTE buffer[SECURITY_MAX_SID_SIZE]{};
    DWORD bytes = 0;
    const BOOL ok = GetTokenInformation(token, TokenUser, buffer, sizeof(buffer), &bytes);
    CloseHandle(token);
    if (!ok) return L"";

    LPWSTR text = nullptr;
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer)->User.Sid, &text)) return L"";
    std::wstring sid = text;
    LocalFree(text);
    return sid;
}

static bool WriteCredential(const std::wstring& sid)
{
    const std::wstring dir = g_scratch + L"\\BtwuUnlock";
    CreateDirectoryW(dir.c_str(), nullptr);

    // Exactly the payload Btwu.Core.CredentialStore writes: user \n domain \n password \n sid
    char payload[512]{};
    sprintf_s(payload, "%ls\n.\n%ls\n%ls", kHarnessUser, kHarnessPassword, sid.c_str());

    DATA_BLOB in{};
    in.cbData = static_cast<DWORD>(strlen(payload));
    in.pbData = reinterpret_cast<BYTE*>(payload);

    static const char kEntropyText[] = "Btwu.v1.credential";
    DATA_BLOB entropy{};
    entropy.cbData = static_cast<DWORD>(sizeof(kEntropyText) - 1);
    entropy.pbData = reinterpret_cast<BYTE*>(const_cast<char*>(kEntropyText));

    DATA_BLOB out{};
    if (!CryptProtectData(&in, L"Btwu harness", &entropy, nullptr, nullptr,
                          CRYPTPROTECT_UI_FORBIDDEN | CRYPTPROTECT_LOCAL_MACHINE, &out))
    {
        printf("cannot DPAPI-protect the test credential (%lu)\n", GetLastError());
        return false;
    }

    const std::wstring path = dir + L"\\cred.bin";
    const HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr,
                                    CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        LocalFree(out.pbData);
        return false;
    }
    DWORD written = 0;
    const BOOL ok = WriteFile(file, out.pbData, out.cbData, &written, nullptr);
    CloseHandle(file);
    LocalFree(out.pbData);
    SecureZeroMemory(payload, sizeof(payload));
    return ok == TRUE;
}

// ------------------------------------------------------------------------------------ main

using PFN_DllGetClassObject = HRESULT(STDAPICALLTYPE*)(REFCLSID, REFIID, void**);
using PFN_DllCanUnloadNow = HRESULT(STDAPICALLTYPE*)();

// NOTE: in the packed form UNICODE_STRING.Buffer is an offset, so it must never be dereferenced
// directly. Reading a packed string means "blob + offset", bounds checked against the blob size -
// exactly the step LSA performs, and exactly the step whose absence crashes a naive consumer.
static std::wstring ReadPackedString(const BYTE* blob, ULONG blobBytes, const UNICODE_STRING& value)
{
    const ULONG_PTR offset = reinterpret_cast<ULONG_PTR>(value.Buffer);
    if (value.Length == 0) return L"";
    if (offset + value.Length > blobBytes) return L"<out of range>";
    return std::wstring(reinterpret_cast<const wchar_t*>(blob + offset), value.Length / sizeof(wchar_t));
}

// ----------------------------------------------------------------------------------- live report

// .NET DateTime ticks (100ns since 0001-01-01) in local wall-clock form.
static std::wstring LocalTimeFromDotNetTicks(long long ticks)
{
    if (ticks <= 0) return L"-";

    ULARGE_INTEGER value;
    value.QuadPart = static_cast<unsigned long long>(ticks - Btwu::kFileTimeToDotNetTicks);

    FILETIME utcFileTime{ value.LowPart, value.HighPart };
    SYSTEMTIME utc{}, local{};
    if (!FileTimeToSystemTime(&utcFileTime, &utc)) return L"<invalid>";
    if (!SystemTimeToTzSpecificLocalTime(nullptr, &utc, &local)) return L"<invalid>";

    wchar_t buffer[64]{};
    swprintf_s(buffer, L"%04u-%02u-%02u %02u:%02u:%02u",
               local.wYear, local.wMonth, local.wDay, local.wHour, local.wMinute, local.wSecond);
    return buffer;
}

// Mirrors Btwu::ShouldUnlock but names the clause that decided, so a FALSE is never a mystery.
static const char* VerdictReason(const Btwu::Presence& presence)
{
    if (!presence.Valid) return "state file missing or unparsable -> fail closed";
    if (!presence.Present) return "trigger device not present";
    if (!presence.AutoUnlock) return "autoUnlock=0 in the published state";

    const long long now = Btwu::NowDotNetTicks();
    if (now - presence.HeartbeatTicks > Btwu::kMaxAgeTicks) return "heartbeat stale -> agent gone or hung";
    if (now < presence.SuppressUntilTicks) return "suppressed -> paused, or a previous unlock failed";
    if (presence.UnlockWindowSeconds > 0 &&
        now - presence.SinceTicks > static_cast<long long>(presence.UnlockWindowSeconds) * Btwu::kTicksPerSecond)
        return "present for longer than the unlock window";
    return "would auto-unlock";
}

// Drives the real DLL against the real data directory. Read only.
static int ReportLive(ICredentialProvider* provider)
{
    Section("LIVE - the real data directory, the real DLL, nothing written");

    printf("  data dir          : %ls\n", Btwu::DataDirectory().c_str());
    printf("  presence.dat      : %s\n",
           GetFileAttributesW(Btwu::PresenceFilePath().c_str()) != INVALID_FILE_ATTRIBUTES ? "exists" : "MISSING");
    printf("  cred.bin          : %s\n",
           GetFileAttributesW(Btwu::CredentialFilePath().c_str()) != INVALID_FILE_ATTRIBUTES ? "exists" : "MISSING");
    printf("  pause.until       : %s\n",
           GetFileAttributesW(Btwu::PauseFilePath().c_str()) != INVALID_FILE_ATTRIBUTES ? "exists" : "absent");
    printf("  disable.cp        : %s\n",
           GetFileAttributesW(Btwu::DisableFilePath().c_str()) != INVALID_FILE_ATTRIBUTES ? "PRESENT - provider off" : "absent");
    printf("  autosubmit marker : %s\n",
           Btwu::AutoSubmitUnlock() ? "present - LogonUI submits on its own"
                                    : "absent - default tile, the user presses Enter");

    // The identity check, reported before the provider is asked anything, because it decides whether a
    // credential can be offered at all. `btwu enroll` writes the SID; the lock screen needs it to be
    // the SID of the account signed in here.
    {
        std::wstring sessionSid;
        const bool resolved = Btwu::SessionUserSid(sessionSid);
        wchar_t user[Btwu::kMaxSessionNameChars]{};
        const bool signedIn = Btwu::QuerySessionUser(user, Btwu::kMaxSessionNameChars);
        printf("  session user      : %ls (signedIn=%d)\n", signedIn ? user : L"<none>", signedIn ? 1 : 0);
        printf("  its SID           : %ls\n",
               resolved ? sessionSid.c_str() : L"<cannot resolve - the provider will stay invisible>");
    }

    // Print the raw line before the parsed fields: if the writer and this parser ever drift apart,
    // seeing the actual bytes is the fastest way to spot it.
    const HANDLE file = CreateFileW(Btwu::PresenceFilePath().c_str(), GENERIC_READ,
                                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                    nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file != INVALID_HANDLE_VALUE)
    {
        char raw[256]{};
        DWORD read = 0;
        if (ReadFile(file, raw, sizeof(raw) - 1, &read, nullptr) && read > 0)
        {
            while (read > 0 && (raw[read - 1] == '\n' || raw[read - 1] == '\r')) raw[--read] = 0;
            printf("  raw presence.dat  : \"%s\"\n", raw);
        }
        CloseHandle(file);
    }

    const Btwu::Presence presence = Btwu::ReadPresence();

    if (presence.Valid)
    {
        const long long ageTicks = Btwu::NowDotNetTicks() - presence.HeartbeatTicks;
        printf("  Present           : %d\n", presence.Present ? 1 : 0);
        printf("  Since             : %ls\n", LocalTimeFromDotNetTicks(presence.SinceTicks).c_str());
        printf("  Heartbeat         : %ls  (%.1f s ago, stale after %.0f s)\n",
               LocalTimeFromDotNetTicks(presence.HeartbeatTicks).c_str(),
               static_cast<double>(ageTicks) / 10000000.0,
               static_cast<double>(Btwu::kMaxAgeTicks) / 10000000.0);
        printf("  SuppressUntil     : %ls\n", LocalTimeFromDotNetTicks(presence.SuppressUntilTicks).c_str());
        printf("  AutoUnlock        : %d\n", presence.AutoUnlock ? 1 : 0);
        printf("  UnlockWindow      : %d s (0 = unlock whenever present)\n", presence.UnlockWindowSeconds);
    }

    const bool unlock = Btwu::ShouldUnlock(presence);
    printf("  ShouldUnlock      : %s   -- %s\n", unlock ? "TRUE" : "FALSE", VerdictReason(presence));

    // Ask the DLL the same question LogonUI asks - under BOTH scenarios, because since Windows 10
    // LogonUI uses CPUS_LOGON at the lock screen. Both must give the same answer: the classic scenario
    // is the older Windows path, the merged one is what this machine's lock screen actually uses.
    //
    //   count=1, autoLogon=0  -> our tile is selected; the user presses Enter and the stored password
    //                            is submitted. The lock screen keeps its own UI throughout.
    //   count=1, autoLogon=1  -> the autosubmit marker is present and LogonUI submits on its own.
    //   count=0               -> nothing is offered, and the lock screen is exactly as Windows made it.
    provider->SetUsageScenario(CPUS_UNLOCK_WORKSTATION, 0);
    DWORD count = 99, defaultIndex = 99;
    BOOL autoLogon = TRUE;
    const HRESULT hr = provider->GetCredentialCount(&count, &defaultIndex, &autoLogon);

    printf("\n  classic scenario  : hr=0x%08X count=%u pdwDefault=%u pbAutoLogon=%d\n",
           static_cast<unsigned>(hr), count,
           static_cast<unsigned>(defaultIndex), autoLogon);

    if (count == 1)
    {
        ICredentialProviderCredential* credential = nullptr;
        if (SUCCEEDED(provider->GetCredentialAt(0, &credential)) && credential != nullptr)
        {
            CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE response = CPGSR_NO_CREDENTIAL_NOT_FINISHED;
            CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION serialization{};
            PWSTR statusText = nullptr;
            CREDENTIAL_PROVIDER_STATUS_ICON icon = CPSI_NONE;

            const HRESULT serializationHr =
                credential->GetSerialization(&response, &serialization, &statusText, &icon);

            printf("  GetSerialization  : hr=0x%08X response=%d%s\n",
                   static_cast<unsigned>(serializationHr), response,
                   SUCCEEDED(serializationHr)
                       ? "  -> a real unlock WOULD be submitted (under the classic scenario)"
                       : "  -> nothing would be submitted (is cred.bin enrolled?)");

            if (serialization.rgbSerialization != nullptr) CoTaskMemFree(serialization.rgbSerialization);
            credential->Release();
        }
    }

    provider->SetUsageScenario(CPUS_LOGON, 0);
    DWORD mergedCount = 99, mergedDefault = 99;
    BOOL mergedAutoLogon = TRUE;
    const HRESULT mergedHr = provider->GetCredentialCount(&mergedCount, &mergedDefault, &mergedAutoLogon);
    printf("  merged scenario   : hr=0x%08X count=%u pdwDefault=%u pbAutoLogon=%d\n",
           static_cast<unsigned>(mergedHr), mergedCount,
           static_cast<unsigned>(mergedDefault), mergedAutoLogon);
    printf("                      (this is the scenario this machine's lock screen really uses)\n");

    Section("live assertions");
    Check(SUCCEEDED(hr), "GetCredentialCount succeeded (0x%08X)", static_cast<unsigned>(hr));
    Check((count == 1) == unlock,
          "classic answer matches Btwu::ShouldUnlock (unlock=%s, count=%u)",
          unlock ? "TRUE" : "FALSE", count);
    Check(count != 1 || (autoLogon == TRUE) == Btwu::AutoSubmitUnlock(),
          "pbAutoLogonWithDefault is the autosubmit marker, not an accident (autoLogon=%d, marker=%s)",
          autoLogon, Btwu::AutoSubmitUnlock() ? "present" : "absent");
    Check(mergedHr == hr && mergedCount == count,
          "both scenarios answer identically (classic count=%u, merged count=%u)", count, mergedCount);
    Check(unlock ? defaultIndex == 0 : defaultIndex == CREDENTIAL_PROVIDER_NO_DEFAULT,
          "pdwDefault is %s (got %u)", unlock ? "0" : "NO_DEFAULT",
          static_cast<unsigned>(defaultIndex));

    printf("\n%d passed, %d failed\n", g_passed, g_failed);
    if (!unlock)
        printf("\nLIVE VERDICT: nothing is offered - the lock screen is exactly as Windows made it.\n");
    else if (Btwu::AutoSubmitUnlock())
        printf("\nLIVE VERDICT: the lock screen would unlock by itself at this moment.\n");
    else
        printf("\nLIVE VERDICT: the lock screen would show every normal tile, plus ours as the selected\n"
               "              one - pressing Enter (or clicking 解锁) unlocks with no password.\n");

    return g_failed == 0 ? 0 : 1;
}

int wmain(int argc, wchar_t** argv)
{
    bool keep = false, live = false;
    for (int i = 1; i < argc; i++)
    {
        if (wcscmp(argv[i], L"--keep") == 0) keep = true;
        if (wcscmp(argv[i], L"--live") == 0) live = true;
    }

    // Used by the scratch mode to enrol a credential for *this* user, which is the only kind of
    // credential the provider will offer at a lock screen.
    const std::wstring sessionSid = ProcessUserSid();

    if (live)
    {
        std::wstring real = Btwu::DataDirectory();
        printf("LIVE mode - reading %ls as-is\n", real.c_str());
        printf("  presence.dat / cred.bin / config.json / pause.until are not written.\n");
        printf("  the DLL will append to its own credprov.log, as it does inside LogonUI.\n");
    }
    else
    {
        wchar_t tempPath[MAX_PATH]{};
        GetTempPathW(MAX_PATH, tempPath);
        g_scratch = std::wstring(tempPath) + L"btwu-harness-" + std::to_wstring(GetCurrentProcessId());

        // Btwu::DataDirectory() resolves %ProgramData%\Btwu at every call, so redirecting the variable
        // here is enough to keep the real state out of reach.
        SetEnvironmentVariableW(L"ProgramData", g_scratch.c_str());
        CreateDirectoryW(g_scratch.c_str(), nullptr);
        printf("scratch ProgramData = %ls\n", g_scratch.c_str());
        printf("this process' user SID   = %ls\n", sessionSid.c_str());

        if (sessionSid.empty())
        {
            printf("cannot read this process' token - the identity checks cannot be exercised\n");
            return 1;
        }

        if (!WriteCredential(sessionSid)) return 1;
    }

    std::wstring dllPath;
    if (argc >= 2 && argv[1][0] != L'-')
    {
        dllPath = argv[1];
    }
    else
    {
        // Tolerate being launched from the repository root, from test\build, or from anywhere else.
        wchar_t exeDir[MAX_PATH]{};
        GetModuleFileNameW(nullptr, exeDir, MAX_PATH);
        PathRemoveFileSpecW(exeDir);

        const std::wstring candidates[] =
        {
            std::wstring(exeDir) + L"\\..\\..\\build\\x64\\BtwuCredProv.dll",
            std::wstring(exeDir) + L"\\..\\build\\x64\\BtwuCredProv.dll",
            L"..\\..\\build\\x64\\BtwuCredProv.dll",
            L"build\\x64\\BtwuCredProv.dll",
            L"BtwuCredProv.dll",
        };
        for (const std::wstring& candidate : candidates)
        {
            if (GetFileAttributesW(candidate.c_str()) != INVALID_FILE_ATTRIBUTES)
            {
                dllPath = candidate;
                break;
            }
        }
        if (dllPath.empty()) dllPath = candidates[0];
    }

    HMODULE dll = LoadLibraryW(dllPath.c_str());
    if (dll == nullptr)
    {
        wchar_t full[MAX_PATH]{};
        GetFullPathNameW(dllPath.c_str(), MAX_PATH, full, nullptr);
        printf("FAIL LoadLibrary(%ls) -> %lu\n", full, GetLastError());
        return 1;
    }

    auto getClassObject = reinterpret_cast<PFN_DllGetClassObject>(GetProcAddress(dll, "DllGetClassObject"));
    auto canUnloadNow = reinterpret_cast<PFN_DllCanUnloadNow>(GetProcAddress(dll, "DllCanUnloadNow"));
    if (getClassObject == nullptr || canUnloadNow == nullptr)
    {
        printf("FAIL the DLL does not export DllGetClassObject / DllCanUnloadNow\n");
        return 1;
    }

    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

    Section("COM activation");
    // Baseline: nothing has been created yet, so the DLL is legitimately unloadable. The matching
    // assertion at the end of this file checks that advising a provider - which starts a thread that
    // runs code inside the DLL - takes that permission away.
    Check(canUnloadNow() == S_OK, "before any object exists, DllCanUnloadNow -> S_OK");

    IClassFactory* factory = nullptr;
    HRESULT hr = getClassObject(CLSID_BtwuCredProv, IID_IClassFactory, reinterpret_cast<void**>(&factory));
    Check(SUCCEEDED(hr), "DllGetClassObject -> 0x%08X", hr);
    if (FAILED(hr)) return 1;

    ICredentialProvider* provider = nullptr;
    hr = factory->CreateInstance(nullptr, IID_ICredentialProvider, reinterpret_cast<void**>(&provider));
    Check(SUCCEEDED(hr), "IClassFactory::CreateInstance(IID_ICredentialProvider) -> 0x%08X", hr);
    if (FAILED(hr)) return 1;

    Section("scenario policy: unlock only, nothing else");
    hr = provider->SetUsageScenario(CPUS_UNLOCK_WORKSTATION, 0);
    Check(hr == S_OK, "SetUsageScenario(CPUS_UNLOCK_WORKSTATION) == S_OK (got 0x%08X)", hr);

    // Windows 10/11 reports the lock screen as CPUS_LOGON, not CPUS_UNLOCK_WORKSTATION - the platform
    // merged the two scenarios. The provider therefore accepts CPUS_LOGON, but only when a user is
    // signed in to this session, which is the documented discriminator. The expectation below is
    // computed with the very same shared helper the DLL calls, so this asserts the policy rather than
    // a copy of it - and stays correct whether this harness runs from an interactive session (the
    // normal case, signedIn == true) or from a service session (signedIn == false).
    wchar_t sessionUser[Btwu::kMaxSessionNameChars]{};
    wchar_t sessionDomain[Btwu::kMaxSessionNameChars]{};
    const bool signedIn = Btwu::QuerySessionUser(sessionUser, Btwu::kMaxSessionNameChars,
                                                 sessionDomain, Btwu::kMaxSessionNameChars);
    printf("  this session: user=\"%ls\" domain=\"%ls\" -> signed in = %s\n",
           sessionUser, sessionDomain, signedIn ? "true" : "false");

    hr = provider->SetUsageScenario(CPUS_LOGON, 0);
    Check(hr == (signedIn ? S_OK : E_NOTIMPL),
          "SetUsageScenario(CPUS_LOGON) == %s (got 0x%08X) - the merged scenario is a lock screen "
          "only when a user is signed in", signedIn ? "S_OK" : "E_NOTIMPL", hr);

    hr = provider->SetUsageScenario(CPUS_CREDUI, 0);
    Check(hr == E_NOTIMPL, "SetUsageScenario(CPUS_CREDUI) == E_NOTIMPL (got 0x%08X) - never approve UAC", hr);
    hr = provider->SetUsageScenario(CPUS_CHANGE_PASSWORD, 0);
    Check(hr == E_NOTIMPL, "SetUsageScenario(CPUS_CHANGE_PASSWORD) == E_NOTIMPL (got 0x%08X)", hr);
    hr = provider->SetUsageScenario(CPUS_PLAP, 0);
    Check(hr == E_NOTIMPL, "SetUsageScenario(CPUS_PLAP) == E_NOTIMPL (got 0x%08X)", hr);
    hr = provider->SetUsageScenario(CPUS_INVALID, 0);
    Check(hr == E_NOTIMPL, "SetUsageScenario(CPUS_INVALID) == E_NOTIMPL (got 0x%08X)", hr);

    if (live)
    {
        // Live mode must drive the provider exactly the way LogonUI does, and on Windows 10/11 that
        // means CPUS_LOGON - the very scenario the old build refused, which is why the lock screen
        // silently did nothing. When this session has a signed-in user, that is the scenario the live
        // report runs under, so the run below exercises the real path end to end.
        if (signedIn)
        {
            provider->SetUsageScenario(CPUS_LOGON, 0);
            printf("  live mode: driving the provider with CPUS_LOGON, as LogonUI does\n");
        }

        // Everything above is scenario-independent; the scratch-state sections below are not.
        const int result = ReportLive(provider);
        provider->Release();
        factory->Release();
        return result;
    }

    // Scratch mode: hand the provider back to the classic scenario the sections below expect.
    provider->SetUsageScenario(CPUS_UNLOCK_WORKSTATION, 0);

    Section("device absent -> the provider must be invisible");
    WritePresence(false, 0);
    DWORD count = 99, defaultIndex = 99;
    BOOL autoLogon = TRUE;
    hr = provider->GetCredentialCount(&count, &defaultIndex, &autoLogon);
    Check(SUCCEEDED(hr) && count == 0, "GetCredentialCount -> count=%u autoLogon=%d (want 0 / FALSE)", count, autoLogon);
    Check(defaultIndex == CREDENTIAL_PROVIDER_NO_DEFAULT, "pdwDefault == CREDENTIAL_PROVIDER_NO_DEFAULT");

    Section("the shared session-SID helper must agree with this process' own token");
    {
        std::wstring helperSid;
        const bool resolved = Btwu::SessionUserSid(helperSid);
        Check(resolved && _wcsicmp(helperSid.c_str(), sessionSid.c_str()) == 0,
              "Btwu::SessionUserSid -> %ls (token says %ls)", helperSid.c_str(), sessionSid.c_str());
    }

    Section("the SID in cred.bin must belong to the user at this lock screen");
    {
        // A well-formed local-account SID for an account that does not exist, of the same shape as the
        // Microsoft-account *identity* SID (S-1-11-96-...) that handed the lock screen no usable UI on
        // 2026-10-14. The provider must go invisible rather than offer something LogonUI cannot attach
        // to a user tile - staying hidden costs the user nothing, a broken lock screen costs the machine.
        WriteCredential(L"S-1-11-96-3623454863-3092224924-2786231121-1208373640");
        WritePresence(true, 0);

        provider->SetUsageScenario(CPUS_UNLOCK_WORKSTATION, 0);
        hr = provider->GetCredentialCount(&count, &defaultIndex, &autoLogon);
        Check(SUCCEEDED(hr) && count == 0,
              "classic scenario -> count=%u (want 0: the device is present, the identity is not ours)", count);

        ICredentialProviderCredential* refused = nullptr;
        const HRESULT refusedHr = provider->GetCredentialAt(0, &refused);
        Check(FAILED(refusedHr) && refused == nullptr,
              "GetCredentialAt(0) refuses while the count said zero (0x%08X)",
              static_cast<unsigned>(refusedHr));
        if (refused != nullptr) refused->Release();

        provider->SetUsageScenario(CPUS_LOGON, 0);
        hr = provider->GetCredentialCount(&count, &defaultIndex, &autoLogon);
        Check(SUCCEEDED(hr) && count == 0,
              "merged scenario -> count=%u (want 0: same reason)", count);

        // ... and the same credential is offered again the moment it belongs to this user.
        WriteCredential(sessionSid);
        hr = provider->GetCredentialCount(&count, &defaultIndex, &autoLogon);
        Check(SUCCEEDED(hr) && count == 1,
              "with the right SID back in place, the merged scenario -> count=%u (want 1)", count);

        provider->SetUsageScenario(CPUS_UNLOCK_WORKSTATION, 0);
    }

    Section("device present -> exactly one credential, offered as the default tile");
    WritePresence(true, 0);
    hr = provider->GetCredentialCount(&count, &defaultIndex, &autoLogon);
    Check(SUCCEEDED(hr) && count == 1, "GetCredentialCount -> count=%u (want 1)", count);
    Check(defaultIndex == 0, "pdwDefault == 0 - our tile is the selected one");
    Check(autoLogon == FALSE,
          "pbAutoLogonWithDefault == FALSE by default (autosubmit marker %s) - the lock screen keeps "
          "its own UI and the user submits with Enter",
          GetFileAttributesW(Btwu::AutoSubmitFilePath().c_str()) != INVALID_FILE_ATTRIBUTES ? "present" : "absent");

    Section("the autosubmit marker turns the default tile into a full auto-logon");
    {
        const std::wstring marker = Btwu::AutoSubmitFilePath();
        const HANDLE file = CreateFileW(marker.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr,
                                        CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        const bool created = file != INVALID_HANDLE_VALUE;
        if (created) CloseHandle(file);

        hr = provider->GetCredentialCount(&count, &defaultIndex, &autoLogon);
        Check(created && SUCCEEDED(hr) && count == 1 && autoLogon == TRUE,
              "with the marker -> count=%u autoLogon=%d (want 1 / TRUE)", count, autoLogon);
        Check(defaultIndex == 0, "pdwDefault == 0");

        DeleteFileW(marker.c_str());
        hr = provider->GetCredentialCount(&count, &defaultIndex, &autoLogon);
        Check(SUCCEEDED(hr) && count == 1 && autoLogon == FALSE,
              "with the marker removed -> count=%u autoLogon=%d (want 1 / FALSE)", count, autoLogon);
    }

    ICredentialProviderCredential* credential = nullptr;
    hr = provider->GetCredentialAt(0, &credential);
    Check(SUCCEEDED(hr) && credential != nullptr, "GetCredentialAt(0) -> 0x%08X", hr);

    if (SUCCEEDED(hr) && credential != nullptr)
    {
        Section("field descriptors");
        DWORD fieldCount = 0;
        provider->GetFieldDescriptorCount(&fieldCount);
        Check(fieldCount == 4, "GetFieldDescriptorCount -> %u (want 4)", fieldCount);

        // A tile whose fields are all un-editable cannot take keyboard focus, and LogonUI's Enter key
        // has nowhere to go - which turns the submit button (drawn as the right-arrow) into the only
        // way in, i.e. a mandatory mouse click. Microsoft's technical reference says CPFIS_FOCUSED
        // "may not be specified for un-editable field types", so the invariant to guard is: exactly
        // one field holds focus, and that field is editable.
        {
            DWORD focused = 0, focusedEditable = 0, submitButtons = 0;
            for (DWORD index = 0; index < fieldCount; ++index)
            {
                CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR* descriptor = nullptr;
                if (FAILED(provider->GetFieldDescriptorAt(index, &descriptor)) || descriptor == nullptr)
                    continue;

                if (descriptor->cpft == CPFT_SUBMIT_BUTTON) submitButtons++;
                const bool editable = descriptor->cpft == CPFT_EDIT_TEXT ||
                                      descriptor->cpft == CPFT_PASSWORD_TEXT;

                CREDENTIAL_PROVIDER_FIELD_STATE state = CPFS_HIDDEN;
                CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE interactive = CPFIS_NONE;
                if (SUCCEEDED(credential->GetFieldState(index, &state, &interactive)) &&
                    interactive == CPFIS_FOCUSED)
                {
                    focused++;
                    if (editable) focusedEditable++;
                }

                CoTaskMemFree(descriptor->pszLabel);
                CoTaskMemFree(descriptor);
            }

            Check(focused == 1,
                  "exactly one field takes keyboard focus (%u) - with none, Enter has nowhere to go and "
                  "the arrow button becomes a mandatory mouse click", focused);
            Check(focusedEditable == 1,
                  "and that focused field is editable (%u) - CPFIS_FOCUSED may not be set on un-editable "
                  "field types, which is exactly how the arrow became mandatory", focusedEditable);
            Check(submitButtons == 1,
                  "still exactly one CPFT_SUBMIT_BUTTON (%u), kept as the mouse fallback", submitButtons);
        }

        ICredentialProviderCredential2* credential2 = nullptr;
        if (SUCCEEDED(credential->QueryInterface(IID_ICredentialProviderCredential2,
                                                  reinterpret_cast<void**>(&credential2))))
        {
            PWSTR sid = nullptr;
            hr = credential2->GetUserSid(&sid);
            Check(SUCCEEDED(hr) && sid != nullptr && wcsstr(sid, L"S-1-5-21") != nullptr,
                  "ICredentialProviderCredential2::GetUserSid -> %ls", sid ? sid : L"(null)");
            if (sid) CoTaskMemFree(sid);
            credential2->Release();
        }
        else
        {
            Check(false, "credential does not expose ICredentialProviderCredential2");
        }

        Section("serialization");
        CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE response = CPGSR_NO_CREDENTIAL_NOT_FINISHED;
        CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION serialization{};
        PWSTR statusText = nullptr;
        CREDENTIAL_PROVIDER_STATUS_ICON icon = CPSI_NONE;

        hr = credential->GetSerialization(&response, &serialization, &statusText, &icon);
        Check(SUCCEEDED(hr) && response == CPGSR_RETURN_CREDENTIAL_FINISHED,
              "GetSerialization -> hr=0x%08X response=%d", hr, response);

        if (SUCCEEDED(hr) && serialization.rgbSerialization != nullptr &&
            serialization.cbSerialization >= sizeof(KERB_INTERACTIVE_UNLOCK_LOGON))
        {
            Check(IsEqualCLSID(serialization.clsidCredentialProvider, CLSID_BtwuCredProv),
                  "clsidCredentialProvider matches");

            auto* logon = reinterpret_cast<KERB_INTERACTIVE_UNLOCK_LOGON*>(serialization.rgbSerialization);
            Check(logon->Logon.MessageType == KerbWorkstationUnlockLogon,
                  "MessageType == KerbWorkstationUnlockLogon (%d)", logon->Logon.MessageType);
            Check(logon->LogonId.LowPart == 0 && logon->LogonId.HighPart == 0,
                  "LogonId == 0 (Winlogon fills in the session being unlocked)");

            Section("packed layout - the assertion that actually matters");
            printf("  blob is %lu bytes\n", serialization.cbSerialization);

            struct Field
            {
                const char* name;
                UNICODE_STRING* value;
                const wchar_t* expected;
            };
            wchar_t computer[MAX_COMPUTERNAME_LENGTH + 1]{};
            DWORD computerLength = MAX_COMPUTERNAME_LENGTH + 1;
            GetComputerNameW(computer, &computerLength);

            Field fields[] =
            {
                { "LogonDomainName", &logon->Logon.LogonDomainName, computer },
                { "UserName",        &logon->Logon.UserName,        kHarnessUser },
                { "Password",        &logon->Logon.Password,        kHarnessPassword },
            };

            for (const Field& field : fields)
            {
                const ULONG_PTR offset = reinterpret_cast<ULONG_PTR>(field.value->Buffer);
                char what[128]{};
                sprintf_s(what, "%s.Buffer", field.name);

                Check(offset < serialization.cbSerialization,
                      "%s = %llu is an offset inside the blob, not a dangling address", what,
                      static_cast<unsigned long long>(offset));
                Check(field.value->MaximumLength == field.value->Length,
                      "%s.MaximumLength == Length (%u)", field.name, field.value->Length);

                const std::wstring actual = ReadPackedString(
                    serialization.rgbSerialization, serialization.cbSerialization, *field.value);
                Check(actual == field.expected, "%s == \"%ls\"", field.name, actual.c_str());
            }

            // Prove the point the other way around: a real pointer would land far outside the blob.
            printf("  (an address would be ~0x%llX and is far outside 0..%lu)\n",
                   static_cast<unsigned long long>(reinterpret_cast<ULONG_PTR>(serialization.rgbSerialization)),
                   serialization.cbSerialization);
        }
        else
        {
            Check(false, "GetSerialization returned no usable blob");
        }

        // Regression test for the bug that made every submission fail on this machine on 2026-10-14
        // (credprov.log: "ReportResult: logon failed (0xC000006D / 0xC00000E5)", and the lock screen
        // said "user name or password is incorrect").
        //
        // The mistake was to let the merged scenario carry over into the MessageType. Windows 10/11
        // report the LOCK SCREEN as CPUS_LOGON, so a blob built under CPUS_LOGON was given
        // KerbWorkstationUnlockLogon - "unlock the session named by LogonId" - while LogonId is
        // deliberately left zero for Winlogon to fill in. LSA could not resolve a zero LUID and
        // failed before comparing the password: hence STATUS_INTERNAL_ERROR, not STATUS_WRONG_PASSWORD.
        //
        // The message type follows the scenario number, NOT what the operation means:
        //   CPUS_UNLOCK_WORKSTATION (2) -> KerbWorkstationUnlockLogon (7)
        //   CPUS_LOGON              (1) -> KerbInteractiveLogon        (2)
        // Chromium's KerbInteractiveUnlockLogonInit and Microsoft's CredentialProvider sample both
        // switch on the scenario for exactly this. The struct stays KERB_INTERACTIVE_UNLOCK_LOGON
        // either way - only MessageType differs, so the blob SIZE must still match the classic one.
        if (signedIn)
        {
            Section("the merged CPUS_LOGON lock screen keeps KerbInteractiveLogon");

            provider->SetUsageScenario(CPUS_LOGON, 0);

            ICredentialProviderCredential* logonCredential = nullptr;
            hr = provider->GetCredentialAt(0, &logonCredential);
            Check(SUCCEEDED(hr) && logonCredential != nullptr, "GetCredentialAt(0) -> 0x%08X", hr);

            if (SUCCEEDED(hr) && logonCredential != nullptr)
            {
                CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE logonResponse = CPGSR_NO_CREDENTIAL_NOT_FINISHED;
                CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION logonSerialization{};
                PWSTR logonStatusText = nullptr;
                CREDENTIAL_PROVIDER_STATUS_ICON logonIcon = CPSI_NONE;

                hr = logonCredential->GetSerialization(&logonResponse, &logonSerialization,
                                                       &logonStatusText, &logonIcon);
                Check(SUCCEEDED(hr) && logonResponse == CPGSR_RETURN_CREDENTIAL_FINISHED,
                      "GetSerialization under CPUS_LOGON -> hr=0x%08X response=%d", hr, logonResponse);

                if (SUCCEEDED(hr) && logonSerialization.rgbSerialization != nullptr &&
                    logonSerialization.cbSerialization >= sizeof(KERB_INTERACTIVE_UNLOCK_LOGON))
                {
                    auto* logonBlob = reinterpret_cast<KERB_INTERACTIVE_UNLOCK_LOGON*>(
                        logonSerialization.rgbSerialization);
                    Check(logonBlob->Logon.MessageType == KerbInteractiveLogon,
                          "MessageType == KerbInteractiveLogon (%d) - a merged-scenario lock screen thinks "
                          "LogonUI submits an interactive logon, not a workstation unlock",
                          logonBlob->Logon.MessageType);
                    // Only comparable when the classic blob was produced too - a failure there is
                    // already reported by the section above, and must not be dereferenced here.
                    if (serialization.rgbSerialization != nullptr &&
                        serialization.cbSerialization >= sizeof(KERB_INTERACTIVE_UNLOCK_LOGON))
                    {
                        auto* classicBlob = reinterpret_cast<KERB_INTERACTIVE_UNLOCK_LOGON*>(
                            serialization.rgbSerialization);
                        Check(logonBlob->Logon.MessageType != classicBlob->Logon.MessageType,
                              "and it therefore DIFFERS from the classic CPUS_UNLOCK_WORKSTATION blob (%d vs %d)",
                              logonBlob->Logon.MessageType, classicBlob->Logon.MessageType);
                    }
                    Check(logonSerialization.cbSerialization == serialization.cbSerialization,
                          "blob size still matches the classic CPUS_UNLOCK_WORKSTATION blob (%lu vs %lu) - "
                          "same struct, same strings, only MessageType differs",
                          logonSerialization.cbSerialization, serialization.cbSerialization);
                    CoTaskMemFree(logonSerialization.rgbSerialization);
                }

                logonCredential->Release();
            }

            // Hand the provider back to the sections that follow.
            provider->SetUsageScenario(CPUS_UNLOCK_WORKSTATION, 0);
        }

        // Presence is judged once - when the lock screen appears - and not again while the user is
        // submitting. A keyboard that dozes off between the two must not silently cancel an unlock the
        // user just asked for; the emergency switches are all that is re-read. This section exists so
        // that "let us re-check presence at submission, it is safer" cannot be reintroduced without
        // deliberately deleting a test that explains why it is not.
        Section("the presence decision is taken at enumeration, not at submission");
        {
            DeleteFileW(Btwu::AutoSubmitFilePath().c_str());

            WritePresence(true, 0);
            hr = provider->GetCredentialCount(&count, &defaultIndex, &autoLogon);
            Check(SUCCEEDED(hr) && count == 1, "offered while the device is present (count=%u)", count);

            ICredentialProviderCredential* dozing = nullptr;
            hr = provider->GetCredentialAt(0, &dozing);
            Check(SUCCEEDED(hr) && dozing != nullptr, "GetCredentialAt(0) -> 0x%08X", hr);

            // The keyboard sleeps while the user is pressing Enter.
            WritePresence(false, 0);
            if (dozing != nullptr)
            {
                CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE dozeResponse = CPGSR_NO_CREDENTIAL_NOT_FINISHED;
                CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION dozeSerialization{};
                hr = dozing->GetSerialization(&dozeResponse, &dozeSerialization, &statusText, &icon);
                Check(SUCCEEDED(hr) && dozeResponse == CPGSR_RETURN_CREDENTIAL_FINISHED,
                      "the unlock the user asked for still happens (hr=0x%08X response=%d)",
                      static_cast<unsigned>(hr), dozeResponse);
                if (dozeSerialization.rgbSerialization != nullptr)
                    CoTaskMemFree(dozeSerialization.rgbSerialization);
                dozing->Release();
            }

            // ... but the next lock screen judges from scratch, device still gone.
            hr = provider->GetCredentialCount(&count, &defaultIndex, &autoLogon);
            Check(SUCCEEDED(hr) && count == 0,
                  "a later enumeration with the device still gone -> count=%u (want 0)", count);

            WritePresence(true, 0);
        }

        Section("a successful unlock publishes the post-unlock dwell");
        {
            // The agent locks the workstation when the trigger device goes absent - and a BLE keyboard's
            // idle disconnect looks exactly like walking away. Without this file the agent would lock
            // the screen it was just asked to unlock, seconds after unlocking it.
            const std::wstring dwellPath = g_scratch + L"\\BtwuUnlock\\unlocked.until";
            DeleteFileW(dwellPath.c_str());
            Check(!Btwu::IsWithinUnlockCooldown(), "no dwell file -> not in a post-unlock dwell");

            hr = credential->ReportResult(0 /* STATUS_SUCCESS */, 0, &statusText, &icon);
            Check(SUCCEEDED(hr), "ReportResult(success) -> 0x%08X", hr);
            Check(GetFileAttributesW(dwellPath.c_str()) != INVALID_FILE_ATTRIBUTES,
                  "a successful unlock wrote unlocked.until");
            Check(Btwu::IsWithinUnlockCooldown(),
                  "and it is in force (%lld s)", Btwu::kUnlockCooldownSeconds);
        }

        Section("failed logon must suppress retries");
        DeleteFileW((g_scratch + L"\\BtwuUnlock\\pause.until").c_str());
        hr = credential->ReportResult(static_cast<NTSTATUS>(0xC000006DL), 0, &statusText, &icon);
        Check(SUCCEEDED(hr), "ReportResult(failure) -> 0x%08X", hr);
        Check(GetFileAttributesW((g_scratch + L"\\BtwuUnlock\\pause.until").c_str()) != INVALID_FILE_ATTRIBUTES,
              "a failed logon wrote pause.until so the next attempt is suppressed");

        WritePresence(true, 3600);
        provider->GetCredentialCount(&count, &defaultIndex, &autoLogon);
        Check(count == 0, "while suppressed, GetCredentialCount -> %u (want 0)", count);

        credential->Release();
    }

    Section("suppressed state still fails closed");
    WritePresence(false, 0);
    provider->GetCredentialCount(&count, &defaultIndex, &autoLogon);
    Check(count == 0, "back to absent -> count=%u", count);

    Section("the DLL must stay mapped while the presence watcher thread lives");

    // Advise is what lets LogonUI notice the device coming back while the lock screen is already up,
    // and it starts a worker thread that outlives every COM object. Advising with a null events pointer
    // starts that thread just the same, so this needs no ICredentialProviderEvents implementation.
    provider->Advise(nullptr, 0);

    provider->Release();
    factory->Release();

    // Nothing holds a COM reference any more; the only thing left is the watcher thread, and its code
    // lives in this image. S_OK here would let CoFreeUnusedLibraries unmap the DLL underneath a running
    // thread - the one failure mode in this provider that could actually take LogonUI down.
    hr = canUnloadNow();
    Check(hr == S_FALSE,
          "after Advise, DllCanUnloadNow -> 0x%08X (S_FALSE keeps the running thread's code mapped)", hr);

    printf("\n%d passed, %d failed\n", g_passed, g_failed);

    if (!keep)
    {
        // Best effort: the DLL may still be loaded, which is fine - the directory is in %TEMP%.
        std::wstring command = L"cmd.exe /c rmdir /s /q \"" + g_scratch + L"\"";
        STARTUPINFOW startup{};
        startup.cb = sizeof(startup);
        PROCESS_INFORMATION process{};
        if (CreateProcessW(nullptr, &command[0], nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
                           nullptr, nullptr, &startup, &process))
        {
            WaitForSingleObject(process.hProcess, 5000);
            CloseHandle(process.hProcess);
            CloseHandle(process.hThread);
        }
    }

    return g_failed == 0 ? 0 : 1;
}
