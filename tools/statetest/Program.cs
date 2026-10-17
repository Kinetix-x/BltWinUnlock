using System.Globalization;
using Btwu.Core;

// ===========================================================================
//  statetest - proves that publishing presence.dat survives contention.
//
//  Why this exists. On 2026-10-14 21:26 a single UnauthorizedAccessException
//  out of PresenceChannel.Write killed the agent's heartbeat loop for the rest
//  of the session. The process stayed alive, presence.dat froze at 21:26, and
//  the only visible symptom - two hours later - was the unlock tile silently
//  disappearing. That is the worst possible failure shape: the feature is gone
//  and nothing looks wrong.
//
//  A test that can only ever pass would not have caught it, so this one runs the
//  same contention against the OLD publish path (--naive) and REQUIRES it to
//  break. If --naive ever passes, the test has stopped testing anything and the
//  real assertions below are worthless.
//
//  Usage:
//      dotnet run              current implementation - must survive, never throw
//      dotnet run -- --naive   pre-2026-10-14 path - must throw, proving the test bites
//
//  Nothing here touches the live state file: BTWU_DATA_DIR points at a scratch
//  directory, and only this tool honours it (the credential provider deliberately
//  does not - see Paths.cs).
// ===========================================================================

var scratch = Path.Combine(Path.GetTempPath(),
    "btwu-statetest-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

// Paths.DataDir is resolved on first use, so this must happen before anything touches it.
Environment.SetEnvironmentVariable("BTWU_DATA_DIR", scratch);

var naive = args.Contains("--naive");
var checks = 0;
var failed = 0;

void Check(bool ok, string what)
{
    checks++;
    if (!ok) failed++;
    Console.WriteLine((ok ? "  [ok]   " : "  [FAIL] ") + what);
}

string Body() => string.Create(CultureInfo.InvariantCulture,
    $"{PresenceSnapshot.Header} 1 {DateTime.UtcNow.Ticks} {DateTime.UtcNow.Ticks} 0 1 0\n");

bool CurrentWrite() => PresenceChannel.Write(new PresenceSnapshot
{
    Present = true,
    SinceTicks = DateTime.UtcNow.Ticks,
    HeartbeatTicks = DateTime.UtcNow.Ticks,
    SuppressUntilTicks = 0,
    AutoUnlock = true,
    UnlockWindowSeconds = 0,
});

// The pre-2026-10-14 path, reproduced exactly: one shared temp name for every writer, a single
// File.Move with no retry, and whatever it throws propagates to the caller.
void NaiveWrite()
{
    var tmp = Paths.PresenceFile + ".tmp";
    File.WriteAllText(tmp, Body());
    File.Move(tmp, Paths.PresenceFile, overwrite: true);
}

Console.WriteLine($"scratch : {scratch}");
Console.WriteLine($"mode    : {(naive ? "NAIVE (pre-2026-10-14 publish path)" : "current PresenceChannel.Write")}");
Console.WriteLine();

Directory.CreateDirectory(scratch);
Paths.EnsureDataDir();
if (!CurrentWrite())
{
    Console.WriteLine("cannot even establish the starting state - aborting.");
    return 2;
}

// ---------------------------------------------------------------------------
// Phase 1: several writers at once, plus a reader that denies delete.
//
// Both halves of the real incident: two agents can run at once (the installer
// starts the scheduled task while a tray instance is already up), and any reader
// that opens the file without FILE_SHARE_DELETE - a .NET ReadAllText, an indexer,
// an AV scan - makes MoveFileEx fail with ACCESS_DENIED.
// ---------------------------------------------------------------------------
Console.WriteLine("--- phase 1: 4 concurrent writers + a delete-denying reader ---");

using var stop = new CancellationTokenSource();
var writerExceptions = 0;
var writerFailures = 0;
var writerSuccesses = 0;
var readerOpens = 0;

void Writer()
{
    while (!stop.IsCancellationRequested)
    {
        try
        {
            if (naive)
            {
                NaiveWrite();
                Interlocked.Increment(ref writerSuccesses);
            }
            else if (CurrentWrite())
            {
                Interlocked.Increment(ref writerSuccesses);
            }
            else
            {
                Interlocked.Increment(ref writerFailures);
            }
        }
        catch
        {
            // Counted, not rethrown: in the naive mode this is the bug under test, and in the current
            // mode reaching here at all is a failure of the contract.
            Interlocked.Increment(ref writerExceptions);
        }
    }
}

void DenyingReader()
{
    while (!stop.IsCancellationRequested)
    {
        try
        {
            // FileShare.Read - exactly what File.ReadAllText takes. It denies both write and delete,
            // which is what turns the atomic replace into ACCESS_DENIED.
            using var stream = new FileStream(Paths.PresenceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            _ = stream.ReadByte();
            Interlocked.Increment(ref readerOpens);
        }
        catch { }
    }
}

var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(Writer)).ToArray();
var reader = Task.Run(DenyingReader);
Thread.Sleep(3000);
stop.Cancel();
try { Task.WaitAll(writers.Append(reader).ToArray(), TimeSpan.FromSeconds(5)); } catch { }

Console.WriteLine($"  writers: {writerSuccesses} published, {writerFailures} refused, {writerExceptions} threw");
Console.WriteLine($"  reader : {readerOpens} opens");
Console.WriteLine($"  state file still present after the storm: {File.Exists(Paths.PresenceFile)}");

if (naive)
{
    // The whole point of --naive: the old path must actually fall over, otherwise this harness is
    // incapable of detecting the bug it was written for.
    Check(writerExceptions > 0,
        $"the old path throws under contention ({writerExceptions} exceptions) - proving this test can fail");
}
else
{
    Check(writerExceptions == 0,
        $"no exception ever escapes Write ({writerExceptions}) - a throw here is what killed the heartbeat loop");
    Check(writerSuccesses > 0,
        $"the state still got published through the contention ({writerSuccesses} times)");
}

// ---------------------------------------------------------------------------
// Phase 2: a holder that refuses to let go, then lets go.
//
// Deterministic and single-threaded, because the assertion is about the writer's own behaviour: while
// the file is locked it must report failure rather than throw (the caller then retries and logs), and
// the moment the lock lifts it must succeed again - the feature has to come back on its own.
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("--- phase 2: unpublishable, then publishable again ---");

// A writer must never leave the destination missing - a missing file reads as "absent" and the unlock
// tile disappears, which is the very symptom being fixed. Asserted rather than assumed; the file is
// rebuilt only so the rest of the phase can run.
var survived = File.Exists(Paths.PresenceFile);
Check(survived, "the destination survived the storm (no writer unlinked it)");
if (!survived)
{
    var attempts = 0;
    while (!CurrentWrite() && attempts++ < 100) Thread.Sleep(10);
}

using (var hostage = new FileStream(Paths.PresenceFile, FileMode.Open, FileAccess.Read, FileShare.Read))
{
    var refused = naive ? Throws(NaiveWrite) : !CurrentWrite();

    if (naive) Check(refused, "the old path throws while the destination is held (that is the 21:26 failure)");
    else Check(refused, "with the destination locked, Write reports failure instead of throwing");
}

var recovered = CurrentWrite();
Check(recovered, "and the very next Write succeeds once the holder releases - it recovers by itself");

// ---------------------------------------------------------------------------
// Phase 3: no litter, and the file we leave behind is parseable.
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("--- phase 3: no orphaned temp files, state still valid ---");

var leftovers = Directory.GetFiles(scratch, "*.tmp");
Check(leftovers.Length == 0,
    $"no orphaned temp files left behind (found {leftovers.Length}: {string.Join(", ", leftovers.Select(Path.GetFileName))})");

var reads = PresenceChannel.TryRead(out var snapshot);
Check(reads && snapshot.Present, "the published state parses back to a valid snapshot");
Check(PresenceSnapshot.Header == (File.ReadAllText(Paths.PresenceFile).Split(' ')[0]),
    "the file still carries the BTWU1 header");

Console.WriteLine();
Console.WriteLine($"{checks - failed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

static bool Throws(Action action)
{
    try { action(); return false; }
    catch { return true; }
}
