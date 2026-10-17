// BtwuCredProv - Bluetooth presence credential provider for Windows.
//
// Replaces the "type your PIN" step at the lock screen with "the paired Bluetooth keyboard is
// connected". Registered as a credential provider, it runs inside LogonUI as SYSTEM.
//
// Design rules, in order of importance:
//   1. Never break normal sign-in. Every failure path returns an error and shows nothing; the
//      built-in providers (PIN / password / fingerprint) stay untouched and usable. There is no
//      scenario in which this DLL can prevent a user from signing in.
//   2. When the trigger device is absent, this provider reports zero credentials and is invisible.
//   3. Only when the agent says "device present AND state is fresh" does it offer one credential
//      that Windows may use to unlock automatically.
//   4. Unlock only, in the sense that actually matters: this can never sign a user in from a real
//      logon screen, and can never silently approve a UAC prompt. Two things follow from the Windows
//      10/11 platform change described below: CPUS_LOGON is accepted (it IS the lock screen now), but
//      only after proving the current session already has a signed-in user, and CPUS_CREDUI is
//      refused unconditionally.
//
// WHY BOTH UNLOCK SCENARIOS ARE ACCEPTED
// --------------------------------------
// Before Windows 10 the lock screen arrived as CPUS_UNLOCK_WORKSTATION (2) and the logon screen as
// CPUS_LOGON (1), so "unlock only" was a one-line scenario test. Windows 10 merged the two: any user
// can sign in from a locked machine without tearing down the current session, so LogonUI now
// reports CPUS_LOGON for a lock screen too. Microsoft's documentation says so outright, and adds
// that a provider "should be robust enough to create the appropriate credential structure based on
// the scenario given to it".
//
// The consequence of ignoring that is invisible: the provider is simply never asked for a credential
// at the lock screen, and the user keeps typing a PIN with no error anywhere. That is exactly the
// bug this file shipped with - credprov.log contained only
//
//     LogonUI.exe pid=41356 SetUsageScenario(1)
//
// and nothing else, ever. The fix is the documented discriminator: ask whether a user is signed in to
// this session (Btwu::QuerySessionUser). Lock screen -> yes -> treat it as an unlock. Fresh boot or
// post-sign-out logon screen -> no -> refuse, exactly as before.
//
// The credential comes from %ProgramData%\BtwuUnlock\cred.bin (DPAPI machine scope + fixed entropy).

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <credentialprovider.h>
#include <ntsecapi.h>
#include <shlwapi.h>
#include <wincrypt.h>
#include <dpapi.h>

#include <cstdarg>
#include <exception>
#include <string>
#include <utility>
#include <vector>

#include "PresenceState.h"

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "crypt32.lib")
#pragma comment(lib, "secur32.lib")
#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "user32.lib")

// {B7C4E1A2-3D58-4F61-8A9E-2C7B5D40E913} - must match Btwu.Core.Registration.ProviderClsid.
static const CLSID CLSID_BtwuCredProv =
{ 0xb7c4e1a2, 0x3d58, 0x4f61, { 0x8a, 0x9e, 0x2c, 0x7b, 0x5d, 0x40, 0xe9, 0x13 } };

namespace
{
    LONG g_referenceCount = 0;
    LONG g_serverLocks = 0;

    // Non-zero once the presence watcher's thread has been created. It is a plain flag rather than a
    // query into the watcher singleton on purpose: DllCanUnloadNow runs on every CoFreeUnusedLibraries
    // and must not construct anything or touch the disk.
    LONG g_watcherThreads = 0;

    bool WatcherThreadAlive()
    {
        return InterlockedCompareExchange(&g_watcherThreads, 0, 0) != 0;
    }

    // A failed logon attempt suppresses auto-unlock for a while; without this a wrong stored
    // password would be retried on every single lock and could lock the account out.
    constexpr long long kFailureSuppressionSeconds = 120;

    // A C++ exception must never cross a COM boundary, and must never unwind out of a worker thread -
    // the first hands garbage to LogonUI, the second calls std::terminate and takes the sign-in UI down
    // with it. Allocation is the only realistic source here (the credential lives in std::wstring and
    // std::vector inside a SYSTEM process), and the correct response to every one of them is the same:
    // offer nothing, leave the built-in providers alone, let the user type their password.
#define BTWU_COM_GUARD(body)                                       \
        try { body }                                               \
        catch (const std::exception& error) {                      \
            LogLine(L"%s: %S", __FUNCTIONW__, error.what());       \
            return E_FAIL;                                         \
        }                                                          \
        catch (...) {                                              \
            LogLine(L"%s: unknown exception", __FUNCTIONW__);      \
            return E_FAIL;                                         \
        }

    // ------------------------------------------------------------------------------- logging

    // Same rotation policy as Btwu.Core.Log. Without it this file grows for as long as the machine
    // keeps locking - it is written by every LogonUI instance, so nothing else would ever bound it.
    constexpr long long kMaxLogBytes = 2LL * 1024 * 1024;

