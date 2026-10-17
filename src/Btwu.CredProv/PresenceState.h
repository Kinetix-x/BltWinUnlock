// PresenceState.h - the two policy questions the credential provider asks before it unlocks.
//
//   1. What does the agent say about the trigger device?  -> ReadPresence() / ShouldUnlock()
//   2. Is this lock screen actually a lock screen?         -> QuerySessionUser()
//
// (1) mirrors Btwu.Core.PresenceChannel. The agent writes one line into
//   %ProgramData%\BtwuUnlock\presence.dat
// formatted as
//   BTWU1 <present> <sinceTicks> <heartbeatTicks> <suppressUntilTicks> <autoUnlock> <unlockWindowSeconds>
// where the ticks are .NET DateTime ticks (100ns units since 0001-01-01 UTC).
//
// <present> answers "is a trigger device connected RIGHT NOW", with NO grace period. The agent's
// GraceSeconds debounce must never reach this bit: it exists to stop a napping BLE keyboard from
// *locking* the workstation, and the agent applies it to the lock decision on its own. Here the bit
// is a precondition for handing a stored password to LSA, so a debounced value would keep unlocking
// the machine with no keyboard in the room. (Measured 2026-10-14: 45 s of exactly that.)
//
// (2) exists because Windows 10 merged the logon and unlock screens: LogonUI no longer raises
// CPUS_UNLOCK_WORKSTATION, it raises CPUS_LOGON for both. The only supported way to tell them apart
// is to ask whether somebody is signed in to this session.
//
// This header is shared verbatim with test/harness.cpp, so the harness asserts against the exact
// logic the DLL runs instead of a copy of it.
//
// Everything in this header is header-only, allocation-light and exception free on purpose: it runs
// inside LogonUI, where an unhandled failure means the user cannot get back into the machine.
// A missing, stale or unparsable file always means "not present" - the provider fails closed.

#pragma once

#include <windows.h>
#include <wtsapi32.h>
#include <sddl.h>
#include <cstdio>
#include <cstddef>
#include <string>

#pragma comment(lib, "wtsapi32.lib")

namespace Btwu
{
    // FILETIME counts from 1601-01-01, .NET DateTime ticks from 0001-01-01.
    constexpr long long kFileTimeToDotNetTicks = 504911232000000000LL;
    constexpr long long kTicksPerSecond = 10000000LL;

    // The agent re-stamps this on a keepalive while the trigger device is present, and immediately on
    // every real transition. Anything older than this means the agent is gone or hung, and the
    // presence claim can no longer be trusted. Keep it a comfortable multiple of the agent's
    // HeartbeatMilliseconds (5000 by default) - three missed beats is the intended margin.
    constexpr long long kMaxAgeTicks = 15LL * kTicksPerSecond;

    struct Presence
    {
        // A trigger device is connected right now - see the file-format note at the top of this file.
        bool Present = false;
        long long SinceTicks = 0;
        long long HeartbeatTicks = 0;
        long long SuppressUntilTicks = 0;
        bool AutoUnlock = true;
        int UnlockWindowSeconds = 0;
        bool Valid = false;
    };

    inline long long NowDotNetTicks()
    {
        FILETIME fileTime{};
        GetSystemTimeAsFileTime(&fileTime);
        ULARGE_INTEGER value;
        value.LowPart = fileTime.dwLowDateTime;
        value.HighPart = fileTime.dwHighDateTime;
        return static_cast<long long>(value.QuadPart) + kFileTimeToDotNetTicks;
    }

    inline std::wstring DataDirectory()
    {
        wchar_t buffer[MAX_PATH]{};
        const DWORD length = GetEnvironmentVariableW(L"ProgramData", buffer, MAX_PATH);
        std::wstring path = (length > 0 && length < MAX_PATH) ? buffer : L"C:\\ProgramData";
        path += L"\\BtwuUnlock";
        return path;
    }

    inline std::wstring PresenceFilePath() { return DataDirectory() + L"\\presence.dat"; }
    inline std::wstring CredentialFilePath() { return DataDirectory() + L"\\cred.bin"; }
    inline std::wstring LogFilePath() { return DataDirectory() + L"\\credprov.log"; }
    inline std::wstring PauseFilePath() { return DataDirectory() + L"\\pause.until"; }
    inline std::wstring UnlockCooldownFilePath() { return DataDirectory() + L"\\unlocked.until"; }
    inline std::wstring DisableFilePath() { return DataDirectory() + L"\\disable.cp"; }
    inline std::wstring AutoSubmitFilePath() { return DataDirectory() + L"\\autosubmit"; }