    // Logs are UTF-8, like Btwu.Core.Log writes and like everything else in this repo. Builds before
    // 2026-10-14 wrote this file as BOM-less UTF-16LE (wcslen * sizeof(wchar_t) straight to
    // WriteFile), which no ordinary tool can read: `type`, `tail`, `grep` and the editor all show a
    // NUL-interleaved mess, and the missing BOM defeats automatic detection too. That defect cost
    // real diagnosis time, so an old-format file is migrated out of the way once per process.
    std::string WideToUtf8(const std::wstring& text)
    {
        if (text.empty()) return {};
        const int size = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()),
                                             nullptr, 0, nullptr, nullptr);
        if (size <= 0) return {};
        std::string result(static_cast<size_t>(size), '\0');
        // &result[0], not result.data(): under the standard this project compiles with, data() still
        // returns const char* and cannot be an output buffer.
        WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()),
                            &result[0], size, nullptr, nullptr);
        return result;
    }

    // A UTF-16LE ASCII character is followed by a NUL byte; valid UTF-8 text never looks like that at
    // offset 0..1. Runs at most once per process - checking it per log line would mean a file open
    // per line for a condition that can only ever be true on the first line after an upgrade.
    bool IsLegacyUtf16Log(const std::wstring& path)
    {
        const HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return false;

        unsigned char head[2]{};
        DWORD read = 0;
        const BOOL ok = ReadFile(file, head, 2, &read, nullptr);
        CloseHandle(file);
        return ok != FALSE && read == 2 && head[0] != 0 && head[1] == 0;
    }

    bool ShouldMigrateLegacyLog(const std::wstring& path)
    {
        static bool checked = false;
        if (checked) return false;
        checked = true;
        return IsLegacyUtf16Log(path);
    }

    void RotateLogIfNeeded(const std::wstring& path)
    {
        WIN32_FILE_ATTRIBUTE_DATA info{};
        if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &info)) return;

        const long long size = (static_cast<long long>(info.nFileSizeHigh) << 32)
                             | static_cast<long long>(info.nFileSizeLow);
        if (size < kMaxLogBytes && !ShouldMigrateLegacyLog(path)) return;

        // Best effort only. If this fails (ACL, sharing) the log keeps growing, which is strictly
        // better than failing a credential provider call over a diagnostic file.
        MoveFileExW(path.c_str(), (path + L".1").c_str(), MOVEFILE_REPLACE_EXISTING);
    }

    // This file is the only diagnostic channel inside LogonUI - there is no console to attach to.
    // Every line carries the hosting process, because this DLL is loaded by more than one host
    // (LogonUI.exe, CredentialUIBroker.exe, the test harness) and "which one called me, with which
    // scenario" is the first question any credential provider bug starts with.
    void LogLine(const wchar_t* format, ...)
    {
        wchar_t message[512]{};
        va_list args;
        va_start(args, format);
        _vsnwprintf_s(message, _TRUNCATE, format, args);
        va_end(args);

        SYSTEMTIME st{};
        GetLocalTime(&st);

        wchar_t exePath[MAX_PATH]{};
        GetModuleFileNameW(nullptr, exePath, MAX_PATH);
        const wchar_t* exeName = wcsrchr(exePath, L'\\');
        exeName = exeName ? exeName + 1 : exePath;

        wchar_t line[1024]{};
        _snwprintf_s(line, _TRUNCATE, L"%04u-%02u-%02u %02u:%02u:%02u [credprov] %s pid=%lu %s\r\n",
                     st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond,
                     exeName, GetCurrentProcessId(), message);

        const std::wstring path = Btwu::LogFilePath();
        RotateLogIfNeeded(path);

        const HANDLE file = CreateFileW(path.c_str(), FILE_APPEND_DATA,
                                        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                                        OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return;

        const std::string utf8 = WideToUtf8(line);
        if (!utf8.empty())
        {
            DWORD written = 0;
            WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
        }
        CloseHandle(file);
    }

    // ------------------------------------------------------------------------ credential store

    struct StoredCredential
    {
        std::wstring User;
        std::wstring Domain;
        std::wstring Password;
        std::wstring Sid;
    };

    // Hand-rolled UTF-8 -> UTF-16 so the build does not depend on the SDK's MultiByteToWideChar
    // overload set, which is ambiguous with wide output buffers in some configurations.
    std::wstring Utf8ToWide(const std::string& text)
    {
        std::wstring result;
        result.reserve(text.size());

        size_t i = 0;
        while (i < text.size())
        {
            const unsigned char lead = static_cast<unsigned char>(text[i]);
            unsigned int code = lead;
            size_t continuation = 0;

            if (lead < 0x80) continuation = 0;
            else if ((lead & 0xE0) == 0xC0) { code = lead & 0x1Fu; continuation = 1; }
            else if ((lead & 0xF0) == 0xE0) { code = lead & 0x0Fu; continuation = 2; }
            else if ((lead & 0xF8) == 0xF0) { code = lead & 0x07u; continuation = 3; }
            else { result.push_back(L'\uFFFD'); i++; continue; }

            if (i + continuation >= text.size()) { result.push_back(L'\uFFFD'); break; }

            bool valid = true;
            for (size_t k = 1; k <= continuation; k++)
            {
                const unsigned char next = static_cast<unsigned char>(text[i + k]);
                if ((next & 0xC0) != 0x80) { valid = false; break; }
                code = (code << 6) | (next & 0x3Fu);
            }
            i += continuation + 1;

            if (!valid) { result.push_back(L'\uFFFD'); continue; }
            if (code <= 0xFFFF)
            {
                result.push_back(static_cast<wchar_t>(code));
            }
            else
            {
                code -= 0x10000u;
                result.push_back(static_cast<wchar_t>(0xD800u + (code >> 10)));
                result.push_back(static_cast<wchar_t>(0xDC00u + (code & 0x3FFu)));
            }
        }
        return result;
    }

    bool DpapiUnprotect(const std::vector<BYTE>& input, std::vector<BYTE>& output)
    {
        // Must match Btwu.Core.CredentialStore.Entropy.
        static const char kEntropyText[] = "Btwu.v1.credential";

        DATA_BLOB inBlob{};
        inBlob.cbData = static_cast<DWORD>(input.size());
        inBlob.pbData = const_cast<BYTE*>(input.data());

        DATA_BLOB entropyBlob{};
        entropyBlob.cbData = static_cast<DWORD>(sizeof(kEntropyText) - 1);
        entropyBlob.pbData = reinterpret_cast<BYTE*>(const_cast<char*>(kEntropyText));

        DATA_BLOB outBlob{};
        const BOOL ok = CryptUnprotectData(&inBlob, nullptr, &entropyBlob, nullptr, nullptr,
                                           CRYPTPROTECT_UI_FORBIDDEN | CRYPTPROTECT_LOCAL_MACHINE, &outBlob);
        if (!ok) return false;

        output.assign(outBlob.pbData, outBlob.pbData + outBlob.cbData);
        LocalFree(outBlob.pbData);
        return true;
    }

    // Payload layout written by Btwu.Core.CredentialStore: user \n domain \n password \n sid
    bool LoadStoredCredential(StoredCredential& credential)
    {
        const HANDLE file = CreateFileW(Btwu::CredentialFilePath().c_str(), GENERIC_READ,
                                        FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return false;

        LARGE_INTEGER size{};
        if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0 || size.QuadPart > 64 * 1024)
        {
            CloseHandle(file);
            return false;
        }

        std::vector<BYTE> blob(static_cast<size_t>(size.QuadPart));
        DWORD read = 0;
        const BOOL ok = ReadFile(file, blob.data(), static_cast<DWORD>(blob.size()), &read, nullptr);
        CloseHandle(file);
        if (!ok || read != blob.size()) return false;

        std::vector<BYTE> plain;
        const bool decrypted = DpapiUnprotect(blob, plain);
        SecureZeroMemory(blob.data(), blob.size());
        if (!decrypted) return false;

        std::string payload(reinterpret_cast<const char*>(plain.data()), plain.size());
        SecureZeroMemory(plain.data(), plain.size());

        const size_t first = payload.find('\n');
        const size_t second = first == std::string::npos ? std::string::npos : payload.find('\n', first + 1);
        const size_t third = second == std::string::npos ? std::string::npos : payload.find('\n', second + 1);
        if (first == std::string::npos || second == std::string::npos) return false;

        credential.User = Utf8ToWide(payload.substr(0, first));
        credential.Domain = Utf8ToWide(payload.substr(first + 1, second - first - 1));
        credential.Password = Utf8ToWide(payload.substr(
            second + 1, (third == std::string::npos ? payload.size() : third) - second - 1));
        if (third != std::string::npos)
            credential.Sid = Utf8ToWide(payload.substr(third + 1));

        SecureZeroMemory(&payload[0], payload.size());

        if (credential.User.empty() || credential.Password.empty()) return false;

        // A local account is addressed as MACHINE\user; the CLI stores "." for "local".
        if (credential.Domain.empty() || credential.Domain == L".")
        {
            wchar_t computer[MAX_COMPUTERNAME_LENGTH + 1]{};
            DWORD length = MAX_COMPUTERNAME_LENGTH + 1;
            credential.Domain = GetComputerNameW(computer, &length) ? computer : L".";
        }
        return true;
    }

    // Is this SID an account SID - something LogonUI can attach a credential to?
    //
    // Account SIDs carry a domain/authority and a relative id:
    //   S-1-5-21-<authority>-<sub1>-<sub2>-<rid>   a local or Active Directory account
    //   S-1-12-1-<...>                             an Entra ID (Azure AD) account
    // Anything else that can appear in this field is an *identity* rather than an account - notably
    // S-1-11-96-..., which is what a Microsoft account's sign-in address resolves to. LogonUI cannot
    // place a credential under such a SID, and in the merged scenario that alone is enough to leave
    // the lock screen with no usable UI: no user tile, no password box, no PIN. Measured on
    // 2026-10-14, and it is the reason this check exists.
    bool LocalAccountSid(const std::wstring& sid)
    {
        return sid.rfind(L"S-1-5-21-", 0) == 0 || sid.rfind(L"S-1-12-1-", 0) == 0;
    }

    // ---------------------------------------------------------------------- logon serialization

    // Resolves the authentication package id for the logon blob. The id and the success flag are
    // reported separately because an id of 0 is legal - "Negotiate" reports 0 on a normal machine -
    // so a zero must not be mistaken for failure.
    bool AuthenticationPackageId(ULONG& packageId)
    {
        static bool lookedUp = false;
        static ULONG cached = 0;
        if (lookedUp)
        {
            packageId = cached;
            return true;
        }

        HANDLE lsa = nullptr;
        const NTSTATUS connectStatus = LsaConnectUntrusted(&lsa);
        if (connectStatus != 0 || lsa == nullptr)
        {
            LogLine(L"LsaConnectUntrusted failed 0x%08X", static_cast<unsigned>(connectStatus));
            return false;
        }

        LSA_STRING packageName{};
        packageName.Length = static_cast<USHORT>(strlen("Negotiate"));
        packageName.MaximumLength = packageName.Length + 1;
        packageName.Buffer = const_cast<char*>("Negotiate");

        ULONG id = 0;
        const NTSTATUS lookupStatus = LsaLookupAuthenticationPackage(lsa, &packageName, &id);
        LsaDeregisterLogonProcess(lsa);
        if (lookupStatus != 0)
        {
            LogLine(L"LsaLookupAuthenticationPackage(Negotiate) failed 0x%08X", static_cast<unsigned>(lookupStatus));
            return false;
        }

        cached = id;
        lookedUp = true;
        LogLine(L"authentication package id = %u", id);
        packageId = id;
        return true;
    }

    // Builds the "packed" KERB_INTERACTIVE_UNLOCK_LOGON that Winlogon and LSA consume.
    //
    // THIS IS THE SUBTLE PART. In the packed form each UNICODE_STRING.Buffer is a byte OFFSET from
    // the start of the blob, not an address, and MaximumLength equals Length (no terminator). LSA
    // receives a copy of the buffer, so a real address would dangle there and the credential would
    // be rejected as malformed. Microsoft's credential provider sample spells this out in
    // KerbInteractiveUnlockLogonPack, and production providers follow the same convention.
    //
    // The blob is handed to LogonUI, which frees rgbSerialization with CoTaskMemFree, so it is
    // allocated with CoTaskMemAlloc rather than owned by a std::vector.
    bool BuildUnlockSerialization(const StoredCredential& credential, KERB_LOGON_SUBMIT_TYPE messageType,
                                  BYTE** rgbSerialization, ULONG* cbSerialization)
    {
        const size_t domainBytes = credential.Domain.size() * sizeof(wchar_t);
        const size_t userBytes = credential.User.size() * sizeof(wchar_t);
        const size_t passwordBytes = credential.Password.size() * sizeof(wchar_t);
        const size_t headerBytes = sizeof(KERB_INTERACTIVE_UNLOCK_LOGON);
        const size_t totalBytes = headerBytes + domainBytes + userBytes + passwordBytes;
        if (totalBytes > 64 * 1024) return false;

        auto* buffer = static_cast<BYTE*>(CoTaskMemAlloc(totalBytes));
        if (buffer == nullptr) return false;
        ZeroMemory(buffer, totalBytes);

        auto* logon = reinterpret_cast<KERB_INTERACTIVE_UNLOCK_LOGON*>(buffer);
        logon->Logon.MessageType = messageType;
        // Left zero on purpose: Winlogon fills in the logon session being unlocked.
        logon->LogonId.LowPart = 0;
        logon->LogonId.HighPart = 0;

        size_t cursor = headerBytes;
        auto pack = [buffer, &cursor](UNICODE_STRING& target, const std::wstring& value)
        {
            const size_t bytes = value.size() * sizeof(wchar_t);
            target.Length = static_cast<USHORT>(bytes);
            target.MaximumLength = static_cast<USHORT>(bytes);
            target.Buffer = reinterpret_cast<PWSTR>(static_cast<ULONG_PTR>(cursor));
            if (bytes > 0) memcpy(buffer + cursor, value.c_str(), bytes);
            cursor += bytes;
        };

        pack(logon->Logon.LogonDomainName, credential.Domain);
        pack(logon->Logon.UserName, credential.User);
        pack(logon->Logon.Password, credential.Password);

        *rgbSerialization = buffer;
        *cbSerialization = static_cast<ULONG>(totalBytes);
        return true;
    }

    // The single place that decides which KERB_LOGON_SUBMIT_TYPE goes into the blob. Kept as one
    // function on purpose: the packed bytes and the log line that describes them must never be able
    // to disagree, and on 2026-10-14 a second, stale copy of this rule made the log announce a
    // "workstation-unlock blob" while the bytes said otherwise.
    //
    //   CPUS_UNLOCK_WORKSTATION (2) -> KerbWorkstationUnlockLogon (7)
    //   CPUS_LOGON              (1) -> KerbInteractiveLogon        (2)
    //
    // Windows 10/11 report the LOCK SCREEN as CPUS_LOGON (the platform merged the two scenarios), and
    // the merge changes which scenario NUMBER arrives, not what LSA expects inside the blob. See the
    // long note in MayOfferCredential for why KerbWorkstationUnlockLogon is wrong for the lock screen.
    KERB_LOGON_SUBMIT_TYPE MessageTypeForScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario)
    {
        return scenario == CPUS_UNLOCK_WORKSTATION ? KerbWorkstationUnlockLogon : KerbInteractiveLogon;
    }

    // Names the two codes LogonUI reports back. The PAIR is what makes a failed unlock diagnosable,
    // and reading it wrong costs hours: 0xC000006D / 0xC00000E5 is NOT "wrong password" - it is LSA
    // saying the submission could not be carried out at all (on 2026-10-14: a workstation-unlock
    // message type submitted for the merged CPUS_LOGON lock screen, whose zero LogonId names no
    // session). See the message-type note in MayOfferCredential. Written with explicit unsigned
    // casts so the NTSTATUS values do not trip signed/unsigned warnings.
    const wchar_t* LogonFailureHint(NTSTATUS status, NTSTATUS substatus)
    {
        const unsigned code = static_cast<unsigned>(status);
        const unsigned sub = static_cast<unsigned>(substatus);

        if (code == 0xC000006Du && sub == 0xC00000E5u)
            return L"[STATUS_LOGON_FAILURE / STATUS_INTERNAL_ERROR - NOT a wrong password: LSA could "
                   L"not carry the submission out at all. Check the message type against the scenario.]";
        if (sub == 0xC000006Au)
            return L"[STATUS_WRONG_PASSWORD - the stored password really is wrong]";
        if (sub == 0xC0000064u)
            return L"[STATUS_NO_SUCH_USER - that account does not exist on this machine]";
        if (sub == 0xC000006Eu)
            return L"[STATUS_ACCOUNT_RESTRICTION - account policy refused this logon]";
        if (sub == 0xC0000072u)
            return L"[STATUS_ACCOUNT_DISABLED - the account is disabled]";
        if (sub == 0xC0000062u)
            return L"[STATUS_ACCOUNT_LOCKED_OUT - too many failed attempts]";
        return L"";
    }

    // Republished as the suppression deadline; the running agent turns it into SuppressUntilTicks.
    void SuppressAfterFailure()
    {
        const long long until = Btwu::NowDotNetTicks() +
            static_cast<long long>(kFailureSuppressionSeconds) * Btwu::kTicksPerSecond;

        const HANDLE file = CreateFileW(Btwu::PauseFilePath().c_str(), GENERIC_WRITE,
                                        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                                        CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return;

        char buffer[32]{};
        const int length = sprintf_s(buffer, "%lld", until);
        DWORD written = 0;
        WriteFile(file, buffer, static_cast<DWORD>(length), &written, nullptr);
        CloseHandle(file);
    }

    // "S-1-5-21-..." style comparison that does not care about case or a stray space.
    bool EqualSidText(const std::wstring& left, const std::wstring& right)
    {
        return _wcsicmp(left.c_str(), right.c_str()) == 0;
    }

    // ------------------------------------------------------------------ the offer gate, in one place

    // Everything the provider knows about the unlock it would perform, computed *before* it tells
    // LogonUI that a credential exists.
    //
    // WHY PRE-BUILD THE PAYLOAD. LogonUI's contract is: GetCredentialCount says "there is one
    // credential, here is its index, and (optionally) auto-logon it", and only later does it call
    // GetSerialization on that credential. Everything between those two calls is time in which the
    // Bluetooth device can sleep, cred.bin can become unreadable, or the LSA package lookup can fail -
    // and a provider that announced a credential and then could not deliver one is a provider that has
    // broken its half of the deal while LogonUI waits. Measured on 2026-10-14: the logon UI went to
    // its "waiting for the submission" state, GetSerialization was never even called, and the user was
    // left with a lock screen showing no user tile, no password box and no PIN.
    //
    // So the whole submission - decrypt, identity check, LSA package id, packed blob - is prepared at
    // enumeration time. GetSerialization then hands over bytes that already exist. A refusal costs the
    // user nothing (LogonUI keeps its own UI); a broken promise costs them the machine.
    struct PreparedUnlock
    {
        StoredCredential Credential;
        ULONG PackageId = 0;
        std::vector<BYTE> Blob;
        std::wstring Sid;
        bool Ready = false;
    };

    // The one and only answer to "may this provider offer a credential right now?".
    //
    // Deliberately a single function shared by GetCredentialCount (what LogonUI asks to decide whether
    // our tile exists at all) and GetCredentialAt (what it calls to actually obtain it). Two copies of
    // this reasoning is how you end up reporting zero and then handing one over anyway, and this
    // provider has already taken the lock screen's UI away once. Everything here fails closed.
    bool MayOfferCredential(CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario, PreparedUnlock& prepared,
                            std::wstring& why)
    {
        why.clear();
        prepared = PreparedUnlock{};

        // (1) Is the trigger device here, and is the agent's claim still fresh? A stale heartbeat means
        //     the agent is gone or hung, and "the keyboard is present" can no longer be trusted.
        const Btwu::Presence presence = Btwu::ReadPresence();
        const bool unlock = Btwu::ShouldUnlock(presence);
        if (!unlock)
        {
            why = L"the unlock decision is false (device away, stale heartbeat, paused, or suppressed)";
            return false;
        }

        // (2) Kill switch, read from disk by the DLL itself. Not routed through the agent on purpose:
        //     the moment the lock screen misbehaves, stopping the agent is the first thing anyone does,
        //     and the switch has to keep working after that.
        if (Btwu::IsDisabled())
        {
            why = L"disable.cp is present";
            return false;
        }

        // (3) Can the identity in cred.bin be attached to the user at THIS lock screen? See
        //     Btwu::SessionUserSid. This is the check that exists because of 2026-10-14.
        if (!LoadStoredCredential(prepared.Credential))
        {
            why = L"cannot load or decrypt cred.bin";
            return false;
        }
        if (prepared.Credential.Sid.empty())
        {
            why = L"cred.bin carries no SID - re-run 'btwu enroll'";
            return false;
        }

        std::wstring sessionSid;
        if (!Btwu::SessionUserSid(sessionSid))
        {
            why = L"cannot resolve the SID of the user at this lock screen - refusing rather than guess";
            return false;
        }
        if (!EqualSidText(prepared.Credential.Sid, sessionSid))
        {
            why = L"cred.bin holds " + prepared.Credential.Sid + L" but this session is " + sessionSid +
                  L" - re-run 'btwu enroll' while signed in as that user";
            return false;
        }
        prepared.Sid = prepared.Credential.Sid;

        // (4) Resolve the authentication package and pack the blob now, so that the promise made by
        //     GetCredentialCount is one this provider can actually keep.
        if (!AuthenticationPackageId(prepared.PackageId))
        {
            why = L"no Negotiate authentication package";
            return false;
        }

        BYTE* blob = nullptr;
        ULONG blobBytes = 0;

        // The message type follows the SCENARIO, not the "this is an unlock" instinct. Getting this
        // wrong is what made every submission fail on 2026-10-14 with 0xC000006D / 0xC00000E5 - LSA
        // answered STATUS_INTERNAL_ERROR, which looks like a bad password and is not.
        //
        //   CPUS_UNLOCK_WORKSTATION (2) -> KerbWorkstationUnlockLogon (7)
        //   CPUS_LOGON              (1) -> KerbInteractiveLogon        (2)
        //
        // Reference implementations agree, and both pack the strings exactly as this file does (byte
        // offsets, MaximumLength == Length, CoTaskMemAlloc):
        //
        //   * Chromium's credential_provider/gaiacp/auth_utils.cc, KerbInteractiveUnlockLogonInit:
        //       case CPUS_UNLOCK_WORKSTATION: MessageType = KerbWorkstationUnlockLogon; break;
        //       case CPUS_LOGON:              MessageType = KerbInteractiveLogon;      break;
        //     with the remark that CredPackAuthenticationBuffer always yields KerbInteractiveLogon and
        //     "if we only handled CPUS_LOGON, this drawback would not be a problem".
        //   * Microsoft's own CredentialProvider sample, which passes its scenario number into
        //     KerbInteractiveUnlockLogonInit for precisely this decision.
        //
        // THE TRAP THIS FILE FELL INTO: Windows 10/11 report the LOCK SCREEN as CPUS_LOGON - the
        // platform merged the two scenarios - so the instinct is to treat it as the unlock scenario
        // all the way down, message type included. That is wrong. The merging changes which scenario
        // NUMBER arrives, not what LSA expects inside the blob. KerbWorkstationUnlockLogon tells LSA
        // to unlock the session named by LogonId, and LogonId is deliberately left zero here for
        // Winlogon to fill in; LSA cannot resolve a zero LUID, so it fails before ever comparing the
        // password. KerbInteractiveLogon asks for no such session and carries no such requirement.
        //
        // The struct stays KERB_INTERACTIVE_UNLOCK_LOGON in both cases - only MessageType differs.
        const KERB_LOGON_SUBMIT_TYPE messageType = MessageTypeForScenario(scenario);
        if (!BuildUnlockSerialization(prepared.Credential, messageType, &blob, &blobBytes) ||
            blob == nullptr || blobBytes == 0)
        {
            why = L"cannot build the unlock serialization";
            return false;
        }
        static_assert(sizeof(BYTE) == 1, "byte copy below assumes bytes");
        prepared.Blob.assign(blob, blob + blobBytes);
        CoTaskMemFree(blob);

        prepared.Ready = true;
        return true;
    }

    // ---------------------------------------------------------------------------- presence watch

    // The agent rewrites presence.dat on a keepalive (and immediately on every real transition), so
    // watching the directory for that write is both faster and far cheaper than re-reading and
    // re-parsing the file on a one-second timer. The timer is kept only as a backstop for a missed
    // notification; the steady state is one wakeup per change.
    const DWORD kPresenceRecheckMs = 5000;

    // LogonUI does not re-poll credential providers on its own. Without this, walking back to the
    // machine with the lock screen already up would leave the unlock tile hidden until the user
    // touches something - which defeats the whole point.
    class PresenceWatcher
    {
    public:
        static PresenceWatcher& Instance()
        {
            static PresenceWatcher watcher;
            return watcher;
        }

        void Advise(ICredentialProviderEvents* events, UINT_PTR context)
        {
            EnterCriticalSection(&m_lock);
            if (m_stream != nullptr)
            {
                m_stream->Release();
                m_stream = nullptr;
            }
            if (events != nullptr)
                CoMarshalInterThreadInterfaceInStream(IID_ICredentialProviderEvents, events, &m_stream);
            m_context = context;
            m_adoptStream = true;
            LeaveCriticalSection(&m_lock);

            EnsureThread();
            Wake();
        }

        void Unadvise()
        {
            EnterCriticalSection(&m_lock);
            m_dropEvents = true;
            LeaveCriticalSection(&m_lock);
            Wake();
        }

    private:
        PresenceWatcher()
        {
            InitializeCriticalSection(&m_lock);
            m_wake = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            m_lastDecision = Btwu::ShouldUnlock(Btwu::ReadPresence());
        }

        ~PresenceWatcher()
        {
            if (m_thread != nullptr) CloseHandle(m_thread);
            if (m_wake != nullptr) CloseHandle(m_wake);
            DeleteCriticalSection(&m_lock);
        }

        void EnsureThread()
        {
            EnterCriticalSection(&m_lock);
            if (m_thread == nullptr)
            {
                m_thread = CreateThread(nullptr, 0, &PresenceWatcher::ThreadProc, this, 0, nullptr);
                if (m_thread != nullptr)
                    InterlockedIncrement(&g_watcherThreads);
            }
            LeaveCriticalSection(&m_lock);
        }

        void Wake()
        {
            if (m_wake != nullptr) SetEvent(m_wake);
        }

        static DWORD WINAPI ThreadProc(LPVOID parameter)
        {
            auto* self = static_cast<PresenceWatcher*>(parameter);
            CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

            ICredentialProviderEvents* events = nullptr;
            UINT_PTR context = 0;

            // Fails when the data directory does not exist yet; the timeout-only wait below is then
            // exactly the old behaviour, so this degrades to "slower" rather than "broken".
            HANDLE change = FindFirstChangeNotificationW(
                Btwu::DataDirectory().c_str(), FALSE,
                FILE_NOTIFY_CHANGE_LAST_WRITE | FILE_NOTIFY_CHANGE_FILE_NAME);

            // An exception that escapes a thread procedure calls std::terminate, which would kill
            // LogonUI - the one outcome this DLL must never cause. Everything below is wrapped, and the
            // loop keeps going so a single bad iteration cannot silently stop presence updates either.
            try
            {
                for (;;)
                {
                    bool adopt = false, drop = false;
                    IStream* stream = nullptr;

                    EnterCriticalSection(&self->m_lock);
                    if (self->m_adoptStream)
                    {
                        self->m_adoptStream = false;
                        adopt = true;
                        stream = self->m_stream;
                        self->m_stream = nullptr;
                        context = self->m_context;
                    }
                    if (self->m_dropEvents)
                    {
                        self->m_dropEvents = false;
                        drop = true;
                    }
                    LeaveCriticalSection(&self->m_lock);

                    if (adopt)
                    {
                        if (events != nullptr) { events->Release(); events = nullptr; }
                        if (stream != nullptr &&
                            FAILED(CoGetInterfaceAndReleaseStream(stream, IID_ICredentialProviderEvents,
                                                                  reinterpret_cast<void**>(&events))))
                            events = nullptr;
                        LogLine(L"notifier attached (%s)", events != nullptr ? L"ok" : L"failed");
                    }
                    if (drop && events != nullptr)
                    {
                        events->Release();
                        events = nullptr;
                    }

                    const bool decision = Btwu::ShouldUnlock(Btwu::ReadPresence());
                    if (events != nullptr && decision != self->m_lastDecision)
                    {
                        // Only a transition is worth a callback: LogonUI re-enumerates on every
                        // CredentialsChanged, and our gate re-reads presence.dat each time it does.
                        // This is what makes the credential appear the moment the keyboard reconnects
                        // instead of waiting for the user to touch the screen.
                        LogLine(L"presence changed -> notifying LogonUI (unlock=%s)",
                                decision ? L"yes" : L"no");
                        events->CredentialsChanged(context);
                    }
                    self->m_lastDecision = decision;

                    if (change != INVALID_HANDLE_VALUE)
                    {
                        const HANDLE handles[2] = { self->m_wake, change };
                        const DWORD signalled = WaitForMultipleObjects(2, handles, FALSE, kPresenceRecheckMs);
                        if (signalled == WAIT_OBJECT_0 + 1)
                            FindNextChangeNotification(change);
                    }
                    else
                    {
                        WaitForSingleObject(self->m_wake, kPresenceRecheckMs);
                    }
                }
            }
            catch (const std::exception& error)
            {
                LogLine(L"watcher thread: %S", error.what());
            }
            catch (...)
            {
                LogLine(L"watcher thread: unknown exception");
            }

            if (change != INVALID_HANDLE_VALUE) FindCloseChangeNotification(change);
            if (events != nullptr) events->Release();
            CoUninitialize();
            return 0;
        }

        CRITICAL_SECTION m_lock{};
        HANDLE m_thread = nullptr;
        HANDLE m_wake = nullptr;
        IStream* m_stream = nullptr;
        UINT_PTR m_context = 0;
        bool m_adoptStream = false;
        bool m_dropEvents = false;
        bool m_lastDecision = false;
    };

    // ------------------------------------------------------------------------------- field layout

    enum FIELD_ID
    {
        FI_LABEL = 0,
        FI_STATUS = 1,
        FI_ENTRY = 2,
        FI_SUBMIT = 3,
        FI_COUNT = 4,
    };

    // WHY THERE IS AN EDITABLE FIELD ON A TILE THAT HAS NOTHING TO TYPE.
    //
    // Without it, pressing Enter at the lock screen did nothing and the only way to submit was to
    // click the submit button (LogonUI draws it as the right-arrow button). The reason is documented
    // in Microsoft's Credential Provider Technical Reference, on CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE:
    //
    //     CPFIS_FOCUSED - ... This interactive state MAY NOT BE SPECIFIED FOR UN-EDITABLE FIELD TYPES.
    //
    // With LARGE_TEXT / SMALL_TEXT / SUBMIT_BUTTON only, no field in the tile can hold keyboard focus,
    // so there is nothing for the Enter key to be delivered to. An empty CPFT_EDIT_TEXT carrying
    // CPFIS_FOCUSED is the supported way to give the tile a keyboard target; whatever the user types
    // is ignored, because the credential being submitted was decrypted and packed long before this
    // object existed.
    const CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR g_fieldDescriptors[FI_COUNT] =
    {
        { FI_LABEL,  CPFT_LARGE_TEXT,    const_cast<PWSTR>(L"蓝牙在场解锁"), { 0 } },
        { FI_STATUS, CPFT_SMALL_TEXT,    const_cast<PWSTR>(L""),            { 0 } },
        { FI_ENTRY,  CPFT_EDIT_TEXT,     const_cast<PWSTR>(L"按回车解锁"),   { 0 } },
        { FI_SUBMIT, CPFT_SUBMIT_BUTTON, const_cast<PWSTR>(L"解锁"),        { 0 } },
    };

    const wchar_t* kLabel = L"蓝牙在场解锁";
    const wchar_t* kStatusPresent = L"已检测到触发设备";
    const wchar_t* kStatusAbsent = L"触发设备不在场";
    const wchar_t* kEntry = L"";
    const wchar_t* kSubmit = L"解锁";

    // ------------------------------------------------------------------------------ credential

    // IConnectableCredentialProviderCredential is deliberately not implemented: in the Windows SDK
    // it re-inherits ICredentialProviderCredential, which would hand this class two IUnknown
    // subobjects and break COM identity. The unlock scenario does not need it.
    class CBtwuCredential : public ICredentialProviderCredential2
    {
    public:
        // Takes the unlock the provider already prepared. Nothing is read from disk here: by the time
        // this object exists, cred.bin has been decrypted, the identity has been checked against the
        // signed-in user, and the packed blob is sitting in memory.
        explicit CBtwuCredential(CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario, PreparedUnlock prepared)
            : m_scenario(scenario), m_prepared(std::move(prepared))
        {
            InterlockedIncrement(&g_referenceCount);
        }

        // IUnknown
        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
        {
            if (ppv == nullptr) return E_POINTER;
            *ppv = nullptr;

            if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_ICredentialProviderCredential))
                *ppv = static_cast<ICredentialProviderCredential*>(this);
            else if (IsEqualIID(riid, IID_ICredentialProviderCredential2))
                *ppv = static_cast<ICredentialProviderCredential2*>(this);
            else
                return E_NOINTERFACE;

            AddRef();
            return S_OK;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_references); }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const ULONG count = InterlockedDecrement(&m_references);
            if (count == 0) delete this;
            return count;
        }

        // ICredentialProviderCredential
        IFACEMETHODIMP Advise(ICredentialProviderCredentialEvents* events) override
        {
            if (m_events != nullptr) m_events->Release();
            m_events = events;
            if (m_events != nullptr) m_events->AddRef();
            return S_OK;
        }

        IFACEMETHODIMP UnAdvise() override
        {
            if (m_events != nullptr) { m_events->Release(); m_events = nullptr; }
            return S_OK;
        }

        IFACEMETHODIMP SetSelected(BOOL* pbAutoLogon) override
        {
            // pbAutoLogon means "submit this credential without waiting for the user". The default is
            // FALSE, and that default is the whole safety story:
            //
            //   FALSE - our tile is simply the selected one. The lock screen looks exactly as it always
            //           does (user tile, PIN, password box, fingerprint) and the user presses Enter or
            //           clicks 解锁 to have the stored password submitted. Because nothing has been
            //           announced to LogonUI in advance, there is no state it can get stuck in.
            //   TRUE  - LogonUI submits immediately, which makes a manual Win+L bounce straight back to
            //           the desktop. Enabled by the `autosubmit` marker file, and only safe because
            //           MayOfferCredential has already validated and packed the whole submission.
            const bool autoSubmit = Btwu::AutoSubmitUnlock();
            if (pbAutoLogon != nullptr) *pbAutoLogon = autoSubmit ? TRUE : FALSE;
            LogLine(L"SetSelected -> pbAutoLogon=%d (autosubmit %s, prepared=%d)",
                    autoSubmit ? 1 : 0, autoSubmit ? L"on" : L"off", m_prepared.Ready ? 1 : 0);
            return S_OK;
        }

        IFACEMETHODIMP SetDeselected() override { return S_OK; }

        IFACEMETHODIMP GetFieldState(DWORD fieldId, CREDENTIAL_PROVIDER_FIELD_STATE* pcpfs,
                                     CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* pcpfis) override
        {
            if (pcpfs == nullptr || pcpfis == nullptr) return E_POINTER;
            *pcpfis = CPFIS_NONE;

            switch (fieldId)
            {
            case FI_LABEL:  *pcpfs = CPFS_DISPLAY_IN_BOTH;          return S_OK;
            case FI_STATUS: *pcpfs = CPFS_DISPLAY_IN_SELECTED_TILE; return S_OK;
            case FI_SUBMIT: *pcpfs = CPFS_DISPLAY_IN_SELECTED_TILE; return S_OK;
            case FI_ENTRY:
                // The point of this field: it is the only thing in the tile that can take keyboard
                // focus, and a focused edit field is what makes Enter reach the submit button. See the
                // field-layout note above.
                *pcpfs = CPFS_DISPLAY_IN_SELECTED_TILE;
                *pcpfis = CPFIS_FOCUSED;
                return S_OK;
            default:                                                return E_INVALIDARG;
            }
        }

        IFACEMETHODIMP GetStringValue(DWORD fieldId, PWSTR* ppwsz) override
        {
            if (ppwsz == nullptr) return E_POINTER;
            switch (fieldId)
            {
            case FI_LABEL:  return SHStrDupW(kLabel, ppwsz);
            case FI_STATUS: return SHStrDupW(
                Btwu::ShouldUnlock(Btwu::ReadPresence()) ? kStatusPresent : kStatusAbsent, ppwsz);
            case FI_ENTRY:  return SHStrDupW(kEntry, ppwsz);
            case FI_SUBMIT: return SHStrDupW(kSubmit, ppwsz);
            default:        return E_INVALIDARG;
            }
        }

        IFACEMETHODIMP GetBitmapValue(DWORD, HBITMAP* phbmp) override
        {
            if (phbmp != nullptr) *phbmp = nullptr;
            return E_NOTIMPL;
        }

        IFACEMETHODIMP GetCheckboxValue(DWORD, BOOL* pbChecked, PWSTR* ppwszLabel) override
        {
            if (pbChecked != nullptr) *pbChecked = FALSE;
            if (ppwszLabel != nullptr) *ppwszLabel = nullptr;
            return E_NOTIMPL;
        }

        IFACEMETHODIMP GetSubmitButtonValue(DWORD fieldId, DWORD* pdwAdjacentTo) override
        {
            if (pdwAdjacentTo == nullptr) return E_POINTER;
            if (fieldId != FI_SUBMIT) return E_INVALIDARG;
            // Attach the button to the edit field, not to the status line: this is where the arrow is
            // drawn, and it is the field the button is associated with for keyboard purposes.
            *pdwAdjacentTo = FI_ENTRY;
            return S_OK;
        }

        IFACEMETHODIMP GetComboBoxValueCount(DWORD, DWORD* pcItems, DWORD*) override
        {
            if (pcItems != nullptr) *pcItems = 0;
            return E_NOTIMPL;
        }

        IFACEMETHODIMP GetComboBoxValueAt(DWORD, DWORD, PWSTR*) override { return E_NOTIMPL; }

        IFACEMETHODIMP SetStringValue(DWORD fieldId, PCWSTR value) override
        {
            // Deliberately discard whatever the user types. The credential being submitted was
            // decrypted, identity-checked and packed during enumeration, so nothing typed here can
            // change it - accepting and ignoring is what keeps the box editable (and therefore
            // focusable, and therefore able to deliver Enter). E_NOTIMPL would make LogonUI treat the
            // field as read-only and the whole point of it would be lost.
            (void)value;
            return fieldId == FI_ENTRY ? S_OK : E_INVALIDARG;
        }

        IFACEMETHODIMP SetCheckboxValue(DWORD, BOOL) override { return E_NOTIMPL; }
        IFACEMETHODIMP SetComboBoxSelectedValue(DWORD, DWORD) override { return E_NOTIMPL; }
        IFACEMETHODIMP CommandLinkClicked(DWORD) override { return E_NOTIMPL; }

        IFACEMETHODIMP GetSerialization(CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* pcpgsr,
                                        CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs,
                                        PWSTR* ppwszOptionalStatusText,
                                        CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon) override
        {
            if (pcpgsr == nullptr || pcpcs == nullptr) return E_POINTER;
            if (ppwszOptionalStatusText != nullptr) *ppwszOptionalStatusText = nullptr;
            if (pcpsiOptionalStatusIcon != nullptr) *pcpsiOptionalStatusIcon = CPSI_NONE;

            // The only place in this DLL that allocates on a path LogonUI drives, so it is also the only
            // place a std::bad_alloc could reach LogonUI. Guarded: on any failure the provider offers
            // nothing and the built-in password tile stays exactly as it was.
            BTWU_COM_GUARD({
                *pcpgsr = CPGSR_NO_CREDENTIAL_NOT_FINISHED;
                ZeroMemory(pcpcs, sizeof(*pcpcs));

                // The blob was decrypted, validated and packed during enumeration, so nothing here can
                // fail for a technical reason any more. The only things re-read are the two emergency
                // switches, because they are two file existence tests and the operator asking for them
                // outranks everything else.
                //
                // Deliberately NOT the presence decision. Presence is judged once, when the lock screen
                // appears - that is the moment the answer matters. Re-judging it here would mean a
                // keyboard that dozes off while the user is pressing Enter silently cancels the unlock:
                // the user gets no error, no message, nothing happens, and the obvious conclusion is
                // that the feature is broken. The gap being protected is milliseconds in auto mode and
                // however long the user takes to press a key in the default mode, with that user
                // standing in front of the screen - so the presence window it costs is worth far less
                // than a silently dropped unlock.
                if (Btwu::IsDisabled() || Btwu::IsPaused())
                {
                    LogLine(L"GetSerialization: disable.cp or pause.until is in force, refusing");
                    return E_FAIL;
                }

                if (!m_prepared.Ready || m_prepared.Blob.empty())
                {
                    LogLine(L"GetSerialization: nothing was prepared - refusing");
                    return E_FAIL;
                }

                // LogonUI frees rgbSerialization with CoTaskMemFree, so the bytes need their own
                // CoTaskMem allocation rather than a pointer into the std::vector that owns them.
                auto* serialization = static_cast<BYTE*>(CoTaskMemAlloc(m_prepared.Blob.size()));
                if (serialization == nullptr) return E_OUTOFMEMORY;
                memcpy(serialization, m_prepared.Blob.data(), m_prepared.Blob.size());

                pcpcs->ulAuthenticationPackage = m_prepared.PackageId;
                pcpcs->clsidCredentialProvider = CLSID_BtwuCredProv;
                pcpcs->rgbSerialization = serialization;
                pcpcs->cbSerialization = static_cast<ULONG>(m_prepared.Blob.size());

                SecureZeroMemory(&m_prepared.Credential.Password[0],
                                 m_prepared.Credential.Password.size() * sizeof(wchar_t));

                // Describe what we ACTUALLY packed, read back out of the bytes themselves rather
                // than re-derived from the scenario: these are the bytes LSA will receive, so the log
                // cannot drift from them. MessageType sits at offset 0, so reading it needs no more
                // than the size check - the string fields are byte offsets and must not be followed.
                const auto* packedBlob =
                    reinterpret_cast<const KERB_INTERACTIVE_UNLOCK_LOGON*>(m_prepared.Blob.data());
                const KERB_LOGON_SUBMIT_TYPE packedType =
                    m_prepared.Blob.size() >= sizeof(KERB_INTERACTIVE_UNLOCK_LOGON)
                        ? packedBlob->Logon.MessageType
                        : KerbInteractiveLogon;

                *pcpgsr = CPGSR_RETURN_CREDENTIAL_FINISHED;
                LogLine(L"GetSerialization: submitted %s for %s (%llu bytes, scenario %u)",
                        packedType == KerbWorkstationUnlockLogon
                            ? L"workstation-unlock blob" : L"interactive-logon blob",
                        m_prepared.Credential.User.c_str(),
                        static_cast<unsigned long long>(m_prepared.Blob.size()),
                        static_cast<unsigned>(m_scenario));
                return S_OK;
            })
        }

        IFACEMETHODIMP ReportResult(NTSTATUS status, NTSTATUS substatus, PWSTR* ppwszOptionalStatusText,
                                    CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon) override
        {
            if (ppwszOptionalStatusText != nullptr) *ppwszOptionalStatusText = nullptr;
            if (pcpsiOptionalStatusIcon != nullptr) *pcpsiOptionalStatusIcon = CPSI_NONE;

            if (status == 0 /* STATUS_SUCCESS */)
            {
                LogLine(L"ReportResult: unlock succeeded");
                // The keyboard was present an instant ago - it just unlocked this machine. Tell the
                // agent to stop treating its idle disconnect as "the user walked away", so the screen
                // is not locked again a minute later. See Btwu::kUnlockCooldownSeconds.
                Btwu::WriteUnlockCooldown();
                LogLine(L"  post-unlock dwell published: no absence lock for %llds",
                        Btwu::kUnlockCooldownSeconds);
            }
            else
            {
                LogLine(L"ReportResult: logon failed (0x%08X / 0x%08X) %s, suppressing auto-unlock for %llds",
                        static_cast<unsigned>(status), static_cast<unsigned>(substatus),
                        LogonFailureHint(status, substatus), kFailureSuppressionSeconds);
                SuppressAfterFailure();
            }
            return S_OK;
        }

        // ICredentialProviderCredential2
        IFACEMETHODIMP GetUserSid(PWSTR* ppszSid) override
        {
            if (ppszSid == nullptr) return E_POINTER;
            *ppszSid = nullptr;

            // Nothing is read or decrypted here any more: the provider only ever constructs this object
            // with a SID it has already checked, and only when that SID is provably the SID of the user
            // at this lock screen. Returning E_NOTIMPL is still legal and means "unknown user".
            //
            // THIS VALUE DECIDES WHETHER THE LOCK SCREEN RENDERS AT ALL. LogonUI uses it to attach the
            // credential to a user tile, and in the merged scenario it is the only thing that tells
            // LogonUI which tile this belongs to. Handing it a SID that belongs to no user attached to
            // this session is what took the lock screen's UI away on 2026-10-14: no user tile, no
            // password box, no PIN; only Windows Hello still worked. The check that prevents a repeat
            // now lives in MayOfferCredential, *before* anything is offered, and this line is what makes
            // the value diagnosable in one look.
            BTWU_COM_GUARD({
                LogLine(L"GetUserSid -> %s", m_prepared.Sid.empty() ? L"(none)" : m_prepared.Sid.c_str());
                if (m_prepared.Sid.empty() || !LocalAccountSid(m_prepared.Sid))
                {
                    // Refusing is the safe answer: an unknown SID leaves the credential unattachable to
                    // a user, and LogonUI is better off without it than with a bogus one.
                    LogLine(L"  refused: not a local account SID - re-run 'btwu enroll' to rewrite it");
                    return E_NOTIMPL;
                }

                const size_t bytes = (m_prepared.Sid.size() + 1) * sizeof(wchar_t);
                auto* copy = static_cast<PWSTR>(CoTaskMemAlloc(bytes));
                if (copy == nullptr) return E_OUTOFMEMORY;
                memcpy(copy, m_prepared.Sid.c_str(), bytes);
                *ppszSid = copy;
                return S_OK;
            })
        }

    private:
        ~CBtwuCredential()
        {
            if (m_events != nullptr) m_events->Release();
            InterlockedDecrement(&g_referenceCount);
        }

        long m_references = 1;
        CREDENTIAL_PROVIDER_USAGE_SCENARIO m_scenario = CPUS_INVALID;
        ICredentialProviderCredentialEvents* m_events = nullptr;
        PreparedUnlock m_prepared;
    };

    // -------------------------------------------------------------------------------- provider

    class CBtwuProvider : public ICredentialProvider
    {
    public:
        CBtwuProvider() { InterlockedIncrement(&g_referenceCount); }

        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
        {
            if (ppv == nullptr) return E_POINTER;
            if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_ICredentialProvider))
            {
                *ppv = static_cast<ICredentialProvider*>(this);
                AddRef();
                return S_OK;
            }
            *ppv = nullptr;
            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_references); }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const ULONG count = InterlockedDecrement(&m_references);
            if (count == 0) delete this;
            return count;
        }

        IFACEMETHODIMP SetUsageScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO scenario, DWORD) override
        {
            // Wrapped like GetSerialization even though nothing here allocates on the happy path:
            // LogLine builds a std::string, and this DLL runs inside LogonUI, where an exception
            // crossing a COM boundary hands the sign-in UI garbage. Failing this call is harmless -
            // LogonUI just ignores the provider and the built-in tiles behave exactly as before.
            BTWU_COM_GUARD({
                LogLine(L"SetUsageScenario(%u) in pid %lu", static_cast<unsigned>(scenario),
                        GetCurrentProcessId());

                // The kill switch, checked before anything else so that dropping disable.cp makes this
                // provider vanish from every scenario without needing an uninstall. It is the escape
                // hatch for a lock screen this provider has broken: the user has a shell (via another
                // account, safe mode, or by unlocking with Windows Hello) and can create the file.
                if (Btwu::IsDisabled())
                {
                    LogLine(L"  refused: %s is present - provider disabled by operator",
                            Btwu::DisableFilePath().c_str());
                    return E_NOTIMPL;
                }

                // Unlock only, on purpose. See the block comment at the top of this file for why
                // CPUS_LOGON counts as an unlock on Windows 10/11, and why it needs a second check.
                switch (scenario)
                {
                case CPUS_UNLOCK_WORKSTATION:
                    // The classic lock screen. Unambiguous, and what an older Windows asks for.
                    LogLine(L"  accepted: CPUS_UNLOCK_WORKSTATION");
                    break;

                case CPUS_LOGON:
                {
                    // The merged Windows 10/11 scenario: a lock screen AND a logon screen. Only the
                    // former may auto-unlock, and a signed-in session is the documented way to tell
                    // them apart - see Btwu::QuerySessionUser.
                    wchar_t user[Btwu::kMaxSessionNameChars]{};
                    wchar_t domain[Btwu::kMaxSessionNameChars]{};
                    const bool signedIn = Btwu::QuerySessionUser(user, Btwu::kMaxSessionNameChars,
                                                                 domain, Btwu::kMaxSessionNameChars);
                    LogLine(L"  CPUS_LOGON: session user = '%s\\%s' -> treating as %s", domain, user,
                            signedIn ? L"lock screen (accepted)" : L"logon screen (refused)");
                    if (!signedIn)
                    {
                        // A real logon screen: nobody is signed in, so there is no session to come
                        // back to and auto sign-in must stay impossible. This also covers a fresh
                        // boot, which is deliberately not supported.
                        return E_NOTIMPL;
                    }
                    break;
                }

                default:
                    // CPUS_CREDUI is the important one: accepting it would let the stored password
                    // silently approve a UAC elevation prompt. CPUS_CHANGE_PASSWORD and CPUS_PLAP
                    // have nothing to do with unlocking.
                    LogLine(L"  refused: scenario %u is not an unlock", static_cast<unsigned>(scenario));
                    return E_NOTIMPL;
                }

                // Recorded only when accepted, so a refused call cannot change the message type the
                // credential will later build.
                m_scenario = scenario;
                return S_OK;
            })
        }

        IFACEMETHODIMP SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION*) override
        {
            return S_OK;
        }

        IFACEMETHODIMP Advise(ICredentialProviderEvents* events, UINT_PTR context) override
        {
            PresenceWatcher::Instance().Advise(events, context);
            return S_OK;
        }

        IFACEMETHODIMP UnAdvise() override
        {
            PresenceWatcher::Instance().Unadvise();
            return S_OK;
        }

        IFACEMETHODIMP GetFieldDescriptorCount(DWORD* pdwCount) override
        {
            if (pdwCount == nullptr) return E_POINTER;
            *pdwCount = FI_COUNT;
            LogLine(L"GetFieldDescriptorCount -> %u", FI_COUNT);
            return S_OK;
        }

        IFACEMETHODIMP GetFieldDescriptorAt(DWORD index, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** ppcpfd) override
        {
            if (ppcpfd == nullptr) return E_POINTER;
            *ppcpfd = nullptr;
            if (index >= FI_COUNT) return E_INVALIDARG;

            auto* descriptor = static_cast<CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR*>(
                CoTaskMemAlloc(sizeof(CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR)));
            if (descriptor == nullptr) return E_OUTOFMEMORY;
            ZeroMemory(descriptor, sizeof(*descriptor));

            descriptor->dwFieldID = g_fieldDescriptors[index].dwFieldID;
            descriptor->cpft = g_fieldDescriptors[index].cpft;
            descriptor->guidFieldType = g_fieldDescriptors[index].guidFieldType;
            if (FAILED(SHStrDupW(g_fieldDescriptors[index].pszLabel, &descriptor->pszLabel)))
            {
                CoTaskMemFree(descriptor);
                return E_OUTOFMEMORY;
            }
            *ppcpfd = descriptor;
            return S_OK;
        }

        IFACEMETHODIMP GetCredentialCount(DWORD* pdwCount, DWORD* pdwDefault, BOOL* pbAutoLogonWithDefault) override
        {
            if (pdwCount == nullptr || pdwDefault == nullptr || pbAutoLogonWithDefault == nullptr)
                return E_POINTER;

            BTWU_COM_GUARD({
                // Re-evaluated on every enumeration, including the re-enumeration LogonUI performs when
                // the watcher raises CredentialsChanged. MayOfferCredential carries the reasoning; this
                // call is the only place the policy is applied.
                //
                // Note what this does NOT do: it does not announce an auto-logon unless the operator
                // asked for one. In the default mode the credential is offered as the default *tile*
                // and LogonUI keeps its own sign-in UI on screen, so pressing Enter submits the stored
                // password. That is what makes the failure mode of 2026-10-14 unreachable.
                std::wstring why;
                m_prepared = PreparedUnlock{};
                const bool offer = MayOfferCredential(m_scenario, m_prepared, why);

                const bool autoSubmit = offer && Btwu::AutoSubmitUnlock();

                if (offer)
                {
                    *pdwCount = 1;
                    *pdwDefault = 0;
                    *pbAutoLogonWithDefault = autoSubmit ? TRUE : FALSE;
                }
                else
                {
                    // Invisible: the built-in providers handle the lock screen exactly as before.
                    *pdwCount = 0;
                    *pdwDefault = CREDENTIAL_PROVIDER_NO_DEFAULT;
                    *pbAutoLogonWithDefault = FALSE;
                }

                if (offer)
                {
                    LogLine(L"GetCredentialCount -> count=1 default=0 autoLogon=%d (scenario=%u, "
                            L"autosubmit marker %s, prepared %llu bytes for %s)",
                            autoSubmit ? 1 : 0, static_cast<unsigned>(m_scenario),
                            Btwu::AutoSubmitUnlock() ? L"present" : L"absent",
                            static_cast<unsigned long long>(m_prepared.Blob.size()),
                            m_prepared.Credential.User.c_str());
                }
                else
                {
                    LogLine(L"GetCredentialCount -> count=0 (scenario=%u): %s",
                            static_cast<unsigned>(m_scenario), why.c_str());
                }
                return S_OK;
            })
        }

        IFACEMETHODIMP GetCredentialAt(DWORD index, ICredentialProviderCredential** ppcpc) override
        {
            if (ppcpc == nullptr) return E_POINTER;
            *ppcpc = nullptr;
            if (index != 0) return E_INVALIDARG;

            // The same gate GetCredentialCount just answered with, and the same prepared payload it
            // measured. LogonUI only ever asks for indices below the count it was given, so this is belt
            // and braces - but a provider that can be talked into handing over a credential it said it
            // did not have is exactly the class of surprise that took the lock screen's UI away once.
            std::wstring why;
            PreparedUnlock prepared;
            const bool offer = MayOfferCredential(m_scenario, prepared, why);
            LogLine(L"GetCredentialAt(0) in scenario %u -> %s", static_cast<unsigned>(m_scenario),
                    offer ? L"offered" : L"refused (the count said zero)");
            if (!offer) return E_INVALIDARG;

            auto* credential = new (std::nothrow) CBtwuCredential(m_scenario, std::move(prepared));
            if (credential == nullptr) return E_OUTOFMEMORY;

            const HRESULT hr = credential->QueryInterface(IID_ICredentialProviderCredential,
                                                          reinterpret_cast<void**>(ppcpc));
            credential->Release();
            return hr;
        }

    private:
        ~CBtwuProvider() { InterlockedDecrement(&g_referenceCount); }

        long m_references = 1;
        CREDENTIAL_PROVIDER_USAGE_SCENARIO m_scenario = CPUS_INVALID;

        // Kept between GetCredentialCount and GetCredentialAt so the log line in the latter can report
        // what the former measured, and so a caller that inspects the provider after enumeration sees
        // the same payload the credential was built from.
        PreparedUnlock m_prepared;
    };

    // --------------------------------------------------------------------------- class factory

    class CBtwuClassFactory : public IClassFactory
    {
    public:
        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
        {
            if (ppv == nullptr) return E_POINTER;
            if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IClassFactory))
            {
                *ppv = static_cast<IClassFactory*>(this);
                AddRef();
                return S_OK;
            }
            *ppv = nullptr;
            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_references); }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const ULONG count = InterlockedDecrement(&m_references);
            if (count == 0) delete this;
            return count;
        }

        IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
        {
            if (ppv == nullptr) return E_POINTER;
            *ppv = nullptr;
            if (outer != nullptr) return CLASS_E_NOAGGREGATION;

            auto* provider = new (std::nothrow) CBtwuProvider();
            if (provider == nullptr) return E_OUTOFMEMORY;

            const HRESULT hr = provider->QueryInterface(riid, ppv);
            provider->Release();
            return hr;
        }

        IFACEMETHODIMP LockServer(BOOL lock) override
        {
            if (lock) InterlockedIncrement(&g_serverLocks);
            else InterlockedDecrement(&g_serverLocks);
            return S_OK;
        }

    private:
        long m_references = 1;
    };

} // namespace