    // ------------------------------------------------------------------------------ kill switches

    // Two independent stops, both read straight off the disk by the credential provider so that
    // neither depends on the agent still being alive.
    //
    //   pause.until - a .NET-tick deadline. The agent writes it for `btwu pause`, and this DLL writes
    //                 it from ReportResult after a failed unlock. Honouring it *here* is what makes a
    //                 wrong stored password unable to turn into an attempt loop: the agent translates
    //                 it into SuppressUntilTicks, but if the agent has died - and it is exactly the
    //                 process a user kills when the lock screen misbehaves - that translation never
    //                 happens and the loop would continue. Read it directly instead.
    //
    //   disable.cp  - a zero-byte file whose only purpose is to be creatable from a normal shell by
    //                 someone who wants this provider to disappear now, without uninstalling it.
    //                 Checked once per enumeration, so it takes effect on the next lock/unlock.

    inline bool ReadDeadlineTicks(const std::wstring& path, long long& deadline)
    {
        deadline = 0;
        const HANDLE file = CreateFileW(path.c_str(), GENERIC_READ,
                                        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return false;

        char buffer[32]{};
        DWORD read = 0;
        const BOOL ok = ReadFile(file, buffer, sizeof(buffer) - 1, &read, nullptr);
        CloseHandle(file);
        if (!ok || read == 0) return false;

        deadline = _strtoi64(buffer, nullptr, 10);
        return true;
    }

    // True when a pause is in force right now. A missing or unparsable file is not a pause.
    inline bool IsPaused()
    {
        long long deadline = 0;
        return ReadDeadlineTicks(PauseFilePath(), deadline) && NowDotNetTicks() < deadline;
    }

    // True when the operator dropped the disable.cp kill switch.
    inline bool IsDisabled()
    {
        return GetFileAttributesW(DisableFilePath().c_str()) != INVALID_FILE_ATTRIBUTES;
    }

    // True when the operator asked for a *fully* automatic unlock (no keystroke at all).
    //
    // A marker file rather than a config.json key on purpose: config.json is rewritten wholesale by
    // `btwu enroll`, so a key living there would silently disappear the next time somebody re-enrolled
    // - and this is a safety-relevant switch. It also keeps a JSON parser out of LogonUI's enumeration
    // path. The default (file absent) is the conservative mode described below.
    //
    // Conservative mode (default):
    //   the credential is offered as the *default tile* (pdwDefault = 0) but NOT as an auto-logon
    //   (pbAutoLogonWithDefault = FALSE). The lock screen renders exactly as it always does - user
    //   tile, PIN, password box, fingerprint - and the user presses Enter (or clicks 解锁) to have the
    //   stored password submitted. Nothing about our provider is announced to LogonUI before the user
    //   acts, so a credential that could not be completed can never leave the screen without UI.
    //
    // Automatic mode (file present):
    //   pbAutoLogonWithDefault = TRUE as well, so LogonUI submits the moment it enumerates. This is
    //   what makes a manual Win+L bounce straight back to the desktop. Safe only because the provider
    //   validates and *pre-builds* the whole submission before it ever reports a credential.
    inline bool AutoSubmitUnlock()
    {
        return GetFileAttributesW(AutoSubmitFilePath().c_str()) != INVALID_FILE_ATTRIBUTES;
    }

    // ------------------------------------------------------------------- the post-unlock dwell time

    // A brief armistice on the lock side, written by this provider the moment it completes an unlock
    // and honoured by the agent.
    //
    // WHY IT IS NEEDED. An unlock proves the keyboard is right here - but a BLE keyboard drops its
    // link whenever it idles, and "the keyboard went to sleep" is byte-for-byte the same signal as
    // "the user walked away with it". Without a dwell time the agent locks the workstation it was just
    // asked to unlock: unlock at T, the keyboard naps at T+10s, GraceSeconds elapses, and the screen
    // locks again with the user sitting in front of it. Locking is the right answer only when the
    // device is genuinely absent, and a device that just unlocked the machine is not absent - it is
    // asleep two feet away.
    //
    // So the absence signal is ignored for this long after an unlock, and the decision is taken
    // normally from then on. In other words: the keyboard is checked for presence at each session
    // boundary, not on every transient radio event in between.
    //
    // 90 s covers a reconnect cycle (an idle keyboard typically reconnects within a second of a
    // keypress, but the user may well read for a while first) with room to spare; it is short enough
    // that actually walking off with the keyboard still locks, just a little later.
    constexpr long long kUnlockCooldownSeconds = 90;

    // Publishes "do not lock on absence until <deadline>". Called on a successful unlock.
    inline void WriteUnlockCooldown()
    {
        const long long until = NowDotNetTicks() + kUnlockCooldownSeconds * kTicksPerSecond;

        const HANDLE file = CreateFileW(UnlockCooldownFilePath().c_str(), GENERIC_WRITE,
                                        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                                        CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return;

        char buffer[32]{};
        const int length = sprintf_s(buffer, "%lld", until);
        DWORD written = 0;
        WriteFile(file, buffer, static_cast<DWORD>(length), &written, nullptr);
        CloseHandle(file);
    }

    // True while a post-unlock dwell is in force. A missing or unparsable file is not one.
    inline bool IsWithinUnlockCooldown()
    {
        long long deadline = 0;
        return ReadDeadlineTicks(UnlockCooldownFilePath(), deadline) && NowDotNetTicks() < deadline;
    }

    inline Presence ReadPresence()
    {
        Presence presence;

        const HANDLE file = CreateFileW(PresenceFilePath().c_str(), GENERIC_READ,
                                        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return presence;

        char buffer[256]{};
        DWORD read = 0;
        const BOOL ok = ReadFile(file, buffer, sizeof(buffer) - 1, &read, nullptr);
        CloseHandle(file);
        if (!ok || read == 0) return presence;

        int present = 0, autoUnlock = 1, unlockWindow = 0;
        long long since = 0, heartbeat = 0, suppressUntil = 0;
        const int parsed = sscanf_s(buffer, "BTWU1 %d %lld %lld %lld %d %d",
                                    &present, &since, &heartbeat, &suppressUntil, &autoUnlock, &unlockWindow);
        if (parsed < 4) return presence;

        presence.Present = present == 1;
        presence.SinceTicks = since;
        presence.HeartbeatTicks = heartbeat;
        presence.SuppressUntilTicks = suppressUntil;
        presence.AutoUnlock = autoUnlock != 0;
        presence.UnlockWindowSeconds = unlockWindow;
        presence.Valid = true;
        return presence;
    }

    // The single decision the whole product rests on.
    inline bool ShouldUnlock(const Presence& presence)
    {
        if (!presence.Valid || !presence.Present || !presence.AutoUnlock) return false;

        const long long now = NowDotNetTicks();
        if (now - presence.HeartbeatTicks > kMaxAgeTicks) return false;   // agent gone or hung
        if (now < presence.SuppressUntilTicks) return false;             // paused, or a previous attempt failed
        if (IsPaused()) return false;                                    // same pause, read directly from disk
        if (presence.UnlockWindowSeconds > 0 &&
            now - presence.SinceTicks > static_cast<long long>(presence.UnlockWindowSeconds) * kTicksPerSecond)
            return false;                                                // present too long: user is not "coming back"

        return true;
    }

    // ------------------------------------------------------------------ lock screen vs logon screen

    // Buffer sizes for QuerySessionUser. Account and domain names are capped far below these values
    // (SAM limits an account name to 20 characters), so one call never truncates in practice.
    constexpr int kMaxSessionNameChars = 128;

    // Answers "is somebody signed in to the session this process is running in?".
    //
    // WHY THIS IS THE WHOLE DISCRIMINATOR. Since Windows 10 the CPUS_LOGON and CPUS_UNLOCK_WORKSTATION
    // usage scenarios have been combined, so LogonUI hands the lock screen to every credential
    // provider as CPUS_LOGON (1). A provider that only accepts CPUS_UNLOCK_WORKSTATION (2) therefore
    // never runs at all on Windows 10/11 - which is exactly what credprov.log showed:
    //
    //     LogonUI.exe pid=41356 SetUsageScenario(1)
    //
    // The documented way to recover the distinction is this query: at a lock screen the session
    // already has a user, at a real logon screen (fresh boot, after a sign-out, after a fast user
    // switch) it does not. Microsoft's own guidance is to then "proceed as if you were in a
    // CPUS_UNLOCK_WORKSTATION usage scenario", which is what SetUsageScenario does.
    //
    // The two output buffers are optional and exist for the log line - and for the harness, which
    // computes its expectation from this very function. They are filled with an empty string whenever
    // the answer is false, so a caller that logs a refused scenario still prints the session user and
    // can see WHY it was refused. Returns true when a user name came back.
    //
    // No allocation and no exceptions: fixed caller buffers plus WTSFreeMemory on the API's own.
    inline bool QuerySessionUser(wchar_t* userName = nullptr, int userNameChars = 0,
                                 wchar_t* domainName = nullptr, int domainNameChars = 0)
    {
        if (userName != nullptr && userNameChars > 0) userName[0] = L'\0';
        if (domainName != nullptr && domainNameChars > 0) domainName[0] = L'\0';

        // WTSQuerySessionInformationW has no way to pre-size the buffer, so it allocates one and
        // hands ownership to the caller. Every buffer it returns here is freed with WTSFreeMemory.
        const auto ask = [](WTS_INFO_CLASS kind) -> LPWSTR
        {
            LPWSTR buffer = nullptr;
            DWORD bytes = 0;
            if (!WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, WTS_CURRENT_SESSION,
                                             kind, &buffer, &bytes) || buffer == nullptr)
                return nullptr;
            return buffer;
        };

        LPWSTR sessionUser = ask(WTSUserName);
        if (sessionUser == nullptr) return false;

        // Trust the terminating NUL rather than the returned byte count: the count for an empty name
        // has been observed as both 0 and sizeof(wchar_t) depending on the Windows version, so a size
        // test is not portable. A string class is always NUL-terminated, so buffer[0] is decisive.
        const bool hasUser = sessionUser[0] != L'\0';
        if (hasUser && userName != nullptr && userNameChars > 0)
            lstrcpynW(userName, sessionUser, userNameChars);
        WTSFreeMemory(sessionUser);
        if (!hasUser) return false;

        if (domainName != nullptr && domainNameChars > 0)
        {
            LPWSTR sessionDomain = ask(WTSDomainName);
            if (sessionDomain != nullptr)
            {
                if (sessionDomain[0] != L'\0') lstrcpynW(domainName, sessionDomain, domainNameChars);
                WTSFreeMemory(sessionDomain);
            }
        }

        return true;
    }

    // ------------------------------------------------------------------ the identity of that user

    // The SID of the account signed in to this session, as a string ("S-1-5-21-...").
    //
    // WHY THE PROVIDER NEEDS THIS. ICredentialProviderCredential2::GetUserSid is documented as "The
    // Logon UI will use the returned SID from this method to associate the credential tile with a user
    // tile". A SID that matches no user attached to this session therefore cannot be placed anywhere.
    // That is not a cosmetic problem: on 2026-10-14 the provider returned a Microsoft-account *identity*
    // SID (S-1-11-96-...) instead of the local account SID (S-1-5-21-...), and the result was a lock
    // screen with no user tile, no password box and no PIN - only Windows Hello still worked.
    //
    // So the provider now refuses to offer anything unless the SID it holds is provably the SID of the
    // user sitting at this lock screen. That turns "the lock screen might break" into "we stay
    // invisible", which is the only acceptable failure direction for a credential provider.
    //
    // Deliberately resolution-based rather than a string prefix test: the prefix only says the SID is
    // *shaped* like an account SID, while LookupAccountNameW says whether an account with this name
    // actually exists in this machine's account database right now.
    inline bool SessionUserSid(std::wstring& sid)
    {
        sid.clear();

        wchar_t user[kMaxSessionNameChars]{};
        if (!QuerySessionUser(user, kMaxSessionNameChars)) return false;

        BYTE bytes[SECURITY_MAX_SID_SIZE]{};
        DWORD byteCount = sizeof(bytes);
        wchar_t domain[256]{};
        DWORD domainChars = 256;
        SID_NAME_USE use{};
        if (!LookupAccountNameW(nullptr, user, bytes, &byteCount, domain, &domainChars, &use)) return false;

        LPWSTR text = nullptr;
        if (!ConvertSidToStringSidW(bytes, &text) || text == nullptr) return false;
        sid.assign(text);
        LocalFree(text);
        return !sid.empty();
    }
}