// ------------------------------------------------------------------------------------ exports

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    if (ppv == nullptr) return E_POINTER;
    *ppv = nullptr;
    if (!IsEqualCLSID(rclsid, CLSID_BtwuCredProv)) return CLASS_E_CLASSNOTAVAILABLE;

    auto* factory = new (std::nothrow) CBtwuClassFactory();
    if (factory == nullptr) return E_OUTOFMEMORY;

    const HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

STDAPI DllCanUnloadNow()
{
    // The presence watcher owns a thread that outlives every COM object, because LogonUI has to keep
    // being told when the device comes back. Letting COM unload the DLL underneath that thread would
    // unmap the code it is executing - the one failure mode here that could take LogonUI down, and the
    // exact bug that "the reference implementation" gets away with only because it is never unloaded
    // while advised. Keep the image resident for as long as the thread lives.
    if (WatcherThreadAlive()) return S_FALSE;

    return (g_referenceCount == 0 && g_serverLocks == 0) ? S_OK : S_FALSE;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(instance);
        // No logging here on purpose. DllMain runs under the loader lock, and CreateFileW/WriteFile can
        // load a filter driver or a DLL in turn - a documented deadlock that would freeze LogonUI before
        // any credential provider is even enumerated. The first SetUsageScenario logs the pid instead.
    }
    return TRUE;
}
