using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using ClaudeTaskBar;

namespace ClaudeTaskBar.CoreTests;

public static class Program
{
    private static int _passed, _failed;
    private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.FromHours(9));

    private static void Check(bool cond, string name)
    {
        if (cond) { _passed++; Console.WriteLine("PASS " + name); }
        else { _failed++; Console.WriteLine("FAIL " + name); }
    }

    private static NotifyEvent Ev(NotifyEventKind kind, string id, string source = "", string surface = "terminal") =>
        new NotifyEvent { Kind = kind, SessionId = id, Source = source.Length > 0 ? source : id, Surface = surface };

    public static async Task<int> Main()
    {
        RegistryBasics();
        TwoDoneSessionsBothSurvive();
        OverflowPriority();
        AcknowledgeAndDismiss();
        DismissedPetComesBack();
        JsonParsing();
        LegacyQueryParsing();
        ConfigLoading();
        RequestParsing();
        OrcaHandleShape();
        OrcaEventParsing();
        OrcaLedger();
        OrcaCliResolve();
        OrcaStartInfo();
        await OrcaRun();
        PetSheetReal();
        PetSheetParseErrors();
        PetFrameAt();
        PetStageBehaviour();
        ConfigPetScale();
        VisitKeepsThePet();
        VisitThenStage();
        PidParsing();
        PidInLedger();
        LivenessProcess();
        LivenessWindow();
        LiveParse();
        LiveIsInteractive();
        LiveSameProcess();
        ReconcileBasics();
        ReconcileOthers();
        ReconcileThenStage();
        SetHwndOnlyWhenEmpty();
        LeftSessionsStayAway();
        JumpChooseWindow();
        JumpPickTab();
        SetConsoleHwndOverwrites();
        TsParsing();
        MapStatusValues();
        StatusUpdatedAtParsing();
        ApplyWithTimes();
        VisitSetsStatusAt();
        ReconcileNewestWins();
        ReconcileNewSessions();
        ReconcileThenStageWalks();
        PetSheetOptionalReal();
        PetSheetOptionalParse();
        LeftPictures();
        UsageReading();
        ConfigFlee();
        FleeBehaviour();
        StartleBehaviour();
        MutterBehaviour();
        SpawnBehaviour();
        VanishBehaviour();
        await ServerRoundTrip();
        Console.WriteLine($"---\n{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void RegistryBasics()
    {
        var r = new SessionRegistry();
        Check(r.Apply(Ev(NotifyEventKind.Started, "s1"), T0), "started registers a session");
        Check(r.Count == 1, "one session after start");
        Check(r.Snapshot()[0].Status == SessionStatus.Running, "started session is Running");

        // A second started event for the same id updates, never duplicates.
        r.Apply(Ev(NotifyEventKind.Running, "s1"), T0.AddMinutes(1));
        Check(r.Count == 1, "same session id does not duplicate the pet");

        r.Apply(Ev(NotifyEventKind.Done, "s1"), T0.AddMinutes(2));
        Check(r.Snapshot()[0].Status == SessionStatus.DoneUnseen, "done -> DoneUnseen");

        // A new prompt after done means the session is working again.
        r.Apply(Ev(NotifyEventKind.Running, "s1"), T0.AddMinutes(3));
        Check(r.Snapshot()[0].Status == SessionStatus.Running, "prompt after done -> Running again");

        r.Apply(Ev(NotifyEventKind.Ended, "s1"), T0.AddMinutes(4));
        Check(r.Count == 0, "ended removes the session");

        Check(!r.Apply(new NotifyEvent { Kind = NotifyEventKind.Unknown, SessionId = "x" }, T0),
            "unknown events are ignored");
    }

    private static void TwoDoneSessionsBothSurvive()
    {
        // Regression for the old design: _pendingHwnd held exactly one
        // notification, so a second completion erased the first.
        var r = new SessionRegistry();
        r.Apply(Ev(NotifyEventKind.Done, "a"), T0);
        r.Apply(Ev(NotifyEventKind.Done, "b"), T0.AddSeconds(5));
        Check(r.Count == 2, "two done sessions are both kept");
        var snap = r.Snapshot();
        Check(snap[0].Id == "a" && snap[1].Id == "b", "earlier done session stays first");
    }

    private static void OverflowPriority()
    {
        // Confirmed order (user, 2026-10-29): Asking -> DoneUnseen -> Running.
        var r = new SessionRegistry();
        for (int i = 1; i <= 4; i++) r.Apply(Ev(NotifyEventKind.Running, "run" + i), T0.AddMinutes(i));
        r.Apply(Ev(NotifyEventKind.Done, "done1"), T0.AddMinutes(10));
        r.Apply(Ev(NotifyEventKind.Ask, "ask1"), T0.AddMinutes(11));
        r.Apply(Ev(NotifyEventKind.Running, "run5"), T0.AddMinutes(12));

        var (displayed, overflow) = PetSelection.Select(r.Snapshot());
        Check(displayed.Count == 5, "bar shows at most 5 pets");
        Check(overflow.Count == 2, "the rest overflow (kept in the ledger)");
        Check(displayed[0].Id == "ask1", "asking pet is first");
        Check(displayed[1].Id == "done1", "done-unseen pet is second");
        Check(displayed[2].Id == "run1", "running pets follow, oldest first");

        // Acknowledging the done pet frees its priority: order reshuffles.
        r.Acknowledge("done1");
        var (d2, _) = PetSelection.Select(r.Snapshot());
        Check(d2[1].Id == "run1", "acknowledged done drops behind running");
    }

    private static void AcknowledgeAndDismiss()
    {
        var r = new SessionRegistry();
        r.Apply(Ev(NotifyEventKind.Done, "d"), T0);
        r.Acknowledge("d");
        Check(r.Snapshot()[0].Status == SessionStatus.DoneSeen, "acknowledge marks DoneSeen");
        Check(r.Count == 1, "acknowledged pet stays in the bar");

        r.Apply(Ev(NotifyEventKind.Ask, "q"), T0.AddMinutes(1));
        r.Acknowledge("q");
        Check(r.Snapshot()[0].Status == SessionStatus.Asking, "acknowledge does not clear Asking");

        r.Dismiss("q");
        Check(r.Count == 1, "dismiss removes only that pet");
        r.Clear();
        Check(r.Count == 0, "clear empties the ledger");
    }

    private static void DismissedPetComesBack()
    {
        // Clicking a pet jumps and then dismisses it (owner, 2026-11-04).
        // The session is still alive, so its next event must bring the pet back.
        var r = new SessionRegistry();
        r.Apply(new NotifyEvent { Kind = NotifyEventKind.Running, SessionId = "j", Source = "proj", OrcaTerminal = "term_abc123" }, T0);
        r.Dismiss("j");
        Check(r.Count == 0, "dismissed pet leaves the bar");

        r.Apply(new NotifyEvent { Kind = NotifyEventKind.Done, SessionId = "j", Source = "proj", OrcaTerminal = "term_abc123" }, T0.AddMinutes(1));
        Check(r.Count == 1, "next event (Stop) after dismiss brings the pet back");
        var back = r.Snapshot()[0];
        Check(back.Id == "j" && back.Status == SessionStatus.DoneUnseen, "returned pet shows as done (unseen)");
        Check(back.OrcaTerminal == "term_abc123", "returned pet can jump to its Orca tab again");
    }

    private static void JsonParsing()
    {
        var ev = NotifyEvent.FromJson(
            "{\"session_id\":\"abc-123\",\"hook_event_name\":\"Stop\",\"cwd\":\"C:\\\\work\\\\my-proj\",\"hwnd\":12345,\"surface\":\"ide\"}");
        Check(ev.Kind == NotifyEventKind.Done, "hook_event_name Stop maps to Done");
        Check(ev.SessionId == "abc-123", "session_id is kept (the old hook discarded it)");
        Check(ev.Source == "my-proj", "source falls back to the cwd leaf name");
        Check(ev.ProjectPath == "C:\\work\\my-proj", "full project path is kept");
        Check(ev.Hwnd == "12345", "numeric hwnd is read as text");
        Check(ev.EffectiveId == "abc-123", "effective id prefers session_id");

        var ev2 = NotifyEvent.FromJson("{\"type\":\"ask\",\"session_id\":\"s2\"}");
        Check(ev2.Kind == NotifyEventKind.Ask, "explicit type ask parses");

        var ev3 = NotifyEvent.FromJson("{\"hook_event_name\":\"SessionEnd\",\"session_id\":\"s3\"}");
        Check(ev3.Kind == NotifyEventKind.Ended, "SessionEnd maps to Ended");

        Check(NotifyEvent.FromJson("not json").Kind == NotifyEventKind.Unknown,
            "broken JSON becomes Unknown, not a crash");
        Check(NotifyEvent.FromJson("{}").EffectiveId.StartsWith("source:"),
            "id-less events still get a stable fallback id");
    }

    private static void LegacyQueryParsing()
    {
        var ev = NotifyEvent.FromQuery("/done?source=proj&hwnd=99&type=ask&surface=web");
        Check(ev.Kind == NotifyEventKind.Ask, "legacy GET type=ask parses");
        Check(ev.Source == "proj" && ev.Hwnd == "99" && ev.Surface == "web", "legacy GET fields parse");
        Check(ev.EffectiveId == "source:proj|web", "legacy events key by source+surface");

        var ev2 = NotifyEvent.FromQuery("/done?session_id=zz&cwd=C%3A%5Cwork%5Cother&type=done");
        Check(ev2.SessionId == "zz", "legacy GET can carry session_id");
        Check(ev2.Source == "other", "legacy GET source falls back to cwd leaf");
    }

    private static void ConfigLoading()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ctb-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            Check(AppConfig.Load(path).Port == 8765, "missing config -> default port 8765");
            File.WriteAllText(path, "{\"port\": 9999}");
            Check(AppConfig.Load(path).Port == 9999, "config port is honored");
            File.WriteAllText(path, "{\"port\": 70000}");
            Check(AppConfig.Load(path).Port == 8765, "out-of-range port falls back to default");
            File.WriteAllText(path, "broken");
            Check(AppConfig.Load(path).Port == 8765, "broken config falls back to default");
            Check(AppConfig.Load(path).OrcaPath == "", "broken config -> empty orcaPath");

            File.WriteAllText(path, "{\"port\": 9999, \"orcaPath\": \"D:\\\\Tools\\\\orca\\\\orca.exe\"}");
            var withOrca = AppConfig.Load(path);
            Check(withOrca.Port == 9999 && withOrca.OrcaPath == @"D:\Tools\orca\orca.exe", "config orcaPath is read");
            File.WriteAllText(path, "{\"port\": 9999}");
            Check(AppConfig.Load(path).OrcaPath == "", "missing orcaPath -> empty");
            File.WriteAllText(path, "{\"orcaPath\": 5}");
            Check(AppConfig.Load(path).OrcaPath == "", "non-string orcaPath -> empty");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void RequestParsing()
    {
        var post = NotifyServer.ParseRequest(
            "POST /event HTTP/1.1\r\nContent-Type: application/json\r\nContent-Length: 80\r\n\r\n" +
            "{\"session_id\":\"net-1\",\"type\":\"done\",\"source\":\"proj\",\"surface\":\"terminal\"}");
        Check(post != null && post.SessionId == "net-1" && post.Kind == NotifyEventKind.Done,
            "raw POST /event request parses to an event");

        var get = NotifyServer.ParseRequest("GET /done?source=old&hwnd=7&type=done HTTP/1.1\r\n\r\n");
        Check(get != null && get.Source == "old" && get.Kind == NotifyEventKind.Done,
            "raw legacy GET request parses to an event");

        Check(NotifyServer.ParseRequest("") == null, "empty request parses to null");
        Check(NotifyServer.ParseRequest("garbage") == null, "garbage request parses to null");
    }

    // The shape Orca actually hands out (seen in ORCA_TERMINAL_HANDLE on this PC).
    private const string OrcaHandle = "term_5a8219cf-2f01-4945-8087-028420cf3c73";

    private static void OrcaHandleShape()
    {
        Check(OrcaJump.IsValidHandle(OrcaHandle), "orca handle: real Orca handle passes");
        Check(OrcaJump.IsValidHandle("term_abc123"), "orca handle: short handle passes");
        Check(OrcaJump.IsValidHandle("term_" + new string('a', 128)), "orca handle: 128-char body passes");

        Check(!OrcaJump.IsValidHandle(""), "orca handle: empty is rejected");
        Check(!OrcaJump.IsValidHandle(null), "orca handle: null is rejected");
        Check(!OrcaJump.IsValidHandle("term_"), "orca handle: bare prefix is rejected");
        Check(!OrcaJump.IsValidHandle("term_ab c"), "orca handle: space is rejected");
        Check(!OrcaJump.IsValidHandle("term_ab;calc"), "orca handle: ';' is rejected");
        Check(!OrcaJump.IsValidHandle("term_ab\"x"), "orca handle: '\"' is rejected");
        Check(!OrcaJump.IsValidHandle("term_ab&x"), "orca handle: '&' is rejected");
        Check(!OrcaJump.IsValidHandle("term_" + new string('a', 129)), "orca handle: 129-char body is rejected");
        Check(!OrcaJump.IsValidHandle("term_abc\n"), "orca handle: trailing newline is rejected");
        Check(!OrcaJump.IsValidHandle("TERM_abc"), "orca handle: prefix is case-sensitive");
        Check(!OrcaJump.IsValidHandle("--json"), "orca handle: an option-looking value is rejected");
    }

    private static void OrcaEventParsing()
    {
        var ok = NotifyEvent.FromJson(
            "{\"session_id\":\"o1\",\"type\":\"done\",\"orca_terminal\":\"" + OrcaHandle + "\"}");
        Check(ok.OrcaTerminal == OrcaHandle, "POST: valid orca_terminal is kept");

        var bad = NotifyEvent.FromJson("{\"session_id\":\"o1\",\"type\":\"done\",\"orca_terminal\":\"term_x;calc\"}");
        Check(bad.OrcaTerminal == "" && bad.SessionId == "o1" && bad.Kind == NotifyEventKind.Done,
            "POST: malformed orca_terminal is dropped, the rest of the event is kept");
        Check(NotifyEvent.FromJson("{\"session_id\":\"o1\",\"type\":\"done\",\"orca_terminal\":123}").OrcaTerminal == "",
            "POST: non-string orca_terminal is dropped");
        Check(NotifyEvent.FromJson("{\"session_id\":\"o1\",\"type\":\"done\"}").OrcaTerminal == "",
            "POST: missing orca_terminal is empty");

        var get = NotifyEvent.FromQuery("/done?session_id=o2&type=done&orca_terminal=" + Uri.EscapeDataString(OrcaHandle));
        Check(get.OrcaTerminal == OrcaHandle, "GET: valid orca_terminal is kept");
        var getBad = NotifyEvent.FromQuery("/done?session_id=o2&type=done&orca_terminal=" + Uri.EscapeDataString("term_a\" & calc"));
        Check(getBad.OrcaTerminal == "" && getBad.SessionId == "o2", "GET: malformed orca_terminal is dropped");
        Check(NotifyEvent.FromQuery("/done?session_id=o2&type=done&orca_terminal=").OrcaTerminal == "",
            "GET: empty orca_terminal stays empty");
    }

    private static void OrcaLedger()
    {
        var r = new SessionRegistry();
        r.Apply(new NotifyEvent { Kind = NotifyEventKind.Started, SessionId = "o", OrcaTerminal = "term_first" }, T0);
        r.Apply(new NotifyEvent { Kind = NotifyEventKind.Running, SessionId = "o" }, T0.AddMinutes(1));
        Check(r.Snapshot()[0].OrcaTerminal == "term_first",
            "ledger: an event without orca_terminal keeps the earlier handle");
        r.Apply(new NotifyEvent { Kind = NotifyEventKind.Done, SessionId = "o", OrcaTerminal = "term_second" }, T0.AddMinutes(2));
        Check(r.Snapshot()[0].OrcaTerminal == "term_second", "ledger: a new handle overwrites the old one");
        Check(r.Count == 1, "ledger: handle changes do not duplicate the pet");
    }

    private static void OrcaCliResolve()
    {
        const string localAppData = @"C:\Users\u\AppData\Local";
        const string def = @"C:\Users\u\AppData\Local\Programs\orca\resources\bin\orca.exe";
        const string configured = @"D:\Tools\orca\orca.exe";
        const string onPath = @"E:\bin\orca.exe";
        const string pathEnv = @"C:\Windows;E:\bin";
        Func<string, bool> Only(params string[] files) =>
            p => Array.Exists(files, f => string.Equals(f, p, StringComparison.OrdinalIgnoreCase));

        Check(OrcaJump.ResolveCli(configured, Only(configured, def, onPath), localAppData, pathEnv) == configured,
            "cli: config orcaPath comes first");
        Check(OrcaJump.ResolveCli(@"D:\Tools\orca\orca.cmd", Only(@"D:\Tools\orca\orca.cmd", def), localAppData, pathEnv) == def,
            "cli: config orcaPath not ending in .exe is skipped");
        Check(OrcaJump.ResolveCli("orca.exe", Only("orca.exe", def), localAppData, pathEnv) == def,
            "cli: relative config orcaPath is skipped");
        Check(OrcaJump.ResolveCli(configured, Only(def, onPath), localAppData, pathEnv) == def,
            "cli: missing config file falls back to the default install");
        Check(OrcaJump.ResolveCli("", Only(def, onPath), localAppData, pathEnv) == def,
            "cli: default install comes before PATH");
        Check(OrcaJump.ResolveCli(null, Only(onPath), localAppData, pathEnv) == onPath,
            "cli: orca.exe on PATH is used last");
        Check(OrcaJump.ResolveCli(null, Only(onPath), localAppData, "\"E:\\bin\";;") == onPath,
            "cli: quoted and empty PATH entries are handled");
        Check(OrcaJump.ResolveCli(null, Only(@"E:\bin\orca.cmd"), localAppData, pathEnv) == null,
            "cli: only orca.cmd on PATH -> null (cmd shims are never used)");
        Check(OrcaJump.ResolveCli(null, Only(), localAppData, null) == null, "cli: nothing found -> null");
    }

    private static void OrcaStartInfo()
    {
        var psi = OrcaJump.BuildStartInfo(@"C:\x\orca.exe", OrcaHandle);
        Check(psi != null && psi.FileName == @"C:\x\orca.exe", "start info: runs the resolved orca.exe");
        var args = psi == null ? new List<string>() : new List<string>(psi.ArgumentList);
        Check(string.Join("|", args) == "terminal|switch|--terminal|" + OrcaHandle + "|--json",
            "start info: arguments are terminal switch --terminal <handle> --json");
        Check(psi != null && !psi.UseShellExecute && psi.CreateNoWindow,
            "start info: no shell, no window");
        Check(psi != null && psi.Arguments == "", "start info: no spliced argument string");
        Check(OrcaJump.BuildStartInfo(@"C:\x\orca.exe", "term_a b") == null, "start info: malformed handle -> null");
        Check(OrcaJump.BuildStartInfo("", OrcaHandle) == null, "start info: empty cli -> null");
    }

    private static async Task OrcaRun()
    {
        // The process runner, exercised with cmd.exe (never the real orca.exe,
        // which would switch the owner's tabs while the tests run).
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (!File.Exists(cmd)) { Console.WriteLine("SKIP process runner (no cmd.exe)"); return; }
        ProcessStartInfo Cmd(string line)
        {
            var p = new ProcessStartInfo(cmd)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            p.ArgumentList.Add("/c");
            p.ArgumentList.Add(line);
            return p;
        }

        var logs = new List<string>();
        Check(await OrcaJump.RunAsync(Cmd("exit 0"), TimeSpan.FromSeconds(5), logs.Add) && logs.Count == 0,
            "run: exit code 0 is success, nothing logged");
        Check(!await OrcaJump.RunAsync(Cmd("exit 3"), TimeSpan.FromSeconds(5), logs.Add),
            "run: non-zero exit code is failure");
        Check(logs.Count == 1 && logs[0].Contains("exit code 3"), "run: failure writes one log line");

        var sw = Stopwatch.StartNew();
        var finished = await OrcaJump.RunAsync(Cmd("ping -n 30 127.0.0.1 >nul"), TimeSpan.FromMilliseconds(500), logs.Add);
        sw.Stop();
        Check(!finished && sw.Elapsed < TimeSpan.FromSeconds(10), "run: timeout stops the process and fails");
        Check(logs.Count == 2 && logs[1].Contains("timed out"), "run: timeout writes one log line");

        var missing = new ProcessStartInfo(@"C:\no-such-dir\orca.exe") { UseShellExecute = false, CreateNoWindow = true };
        Check(!await OrcaJump.RunAsync(missing, TimeSpan.FromSeconds(5), logs.Add) && logs.Count == 3,
            "run: a missing exe is a logged failure, not a crash");

        Check(!await OrcaJump.TrySwitchAsync(null, "not a handle", logs.Add) && logs.Count == 4,
            "switch: a malformed handle is refused before any process starts");
    }

    private static string? FindPetDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var p = Path.Combine(dir.FullName, "app", "assets", "pet");
            if (File.Exists(Path.Combine(p, "pet.json"))) return p;
            dir = dir.Parent;
        }
        return null;
    }

    private static void PetSheetReal()
    {
        var petDir = FindPetDir();
        Check(petDir != null, "pet: app/assets/pet/pet.json is found from the test folder");
        if (petDir == null) return;

        PetSheet? sheet = null;
        try { sheet = PetSheet.Load(Path.Combine(petDir, "pet.json")); }
        catch (Exception ex) { Console.WriteLine("  " + ex.Message); }
        Check(sheet != null, "pet: the real pet.json loads");
        if (sheet == null) return;

        Check(sheet.Frame == 32 && sheet.Ground == 30, "pet: frame is 32 and the ground row is 30");
        Check(sheet.Hit.X == 4 && sheet.Hit.Y == 11 && sheet.Hit.W == 24 && sheet.Hit.H == 20, "pet: hit box is read");
        Check(sheet.Anims.Count == 5, "pet: five animations");
        foreach (var name in new[] { "typing", "walk", "done", "ask", "idle" })
        {
            Check(sheet.Anims.TryGetValue(name, out var a) && a.Id == name, "pet: animation '" + name + "' exists");
            if (!sheet.Anims.TryGetValue(name, out var anim)) continue;
            var png = Path.Combine(petDir, anim.File);
            Check(File.Exists(png), "pet: " + anim.File + " exists");
            if (!File.Exists(png)) continue;
            var head = new byte[24];
            using (var fs = File.OpenRead(png)) { int n = fs.Read(head, 0, head.Length); Check(n == 24, "pet: " + anim.File + " has a header"); }
            bool isPng = head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47;
            int width = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
            int height = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
            Check(isPng, "pet: " + anim.File + " is a PNG");
            Check(width == anim.Ms.Count * 32, "pet: " + anim.File + " is frames x 32 wide (" + width + ")");
            Check(height == 32, "pet: " + anim.File + " is 32 tall");
        }
    }

    private static bool Throws(Action act, out string message)
    {
        try { act(); message = ""; return false; }
        catch (Exception ex) { message = ex.Message; return true; }
    }

    private static string PetJson(string anims, string frame = "32") =>
        "{\"frame\":" + frame + ",\"ground\":30,\"hit\":{\"x\":4,\"y\":11,\"w\":24,\"h\":20},\"anims\":{" + anims + "}}";

    private static string PetAnimsJson(string skip = "", string askMs = "100")
    {
        var parts = new List<string>();
        foreach (var n in new[] { "typing", "walk", "done", "ask", "idle" })
        {
            if (n == skip) continue;
            parts.Add("\"" + n + "\":{\"file\":\"" + n + ".png\",\"ms\":[" + (n == "ask" ? askMs : "100,100") + "]}");
        }
        return string.Join(",", parts);
    }

    private static void PetSheetParseErrors()
    {
        string msg;
        Check(!Throws(() => PetSheet.Parse(PetJson(PetAnimsJson())), out msg), "pet parse: a complete sheet parses");
        Check(Throws(() => PetSheet.Parse(PetJson(PetAnimsJson(skip: "ask"))), out msg) && msg.Contains("ask"),
            "pet parse: a missing animation throws and names it");
        Check(Throws(() => PetSheet.Parse(PetJson(PetAnimsJson(askMs: ""))), out msg) && msg.Contains("ask"),
            "pet parse: empty ms throws and names the animation");
        Check(Throws(() => PetSheet.Parse(PetJson(PetAnimsJson(askMs: "100,0"))), out msg),
            "pet parse: a zero frame length throws");
        Check(Throws(() => PetSheet.Parse(PetJson(PetAnimsJson(askMs: "-5"))), out msg),
            "pet parse: a negative frame length throws");
        Check(Throws(() => PetSheet.Parse(PetJson(PetAnimsJson(), frame: "0")), out msg) && msg.Contains("frame"),
            "pet parse: frame 0 throws");
        Check(Throws(() => PetSheet.Parse("{ not json"), out msg) && msg.Length > 0, "pet parse: broken JSON throws");
        Check(Throws(() => PetSheet.Parse("[]"), out msg), "pet parse: a non-object throws");
        Check(Throws(() => PetSheet.Load(Path.Combine(Path.GetTempPath(), "ctb-no-such-" + Guid.NewGuid().ToString("N") + ".json")), out msg),
            "pet load: a missing file throws");
    }

    private static void PetFrameAt()
    {
        var a = new PetAnimInfo("t", "t.png", new[] { 100, 50, 200 });
        Check(a.TotalMs == 350, "frame: TotalMs is the sum of the frame lengths");
        Check(a.FrameAt(0) == 0, "frame: time 0 is frame 0");
        Check(a.FrameAt(99.9) == 0, "frame: just before the first edge is still frame 0");
        Check(a.FrameAt(100) == 1, "frame: exactly on the edge is the next frame");
        Check(a.FrameAt(149.9) == 1 && a.FrameAt(150) == 2, "frame: second edge");
        Check(a.FrameAt(349.9) == 2, "frame: last moment of the cycle is the last frame");
        Check(a.FrameAt(350) == 0, "frame: one full cycle wraps to frame 0");
        Check(a.FrameAt(450) == 1 && a.FrameAt(350 * 1000 + 160) == 2, "frame: times past one cycle wrap");
        Check(a.FrameAt(-1) == 0 && a.FrameAt(-1e9) == 0, "frame: negative time is frame 0");
        Check(a.FrameAt(double.NaN) == 0, "frame: NaN is frame 0");
        Check(a.FrameAt(double.PositiveInfinity) == 0, "frame: infinity is frame 0");
    }

    private static SessionInfo Sess(string id, SessionStatus st = SessionStatus.Running) =>
        new SessionInfo(id) { Status = st };

    private static void PetStageBehaviour()
    {
        Check(PetStage.WidthDots == 274, "stage: width is 2*5*26+6+2*4 = 274 dots");
        Check(PetStage.HomeX(0) == 257 && PetStage.HomeX(1) == 231 && PetStage.HomeX(4) == 153, "stage: seats run right to left");
        Check(PetStage.FrontX(0) == 121 && PetStage.FrontX(4) == 17, "stage: front spots are 136 dots left of the seats");

        var st = new PetStage();
        var a = Sess("a");
        st.Sync(new[] { a });
        Check(st.Actors.Count == 1 && st.Actors[0].Slot == 0 && st.Actors[0].X == PetStage.HomeX(0),
            "stage: a new running session sits at seat 0");
        Check(st.Actors[0].Anim == "typing" && !st.Actors[0].AtFront && !st.Actors[0].FacingLeft,
            "stage: a running pet types at its seat");

        var b = Sess("b");
        st.Sync(new[] { a, b });
        Check(st.Actors.Count == 2 && st.Actors[1].SessionId == "b" && st.Actors[1].Slot == 1 && st.Actors[1].X == PetStage.HomeX(1),
            "stage: the second session takes seat 1");
        st.Advance(1000);
        Check(st.Actors[0].X == PetStage.HomeX(0) && st.Actors[0].Anim == "typing", "stage: a pet that is home does not move");

        // Asking: walks left to the front spot.
        a.Status = SessionStatus.Asking;
        st.Sync(new[] { a, b });
        st.Advance(100);
        var pa = st.Actors[0];
        Check(pa.Anim == "walk" && pa.FacingLeft && !pa.AtFront && pa.X < PetStage.HomeX(0) && pa.X > PetStage.FrontX(0),
            "stage: asking pet walks left (walk, FacingLeft, not at front)");
        Check(Math.Abs(pa.X - (PetStage.HomeX(0) - 3)) < 1e-9, "stage: walking speed is 30 dots per second");
        double dist = PetStage.HomeX(0) - PetStage.FrontX(0);
        st.Advance(dist / PetStage.WalkSpeed * 1000 - 100);
        Check(pa.X == PetStage.FrontX(0) && pa.Anim == "ask" && pa.AtFront && !pa.FacingLeft,
            "stage: after distance/speed it stands at the front spot and asks");
        Check(st.Actors[1].X == PetStage.HomeX(1) && st.Actors[1].Anim == "typing", "stage: other pets stay at their seats");

        // Back to running: walks right to the seat.
        a.Status = SessionStatus.Running;
        st.Sync(new[] { a, b });
        st.Advance(100);
        Check(pa.Anim == "walk" && !pa.FacingLeft && !pa.AtFront && pa.X > PetStage.FrontX(0),
            "stage: back to running -> walks right (FacingLeft false)");
        st.Advance(dist / PetStage.WalkSpeed * 1000 - 100);
        Check(pa.X == PetStage.HomeX(0) && pa.Anim == "typing" && !pa.AtFront, "stage: back at the seat, typing again");

        // Done: walks out, then jumps for joy.
        a.Status = SessionStatus.DoneUnseen;
        st.Sync(new[] { a, b });
        st.Advance(dist / PetStage.WalkSpeed * 1000);
        Check(pa.X == PetStage.FrontX(0) && pa.Anim == "done" && pa.AtFront, "stage: a finished pet stands at the front and does the done dance");

        // Seen: goes home, idle.
        a.Status = SessionStatus.DoneSeen;
        st.Sync(new[] { a, b });
        st.Advance(dist / PetStage.WalkSpeed * 1000);
        Check(pa.X == PetStage.HomeX(0) && pa.Anim == "idle" && !pa.AtFront, "stage: an acknowledged pet goes home and idles");

        // A huge step never overshoots.
        a.Status = SessionStatus.Asking;
        st.Sync(new[] { a, b });
        st.Advance(1e9);
        Check(pa.X == PetStage.FrontX(0) && pa.Anim == "ask", "stage: a huge dt stops exactly at the front spot");
        a.Status = SessionStatus.Running;
        st.Sync(new[] { a, b });
        st.Advance(1e9);
        Check(pa.X == PetStage.HomeX(0) && pa.Anim == "typing", "stage: a huge dt stops exactly at the seat");

        // Bad dt counts as 0.
        double before = pa.X, animBefore = pa.AnimMs;
        st.Advance(-50); st.Advance(double.NaN); st.Advance(double.PositiveInfinity);
        Check(pa.X == before && pa.AnimMs == animBefore, "stage: negative, NaN and infinite dt are treated as 0");

        // Animation clock restarts when the animation changes.
        var clock = new PetStage();
        var c = Sess("c");
        clock.Sync(new[] { c });
        clock.Advance(50); clock.Advance(70);
        Check(clock.Actors[0].Anim == "typing" && Math.Abs(clock.Actors[0].AnimMs - 120) < 1e-9, "stage: AnimMs adds up while the animation stays the same");
        c.Status = SessionStatus.Asking;
        clock.Sync(new[] { c });
        clock.Advance(40);
        Check(clock.Actors[0].Anim == "walk" && clock.Actors[0].AnimMs == 0, "stage: AnimMs goes back to 0 when the animation changes");
        clock.Advance(40);
        Check(Math.Abs(clock.Actors[0].AnimMs - 40) < 1e-9, "stage: ... and counts again afterwards");

        // Seats stay put when others leave; the lowest free seat is reused.
        var s = new PetStage();
        var p = Sess("p"); var q = Sess("q"); var r = Sess("r");
        s.Sync(new[] { p, q, r });
        Check(s.Actors.Count == 3 && s.Actors[2].Slot == 2, "stage: three sessions take seats 0, 1, 2");
        s.Sync(new[] { q, r });
        Check(s.Actors.Count == 2 && s.Actors[0].SessionId == "q" && s.Actors[0].Slot == 1 && s.Actors[0].X == PetStage.HomeX(1),
            "stage: a pet whose neighbour left keeps its seat");
        Check(s.Actors[1].SessionId == "r" && s.Actors[1].Slot == 2, "stage: the gone session's pet is removed, the rest keep their seats");
        var t = Sess("t");
        s.Sync(new[] { q, r, t });
        Check(s.Actors.Count == 3 && s.Actors[0].SessionId == "t" && s.Actors[0].Slot == 0 && s.Actors[0].X == PetStage.HomeX(0),
            "stage: a new session takes the lowest free seat (the one that was freed)");
        s.Sync(new SessionInfo[0]);
        Check(s.Actors.Count == 0, "stage: no sessions, no pets");

        // Actors come out in seat order whatever order Sync is given.
        var o = new PetStage();
        o.Sync(new[] { Sess("x"), Sess("y"), Sess("z") });
        o.Sync(new[] { Sess("z"), Sess("y"), Sess("x") });
        Check(o.Actors[0].SessionId == "x" && o.Actors[2].SessionId == "z", "stage: reordering the input does not move pets between seats");
        Check(o.Actors[0].Slot < o.Actors[1].Slot && o.Actors[1].Slot < o.Actors[2].Slot, "stage: seat numbers go up in the list");
    }

    private static void ConfigPetScale()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ctb-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            Check(AppConfig.Load(path).PetScale == 2, "petScale: a missing config -> 2");
            Check(new AppConfig().PetScale == 2, "petScale: the default is 2");
            File.WriteAllText(path, "{\"port\": 9999}");
            Check(AppConfig.Load(path).PetScale == 2, "petScale: missing key -> 2");
            foreach (var v in new[] { 1, 3, 4, 5, 6 })
            {
                File.WriteAllText(path, "{\"petScale\": " + v + "}");
                Check(AppConfig.Load(path).PetScale == v, "petScale: " + v + " is honored");
            }
            File.WriteAllText(path, "{\"petScale\": 0}");
            Check(AppConfig.Load(path).PetScale == 2, "petScale: 0 is out of range -> 2");
            File.WriteAllText(path, "{\"petScale\": 7}");
            Check(AppConfig.Load(path).PetScale == 2, "petScale: 7 is out of range -> 2");
            File.WriteAllText(path, "{\"petScale\": -3}");
            Check(AppConfig.Load(path).PetScale == 2, "petScale: negative -> 2");
            File.WriteAllText(path, "{\"petScale\": 2.5}");
            Check(AppConfig.Load(path).PetScale == 2, "petScale: a fraction -> 2");
            File.WriteAllText(path, "{\"petScale\": \"4\"}");
            Check(AppConfig.Load(path).PetScale == 2, "petScale: a string -> 2");
            File.WriteAllText(path, "{\"port\": 9999, \"orcaPath\": \"D:\\\\o\\\\orca.exe\", \"petScale\": 4}");
            var all = AppConfig.Load(path);
            Check(all.PetScale == 4 && all.Port == 9999 && all.OrcaPath == @"D:\o\orca.exe",
                "petScale: port and orcaPath still read alongside it");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void VisitKeepsThePet()
    {
        var r = new SessionRegistry();
        int changes = 0;
        r.Changed += () => changes++;

        r.Apply(Ev(NotifyEventKind.Done, "d"), T0);
        changes = 0;
        r.Visit("d");
        Check(r.Snapshot()[0].Status == SessionStatus.DoneSeen && r.Count == 1, "visit: done (unseen) -> done (seen), the pet stays");
        Check(changes == 1, "visit: a change raises Changed once");
        r.Visit("d");
        Check(r.Snapshot()[0].Status == SessionStatus.DoneSeen && changes == 1, "visit: done (seen) stays, no Changed");

        r.Apply(Ev(NotifyEventKind.Ask, "q"), T0.AddMinutes(1));
        changes = 0;
        r.Visit("q");
        var q = r.Snapshot().First(s => s.Id == "q");
        Check(q.Status == SessionStatus.Running && r.Count == 2, "visit: asking -> running, the pet stays");
        Check(changes == 1, "visit: asking -> running raises Changed once");

        r.Apply(Ev(NotifyEventKind.Running, "run"), T0.AddMinutes(2));
        changes = 0;
        r.Visit("run");
        Check(r.Snapshot().First(s => s.Id == "run").Status == SessionStatus.Running && changes == 0,
            "visit: running stays running, no Changed");

        r.Visit("no-such-id");
        Check(r.Count == 3 && changes == 0, "visit: an unknown id does nothing");

        // The old behaviours are untouched: Dismiss still removes, Acknowledge still only acknowledges.
        r.Dismiss("run");
        Check(r.Count == 2, "visit: Dismiss still removes the pet");
    }

    private static void VisitThenStage()
    {
        var r = new SessionRegistry();
        var stage = new PetStage();
        void Sync() => stage.Sync(PetSelection.Select(r.Snapshot()).Displayed);
        double dist = PetStage.HomeX(0) - PetStage.FrontX(0);

        // A finished pet that has come out walks home when visited and idles there.
        r.Apply(Ev(NotifyEventKind.Done, "d"), T0);
        Sync();
        stage.Advance(1e9);
        var d = stage.Actors[0];
        Check(d.AtFront && d.Anim == "done" && d.X == PetStage.FrontX(0), "visit+stage: the finished pet is out in front");
        r.Visit("d");
        Sync();
        stage.Advance(100);
        Check(stage.Actors.Count == 1 && d.Anim == "walk" && !d.FacingLeft && !d.AtFront && d.X > PetStage.FrontX(0),
            "visit+stage: after the visit the pet walks right, towards its seat");
        stage.Advance(dist / PetStage.WalkSpeed * 1000);
        Check(stage.Actors.Count == 1 && d.X == PetStage.HomeX(0) && d.Anim == "idle" && !d.AtFront,
            "visit+stage: it stands at its seat and idles; the pet is still there");

        // An asking pet goes back to its seat and types.
        var r2 = new SessionRegistry();
        var stage2 = new PetStage();
        r2.Apply(Ev(NotifyEventKind.Ask, "q"), T0);
        stage2.Sync(PetSelection.Select(r2.Snapshot()).Displayed);
        stage2.Advance(1e9);
        var q = stage2.Actors[0];
        Check(q.AtFront && q.Anim == "ask", "visit+stage: the asking pet is out in front");
        r2.Visit("q");
        stage2.Sync(PetSelection.Select(r2.Snapshot()).Displayed);
        stage2.Advance(100);
        Check(q.Anim == "walk" && !q.FacingLeft, "visit+stage: the asking pet walks back after the visit");
        stage2.Advance(dist / PetStage.WalkSpeed * 1000);
        Check(stage2.Actors.Count == 1 && q.X == PetStage.HomeX(0) && q.Anim == "typing",
            "visit+stage: it is back at its seat typing; the pet is still there");

        // A running pet does not move when visited.
        var r3 = new SessionRegistry();
        var stage3 = new PetStage();
        r3.Apply(Ev(NotifyEventKind.Running, "x"), T0);
        stage3.Sync(PetSelection.Select(r3.Snapshot()).Displayed);
        stage3.Advance(500);
        r3.Visit("x");
        stage3.Sync(PetSelection.Select(r3.Snapshot()).Displayed);
        stage3.Advance(500);
        Check(stage3.Actors[0].X == PetStage.HomeX(0) && stage3.Actors[0].Anim == "typing",
            "visit+stage: a running pet visited stays at its seat typing");
    }

    private static void PidParsing()
    {
        int P(string json) => NotifyEvent.FromJson(json).Pid;
        Check(P("{\"session_id\":\"p\",\"pid\":4321}") == 4321, "pid: a number is read");
        Check(P("{\"session_id\":\"p\",\"pid\":\"4321\"}") == 4321, "pid: a string of digits is read");
        Check(P("{\"session_id\":\"p\"}") == 0, "pid: missing -> 0");
        Check(P("{\"session_id\":\"p\",\"pid\":0}") == 0, "pid: 0 -> 0");
        Check(P("{\"session_id\":\"p\",\"pid\":-7}") == 0, "pid: negative -> 0");
        Check(P("{\"session_id\":\"p\",\"pid\":\"abc\"}") == 0, "pid: letters -> 0");
        Check(P("{\"session_id\":\"p\",\"pid\":\"12abc\"}") == 0, "pid: digits with letters -> 0");
        Check(P("{\"session_id\":\"p\",\"pid\":\"\"}") == 0, "pid: empty string -> 0");
        Check(P("{\"session_id\":\"p\",\"pid\":1.5}") == 0, "pid: a fraction -> 0");
        Check(P("{\"session_id\":\"p\",\"pid\":99999999999}") == 0, "pid: too big for an int -> 0");
        Check(P("{\"session_id\":\"p\",\"pid\":true}") == 0, "pid: a boolean -> 0");
        Check(P("{\"session_id\":\"p\",\"pid\":null}") == 0, "pid: null -> 0");
        var ev = NotifyEvent.FromJson("{\"session_id\":\"p\",\"type\":\"done\",\"source\":\"proj\",\"pid\":4321}");
        Check(ev.Kind == NotifyEventKind.Done && ev.Source == "proj" && ev.SessionId == "p", "pid: the rest of the event is unchanged");
        Check(NotifyEvent.FromQuery("/done?session_id=p&type=done&pid=4321").Pid == 0, "pid: the legacy GET route does not carry it");
    }

    private static void PidInLedger()
    {
        var r = new SessionRegistry();
        r.Apply(new NotifyEvent { Kind = NotifyEventKind.Started, SessionId = "p", Pid = 777 }, T0);
        Check(r.Snapshot()[0].Pid == 777, "pid ledger: an event with a pid sets SessionInfo.Pid");
        r.Apply(new NotifyEvent { Kind = NotifyEventKind.Running, SessionId = "p" }, T0.AddMinutes(1));
        Check(r.Snapshot()[0].Pid == 777, "pid ledger: an event without a pid keeps it");
        r.Apply(new NotifyEvent { Kind = NotifyEventKind.Done, SessionId = "p", Pid = 888 }, T0.AddMinutes(2));
        Check(r.Snapshot()[0].Pid == 888, "pid ledger: a new pid overwrites the old one");
        r.Apply(new NotifyEvent { Kind = NotifyEventKind.Running, SessionId = "n" }, T0);
        Check(r.Snapshot().First(s => s.Id == "n").Pid == 0, "pid ledger: a session that never sent one has pid 0");
    }

    private static SessionInfo Live(string id, int pid = 0, string hwnd = "") =>
        new SessionInfo(id) { Pid = pid, Hwnd = hwnd };

    private static void LivenessProcess()
    {
        var d1 = new DateTime(2026, 11, 27, 9, 0, 0, DateTimeKind.Local);
        var d2 = d1.AddMinutes(30);
        var starts = new Dictionary<int, DateTime?>();
        int processCalls = 0;
        Func<int, DateTime?> start = pid => { processCalls++; return starts.TryGetValue(pid, out var v) ? v : null; };
        Func<string, bool> noWindow = _ => throw new InvalidOperationException("windowExists must not be asked");

        // Alive: never returned.
        var live = new SessionLiveness();
        var a = Live("a", pid: 100);
        starts[100] = d1;
        Check(live.FindGone(new[] { a }, start, noWindow).Count == 0, "liveness: a live process is not returned (first look)");
        Check(live.FindGone(new[] { a }, start, noWindow).Count == 0, "liveness: a live process is not returned (second look)");
        Check(processCalls == 2, "liveness: the process is looked at every time");

        // Process ends.
        starts[100] = null;
        var gone = live.FindGone(new[] { a }, start, noWindow);
        Check(gone.Count == 1 && gone[0] == "a", "liveness: a process that was seen alive and is now gone is returned");

        // Start time differs (pid reused).
        var live2 = new SessionLiveness();
        starts[100] = d1;
        live2.FindGone(new[] { a }, start, noWindow);
        starts[100] = d2;
        gone = live2.FindGone(new[] { a }, start, noWindow);
        Check(gone.Count == 1 && gone[0] == "a", "liveness: the same pid with a different start time is returned (pid reused)");

        // Never seen alive: left alone.
        var live3 = new SessionLiveness();
        var b = Live("b", pid: 200);
        starts[200] = null;
        Check(live3.FindGone(new[] { b }, start, noWindow).Count == 0, "liveness: a pid never seen alive is not returned");
        Check(live3.FindGone(new[] { b }, start, noWindow).Count == 0, "liveness: ... however often it is looked at");
        starts[200] = d1;
        live3.FindGone(new[] { b }, start, noWindow);
        starts[200] = null;
        Check(live3.FindGone(new[] { b }, start, noWindow).Count == 1, "liveness: ... until it has been seen alive");

        // The session's pid changes: the old memory does not count.
        var live4 = new SessionLiveness();
        var c = Live("c", pid: 300);
        starts[300] = d1;
        live4.FindGone(new[] { c }, start, noWindow);
        c.Pid = 400;
        starts[400] = null;
        Check(live4.FindGone(new[] { c }, start, noWindow).Count == 0, "liveness: a new pid that is not yet seen alive is not returned");
        starts[400] = d2;
        live4.FindGone(new[] { c }, start, noWindow);
        starts[400] = null;
        Check(live4.FindGone(new[] { c }, start, noWindow).Count == 1, "liveness: the new pid is watched from then on");

        // Memory of sessions that left the ledger is dropped.
        var live5 = new SessionLiveness();
        var e = Live("e", pid: 500);
        starts[500] = d1;
        live5.FindGone(new[] { e }, start, noWindow);
        live5.FindGone(new SessionInfo[0], start, noWindow);
        starts[500] = null;
        Check(live5.FindGone(new[] { Live("e", pid: 500) }, start, noWindow).Count == 0,
            "liveness: memory of a session that left the ledger is dropped");

        // Several sessions: only the gone ones, in order.
        var live6 = new SessionLiveness();
        var s1 = Live("s1", pid: 601); var s2 = Live("s2", pid: 602); var s3 = Live("s3", pid: 603);
        starts[601] = d1; starts[602] = d1; starts[603] = d1;
        live6.FindGone(new[] { s1, s2, s3 }, start, noWindow);
        starts[601] = null; starts[603] = null;
        gone = live6.FindGone(new[] { s1, s2, s3 }, start, noWindow);
        Check(gone.Count == 2 && gone[0] == "s1" && gone[1] == "s3", "liveness: only the gone sessions come back, in order");

        // A session with neither pid nor window is never returned, and nothing is asked.
        int before = processCalls;
        Check(new SessionLiveness().FindGone(new[] { Live("none") }, start, noWindow).Count == 0 && processCalls == before,
            "liveness: a session with neither pid nor hwnd is never returned");
    }

    private static void LivenessWindow()
    {
        var asked = new List<string>();
        bool exists = true;
        Func<string, bool> window = h => { asked.Add(h); return exists; };
        Func<int, DateTime?> noProcess = _ => throw new InvalidOperationException("processStart must not be asked");

        var live = new SessionLiveness();
        var w = Live("w", hwnd: "12345");
        Check(live.FindGone(new[] { w }, noProcess, window).Count == 0 && asked.Count == 1 && asked[0] == "12345",
            "liveness window: an existing window is not returned (and is asked about by its handle text)");
        exists = false;
        var gone = live.FindGone(new[] { w }, noProcess, window);
        Check(gone.Count == 1 && gone[0] == "w", "liveness window: a session with no pid whose window is gone is returned");

        asked.Clear();
        foreach (var bad in new[] { "0", "", "abc", "-5", "12x", "1.5" })
        {
            var sess = Live("bad", hwnd: bad);
            Check(new SessionLiveness().FindGone(new[] { sess }, noProcess, window).Count == 0,
                "liveness window: hwnd '" + bad + "' is not judged");
        }
        Check(asked.Count == 0, "liveness window: such handles are never asked about");

        // With a pid, the process decides; the window is not consulted.
        var both = Live("both", pid: 900, hwnd: "12345");
        var d1 = new DateTime(2026, 11, 27, 9, 0, 0, DateTimeKind.Local);
        Check(new SessionLiveness().FindGone(new[] { both }, _ => d1, window).Count == 0 && asked.Count == 0,
            "liveness window: a session with a pid ignores its window");
    }

    // The shape of ~/.claude/sessions/<pid>.json (Claude Code 2.1.x), with synthetic values.
    private const string RealShape =
        "{\"pid\":21852,\"sessionId\":\"56d655be-aaaa\",\"cwd\":\"C:\\\\Users\\\\u\\\\proj\",\"startedAt\":1795748189094," +
        "\"procStart\":\"134402217861979895\",\"version\":\"2.1.332\",\"kind\":\"interactive\",\"entrypoint\":\"cli\"," +
        "\"name\":\"proj-92\",\"status\":\"idle\",\"updatedAt\":1795755162578,\"statusUpdatedAt\":1795755162578}";

    private static void LiveParse()
    {
        var e = LiveSessions.Parse(RealShape);
        Check(e != null, "live parse: the real shape parses");
        if (e != null)
        {
            Check(e.Pid == 21852 && e.SessionId == "56d655be-aaaa", "live parse: pid and sessionId");
            Check(e.Cwd == @"C:\Users\u\proj" && e.Kind == "interactive" && e.Status == "idle", "live parse: cwd, kind, status");
            Check(e.ProcStartFileTime == 134402217861979895L, "live parse: procStart text is read as a FILETIME");
            Check(e.UpdatedAt == DateTimeOffset.FromUnixTimeMilliseconds(1795755162578), "live parse: updatedAt is Unix milliseconds");
        }

        var num = LiveSessions.Parse("{\"pid\":5,\"sessionId\":\"s\",\"procStart\":134402217861979895}");
        Check(num != null && num.ProcStartFileTime == 134402217861979895L, "live parse: procStart as a number is read");
        Check(num != null && num.UpdatedAt == null && num.Cwd == "" && num.Kind == "" && num.Status == "",
            "live parse: missing optional fields are empty / null");
        var badStart = LiveSessions.Parse("{\"pid\":5,\"sessionId\":\"s\",\"procStart\":\"soon\",\"updatedAt\":\"x\"}");
        Check(badStart != null && badStart.ProcStartFileTime == null && badStart.UpdatedAt == null,
            "live parse: unreadable procStart / updatedAt become null, the entry stays");

        Check(LiveSessions.Parse("{\"sessionId\":\"s\"}") == null, "live parse: no pid -> null");
        Check(LiveSessions.Parse("{\"pid\":0,\"sessionId\":\"s\"}") == null, "live parse: pid 0 -> null");
        Check(LiveSessions.Parse("{\"pid\":-3,\"sessionId\":\"s\"}") == null, "live parse: negative pid -> null");
        Check(LiveSessions.Parse("{\"pid\":\"abc\",\"sessionId\":\"s\"}") == null, "live parse: pid as letters -> null");
        Check(LiveSessions.Parse("{\"pid\":1.5,\"sessionId\":\"s\"}") == null, "live parse: fractional pid -> null");
        Check(LiveSessions.Parse("{\"pid\":5}") == null, "live parse: no sessionId -> null");
        Check(LiveSessions.Parse("{\"pid\":5,\"sessionId\":\"\"}") == null, "live parse: empty sessionId -> null");
        Check(LiveSessions.Parse("{\"pid\":5,\"sessionId\":7}") == null, "live parse: sessionId that is not text -> null");
        Check(LiveSessions.Parse("{\"pid\":21852,\"sessionId\":\"56d6") == null, "live parse: half-written JSON -> null");
        Check(LiveSessions.Parse("not json") == null, "live parse: broken JSON -> null");
        Check(LiveSessions.Parse("") == null && LiveSessions.Parse(null) == null, "live parse: empty text -> null");
        Check(LiveSessions.Parse("[1,2]") == null, "live parse: a non-object -> null");
        Check(LiveSessions.Parse("{\"pid\":5,\"sessionId\":\"s\",\"updatedAt\":99999999999999999}") is { UpdatedAt: null },
            "live parse: an updatedAt out of range becomes null");
    }

    private static void LiveIsInteractive()
    {
        bool I(string kind) => LiveSessions.IsInteractive(new LiveSessionEntry { Pid = 1, SessionId = "s", Kind = kind });
        Check(I("interactive") && I("Interactive") && I("INTERACTIVE"), "live interactive: 'interactive' in any case");
        Check(I(""), "live interactive: an empty kind counts");
        var none = LiveSessions.Parse("{\"pid\":5,\"sessionId\":\"s\"}");
        Check(none != null && LiveSessions.IsInteractive(none), "live interactive: a file with no kind counts");
        Check(!I("headless") && !I("sdk") && !I("interactive2"), "live interactive: other kinds do not");
    }

    private static void LiveSameProcess()
    {
        const long ft = 134402217861979895L;
        var exact = DateTime.FromFileTimeUtc(ft);
        Check(LiveSessions.SameProcess(ft, exact), "same process: the exact start time");
        Check(LiveSessions.SameProcess(ft, exact.AddSeconds(1)), "same process: 1 second off is the same");
        Check(LiveSessions.SameProcess(ft, exact.AddSeconds(-2)), "same process: 2 seconds early is still the same");
        Check(!LiveSessions.SameProcess(ft, exact.AddSeconds(5)), "same process: 5 seconds off is another process (pid reused)");
        Check(!LiveSessions.SameProcess(ft, exact.AddSeconds(-5)), "same process: 5 seconds early is another process");
        Check(LiveSessions.SameProcess(null, exact), "same process: no procStart in the file -> a live process counts");
        Check(!LiveSessions.SameProcess(ft, null), "same process: no process -> false");
        Check(!LiveSessions.SameProcess(null, null), "same process: no process and no procStart -> false");
        Check(LiveSessions.SameProcess(ft, DateTime.MinValue), "same process: MinValue (could not look) -> true");
        Check(LiveSessions.SameProcess(ft, exact.ToLocalTime()), "same process: a local-time start is converted to UTC first");
    }

    private static LiveSessionEntry Entry(string id, int pid, string status = "idle", DateTimeOffset? updatedAt = null, string cwd = @"C:\x\proj") =>
        new LiveSessionEntry { Pid = pid, SessionId = id, Cwd = cwd, Kind = "interactive", Status = status, UpdatedAt = updatedAt };

    private static void ReconcileBasics()
    {
        var r = new SessionRegistry();
        int changes = 0;
        r.Changed += () => changes++;

        Check(r.Reconcile(Entry("b", 10, "busy"), T0) && r.Count == 1, "reconcile: a session that is not in the ledger is added");
        var b = r.Snapshot()[0];
        Check(b.Status == SessionStatus.Running && b.Pid == 10 && b.Source == "proj" && b.ProjectPath == @"C:\x\proj",
            "reconcile: busy -> Running; pid, name (cwd leaf) and path are filled in");
        Check(b.StartedAt == T0 && b.LastEventAt == T0, "reconcile: StartedAt and LastEventAt are 'now'");
        Check(changes == 1, "reconcile: Changed once for the add");

        r.Reconcile(Entry("i", 11, "idle"), T0);
        r.Reconcile(Entry("u", 12, "Weird"), T0);
        r.Reconcile(Entry("B", 13, "BUSY"), T0);
        var snap = r.Snapshot();
        Check(snap.First(s => s.Id == "i").Status == SessionStatus.DoneSeen, "reconcile: idle -> DoneSeen");
        Check(snap.First(s => s.Id == "u").Status == SessionStatus.DoneSeen, "reconcile: an unknown status -> DoneSeen");
        Check(snap.First(s => s.Id == "B").Status == SessionStatus.Running, "reconcile: 'BUSY' in any case -> Running");
        Check(r.Count == 4, "reconcile: four sessions");

        changes = 0;
        Check(!r.Reconcile(Entry("b", 10, "busy"), T0.AddSeconds(5)) && r.Count == 4 && changes == 0,
            "reconcile: the same session again is not added twice, returns false, no Changed");

        Check(r.Reconcile(Entry("nocwd", 14, "idle", cwd: ""), T0) && r.Snapshot().First(s => s.Id == "nocwd").Source == "Claude",
            "reconcile: no cwd -> the name is 'Claude'");

        // Hooks decide the status: an existing session keeps it.
        var h = new SessionRegistry();
        h.Apply(Ev(NotifyEventKind.Done, "hooked"), T0);
        changes = 0;
        h.Changed += () => changes++;
        Check(h.Reconcile(Entry("hooked", 55, "idle"), T0.AddMinutes(1)), "reconcile: an existing session without a pid gets one (a change)");
        var hs = h.Snapshot()[0];
        Check(hs.Status == SessionStatus.DoneUnseen && hs.Pid == 55 && hs.ProjectPath == @"C:\x\proj",
            "reconcile: the status stays DoneUnseen (idle does not overwrite it); pid and path are filled");
        Check(changes == 1, "reconcile: Changed once for the fill-in");
        Check(!h.Reconcile(Entry("hooked", 55, "idle"), T0.AddMinutes(2)) && changes == 1, "reconcile: nothing more to fill -> false, no Changed");

        var keep = new SessionRegistry();
        keep.Apply(new NotifyEvent { Kind = NotifyEventKind.Running, SessionId = "k", Pid = 10, ProjectPath = @"C:\own\path" }, T0);
        keep.Reconcile(Entry("k", 99, "busy", cwd: @"C:\other"), T0.AddMinutes(1));
        var ks = keep.Snapshot()[0];
        Check(ks.Pid == 10 && ks.ProjectPath == @"C:\own\path", "reconcile: a pid and a path that are already set are not overwritten");
    }

    private static void ReconcileOthers()
    {
        // The process now runs another session: the old id goes, the new one comes.
        var r = new SessionRegistry();
        r.Apply(new NotifyEvent { Kind = NotifyEventKind.Running, SessionId = "old", Pid = 70 }, T0);
        Check(r.Reconcile(Entry("new", 70, "busy", updatedAt: T0.AddHours(1)), T0.AddHours(1)), "others: the list is newer -> reconcile changes the ledger");
        Check(r.Count == 1 && r.Snapshot()[0].Id == "new", "others: the old id of the same pid is removed and the new id is added");

        // The hooks said something later than the list: do nothing.
        var r2 = new SessionRegistry();
        r2.Apply(new NotifyEvent { Kind = NotifyEventKind.Running, SessionId = "old", Pid = 70 }, T0.AddHours(2));
        int changes = 0;
        r2.Changed += () => changes++;
        Check(!r2.Reconcile(Entry("new", 70, "busy", updatedAt: T0.AddHours(1)), T0.AddHours(3)), "others: a hook event newer than the list -> false");
        Check(r2.Count == 1 && r2.Snapshot()[0].Id == "old" && changes == 0, "others: ... the ledger and Changed are untouched");

        // No UpdatedAt: cannot say the list is older, so remove and add.
        var r3 = new SessionRegistry();
        r3.Apply(new NotifyEvent { Kind = NotifyEventKind.Running, SessionId = "old", Pid = 70 }, T0.AddHours(2));
        Check(r3.Reconcile(Entry("new", 70, "idle", updatedAt: null), T0.AddHours(3)) && r3.Count == 1 && r3.Snapshot()[0].Id == "new",
            "others: no updatedAt in the list -> the old id is removed and the new one added");

        // Other pids are never touched.
        var r4 = new SessionRegistry();
        r4.Apply(new NotifyEvent { Kind = NotifyEventKind.Running, SessionId = "x", Pid = 71 }, T0);
        r4.Apply(new NotifyEvent { Kind = NotifyEventKind.Running, SessionId = "nopid" }, T0);
        r4.Reconcile(Entry("y", 72, "busy", updatedAt: T0.AddHours(1)), T0.AddHours(1));
        Check(r4.Count == 3, "others: sessions with another pid (or none) stay");
    }

    private static void ReconcileThenStage()
    {
        var r = new SessionRegistry();
        r.Reconcile(Entry("busy1", 1, "busy"), T0);
        r.Reconcile(Entry("idle1", 2, "idle"), T0.AddSeconds(1));
        var stage = new PetStage();
        stage.Sync(PetSelection.Select(r.Snapshot()).Displayed);
        stage.Advance(0);
        var busy = stage.Actors.First(a => a.SessionId == "busy1");
        var idle = stage.Actors.First(a => a.SessionId == "idle1");
        Check(stage.Actors.Count == 2, "reconcile+stage: both discovered sessions get a pet");
        Check(busy.Anim == "typing" && busy.X == PetStage.HomeX(busy.Slot), "reconcile+stage: a busy session sits at its seat typing");
        Check(idle.Anim == "idle" && idle.X == PetStage.HomeX(idle.Slot) && !idle.AtFront,
            "reconcile+stage: an idle session stands at its seat (does not walk out)");
    }

    private static void SetHwndOnlyWhenEmpty()
    {
        var r = new SessionRegistry();
        int changes = 0;
        r.Changed += () => changes++;
        r.Apply(Ev(NotifyEventKind.Running, "a"), T0);
        r.Apply(new NotifyEvent { Kind = NotifyEventKind.Running, SessionId = "b", Hwnd = "111" }, T0);
        changes = 0;
        r.SetHwnd("a", "222");
        r.SetHwnd("b", "333");
        r.SetHwnd("nope", "444");
        r.SetHwnd("a", "");
        var snap = r.Snapshot();
        Check(snap.First(s => s.Id == "a").Hwnd == "222", "sethwnd: an empty hwnd is filled in");
        Check(snap.First(s => s.Id == "b").Hwnd == "111", "sethwnd: an hwnd that is already set is kept");
        Check(changes == 0 && r.Count == 2, "sethwnd: no Changed, no new session for an unknown id");
        r.SetHwnd("a", "999");
        Check(r.Snapshot().First(s => s.Id == "a").Hwnd == "222", "sethwnd: once filled in, it is not replaced");
    }

    private static void LeftSessionsStayAway()
    {
        // A right click, SessionEnd and "clear all" must not be undone by the next look at Claude's list.
        var r = new SessionRegistry();
        r.Reconcile(Entry("a", 1, "busy"), T0);
        r.Dismiss("a");
        Check(!r.Reconcile(Entry("a", 1, "busy"), T0.AddSeconds(5)) && r.Count == 0, "left: a dismissed pet is not brought back by the list");
        r.Apply(Ev(NotifyEventKind.Running, "a"), T0.AddSeconds(10));
        Check(r.Count == 1, "left: the session's own next hook event brings it back");
        r.Reconcile(Entry("a", 1, "busy"), T0.AddSeconds(15)); // fills in the pid the hook did not send
        Check(!r.Reconcile(Entry("a", 1, "busy"), T0.AddSeconds(16)) && r.Count == 1, "left: ... and the list then adds nothing twice");

        var e = new SessionRegistry();
        e.Reconcile(Entry("z", 2, "busy"), T0);
        e.Apply(new NotifyEvent { Kind = NotifyEventKind.Ended, SessionId = "z" }, T0.AddSeconds(1));
        Check(!e.Reconcile(Entry("z", 2, "busy"), T0.AddSeconds(2)) && e.Count == 0, "left: an ended session is not brought back by the list");

        var c = new SessionRegistry();
        c.Reconcile(Entry("c", 3, "busy"), T0);
        c.Clear();
        Check(!c.Reconcile(Entry("c", 3, "busy"), T0.AddSeconds(1)) && c.Count == 0, "left: cleared pets are not brought back by the list");
        Check(c.Reconcile(Entry("other", 4, "busy"), T0.AddSeconds(1)) && c.Count == 1, "left: other sessions are still discovered");
    }

    private static void JumpChooseWindow()
    {
        var asked = new List<string>();
        Func<string, bool> all = h => { asked.Add(h); return true; };
        Func<string, bool> none = h => { asked.Add(h); return false; };
        Func<string, bool> only222 = h => { asked.Add(h); return h == "222"; };

        Check(JumpTarget.ChooseWindow("111", "222", all) == "111", "choose: a usable console window wins");
        Check(JumpTarget.ChooseWindow("", "222", all) == "222", "choose: no console window -> the fallback");
        Check(JumpTarget.ChooseWindow(null, "222", all) == "222", "choose: null console window -> the fallback");
        Check(JumpTarget.ChooseWindow("111", "222", only222) == "222", "choose: an unusable console window -> the fallback");
        Check(JumpTarget.ChooseWindow("111", "222", none) == "", "choose: neither usable -> empty");
        Check(JumpTarget.ChooseWindow("", "", all) == "" && JumpTarget.ChooseWindow(null, null, all) == "", "choose: nothing at all -> empty");

        asked.Clear();
        foreach (var bad in new[] { "0", "-5", "abc", "", "1.5", "12x", " " })
            Check(JumpTarget.ChooseWindow(bad, bad, all) == "", "choose: '" + bad + "' is not read as a window");
        Check(asked.Count == 0, "choose: usable is never asked about a value that is not a handle");

        asked.Clear();
        Check(JumpTarget.ChooseWindow("0", "222", all) == "222" && asked.Count == 1 && asked[0] == "222",
            "choose: a bad console value is skipped and only the fallback is asked about");
        asked.Clear();
        JumpTarget.ChooseWindow("111", "222", all);
        Check(asked.Count == 1 && asked[0] == "111", "choose: once the console window is usable the fallback is not asked");
        Check(JumpTarget.ChooseWindow("  333 ", "", all) == "333", "choose: spaces around a number are ignored");
    }

    private static void JumpPickTab()
    {
        var tabs = new List<string> { "alpha", "W4-probe", "Beta" };
        Check(JumpTarget.PickTab(tabs, "W4-probe") == 1, "tab: exactly one match -> its index");
        Check(JumpTarget.PickTab(tabs, "alpha") == 0 && JumpTarget.PickTab(tabs, "Beta") == 2, "tab: first and last");
        Check(JumpTarget.PickTab(tabs, "gamma") == -1, "tab: no match -> -1");
        Check(JumpTarget.PickTab(new List<string> { "x", "same", "same" }, "same") == -1, "tab: two tabs with the same name -> -1");
        Check(JumpTarget.PickTab(tabs, "") == -1 && JumpTarget.PickTab(tabs, "   ") == -1 && JumpTarget.PickTab(tabs, null) == -1,
            "tab: an empty, blank or missing title -> -1");
        Check(JumpTarget.PickTab(tabs, "w4-probe") == -1 && JumpTarget.PickTab(tabs, "BETA") == -1, "tab: a different case does not match");
        Check(JumpTarget.PickTab(new List<string>(), "alpha") == -1, "tab: no tabs -> -1");
        Check(JumpTarget.PickTab(tabs, "W4-probe ") == -1, "tab: the name must be exactly the title");
    }

    private static void SetConsoleHwndOverwrites()
    {
        var r = new SessionRegistry();
        int changes = 0;
        r.Changed += () => changes++;
        r.Apply(new NotifyEvent { Kind = NotifyEventKind.Running, SessionId = "a", Hwnd = "111" }, T0);
        r.Apply(Ev(NotifyEventKind.Running, "b"), T0);
        changes = 0;

        r.SetConsoleHwnd("a", "222");
        r.SetConsoleHwnd("b", "333");
        var snap = r.Snapshot();
        Check(snap.First(s => s.Id == "a").Hwnd == "222", "console hwnd: replaces the hook's hwnd");
        Check(snap.First(s => s.Id == "b").Hwnd == "333", "console hwnd: fills an empty hwnd too");
        r.SetConsoleHwnd("a", "");
        r.SetConsoleHwnd("a", "  ");
        Check(r.Snapshot().First(s => s.Id == "a").Hwnd == "222", "console hwnd: an empty value is not set");
        r.SetConsoleHwnd("nope", "444");
        Check(r.Count == 2, "console hwnd: an unknown id does nothing");
        Check(changes == 0, "console hwnd: Changed is not raised");

        // SetHwnd keeps its own rule (only when empty).
        r.SetHwnd("a", "999");
        Check(r.Snapshot().First(s => s.Id == "a").Hwnd == "222", "console hwnd: SetHwnd still does not overwrite");
        // A later hook event brings its own hwnd back; setting the console one again wins again.
        r.Apply(new NotifyEvent { Kind = NotifyEventKind.Done, SessionId = "a", Hwnd = "111" }, T0.AddSeconds(1));
        Check(r.Snapshot().First(s => s.Id == "a").Hwnd == "111", "console hwnd: a later hook event sets its hwnd (as before)");
        r.SetConsoleHwnd("a", "222");
        Check(r.Snapshot().First(s => s.Id == "a").Hwnd == "222", "console hwnd: and the console's window replaces it again");
    }

    private static void TsParsing()
    {
        DateTimeOffset? At(string json) => NotifyEvent.FromJson(json).At;
        var expected = DateTimeOffset.FromUnixTimeMilliseconds(1795755162578);
        Check(At("{\"session_id\":\"t\",\"ts\":1795755162578}") == expected, "ts: a number is read as Unix milliseconds");
        Check(At("{\"session_id\":\"t\",\"ts\":\"1795755162578\"}") == expected, "ts: a string of digits is read");
        Check(At("{\"session_id\":\"t\"}") == null, "ts: missing -> null");
        Check(At("{\"session_id\":\"t\",\"ts\":-5}") == null, "ts: negative -> null");
        Check(At("{\"session_id\":\"t\",\"ts\":\"abc\"}") == null, "ts: letters -> null");
        Check(At("{\"session_id\":\"t\",\"ts\":\"12abc\"}") == null, "ts: digits with letters -> null");
        Check(At("{\"session_id\":\"t\",\"ts\":1.5}") == null, "ts: a fraction -> null");
        Check(At("{\"session_id\":\"t\",\"ts\":\"\"}") == null, "ts: empty -> null");
        Check(At("{\"session_id\":\"t\",\"ts\":99999999999999999999}") == null, "ts: too big for a long -> null");
        Check(At("{\"session_id\":\"t\",\"ts\":9999999999999999}") == null, "ts: a long that is out of the time range -> null");
        Check(At("{\"session_id\":\"t\",\"ts\":true}") == null && At("{\"session_id\":\"t\",\"ts\":null}") == null, "ts: boolean / null -> null");
        Check(NotifyEvent.FromJson("{\"session_id\":\"t\",\"type\":\"done\",\"ts\":1795755162578}").Kind == NotifyEventKind.Done,
            "ts: the rest of the event is unchanged");
        Check(NotifyEvent.FromQuery("/done?session_id=t&type=done&ts=1795755162578").At == null, "ts: the legacy GET route does not carry it");
    }

    private static void MapStatusValues()
    {
        Check(LiveSessions.MapStatus("busy") == SessionStatus.Running, "map: busy -> Running");
        Check(LiveSessions.MapStatus("BUSY") == SessionStatus.Running && LiveSessions.MapStatus("Busy") == SessionStatus.Running, "map: case is ignored");
        Check(LiveSessions.MapStatus("shell") == SessionStatus.Running, "map: shell -> Running");
        Check(LiveSessions.MapStatus("waiting") == SessionStatus.Asking, "map: waiting -> Asking");
        Check(LiveSessions.MapStatus("idle") == SessionStatus.DoneSeen && LiveSessions.MapStatus("IDLE") == SessionStatus.DoneSeen, "map: idle -> DoneSeen (at rest)");
        Check(LiveSessions.MapStatus("thinking") == null, "map: an unknown value -> null");
        Check(LiveSessions.MapStatus("") == null && LiveSessions.MapStatus("  ") == null && LiveSessions.MapStatus(null) == null, "map: empty / null -> null");
    }

    private static void StatusUpdatedAtParsing()
    {
        var e = LiveSessions.Parse(RealShape);
        Check(e != null && e.StatusUpdatedAt == DateTimeOffset.FromUnixTimeMilliseconds(1795755162578), "statusUpdatedAt: read like updatedAt");
        var none = LiveSessions.Parse("{\"pid\":5,\"sessionId\":\"s\"}");
        Check(none != null && none.StatusUpdatedAt == null, "statusUpdatedAt: missing -> null");
        var bad = LiveSessions.Parse("{\"pid\":5,\"sessionId\":\"s\",\"statusUpdatedAt\":\"soon\"}");
        Check(bad != null && bad.StatusUpdatedAt == null, "statusUpdatedAt: not a number -> null, the entry stays");
        var range = LiveSessions.Parse("{\"pid\":5,\"sessionId\":\"s\",\"statusUpdatedAt\":99999999999999999}");
        Check(range != null && range.StatusUpdatedAt == null, "statusUpdatedAt: out of range -> null");
    }

    private static NotifyEvent EvAt(NotifyEventKind kind, string id, DateTimeOffset? at, string source = "") =>
        new NotifyEvent { Kind = kind, SessionId = id, Source = source.Length > 0 ? source : id, At = at };

    private static void ApplyWithTimes()
    {
        var r = new SessionRegistry();
        r.Apply(EvAt(NotifyEventKind.Running, "a", T0.AddSeconds(10)), T0.AddSeconds(13));
        var a = r.Snapshot()[0];
        Check(a.StatusAt == T0.AddSeconds(10) && a.Status == SessionStatus.Running, "apply+ts: StatusAt is when it happened, not when it arrived");
        Check(a.LastEventAt == T0.AddSeconds(13), "apply+ts: LastEventAt is still the arrival time");

        // An older event (it took the long way round): other fields update, the status stays.
        r.Apply(EvAt(NotifyEventKind.Done, "a", T0.AddSeconds(5), source: "renamed"), T0.AddSeconds(14));
        a = r.Snapshot()[0];
        Check(a.Status == SessionStatus.Running && a.StatusAt == T0.AddSeconds(10), "apply+ts: an older event does not change Status or StatusAt");
        Check(a.Source == "renamed" && a.LastEventAt == T0.AddSeconds(14), "apply+ts: ... but its other fields are taken");

        // Newer ts wins.
        r.Apply(EvAt(NotifyEventKind.Done, "a", T0.AddSeconds(12)), T0.AddSeconds(15));
        a = r.Snapshot()[0];
        Check(a.Status == SessionStatus.DoneUnseen && a.StatusAt == T0.AddSeconds(12), "apply+ts: a newer event changes the status");

        // No ts: as before.
        r.Apply(Ev(NotifyEventKind.Running, "a"), T0.AddSeconds(20));
        a = r.Snapshot()[0];
        Check(a.Status == SessionStatus.Running && a.StatusAt == T0.AddSeconds(20), "apply+ts: an event without ts always changes the status, at the arrival time");
        r.Apply(Ev(NotifyEventKind.Done, "a"), T0.AddSeconds(21));
        Check(r.Snapshot()[0].Status == SessionStatus.DoneUnseen, "apply+ts: ... again");

        // Started is weak: StatusAt is MinValue, and it is not 'worked'.
        var s = new SessionRegistry();
        s.Apply(EvAt(NotifyEventKind.Started, "s", T0), T0);
        var si = s.Snapshot()[0];
        Check(si.StatusAt == DateTimeOffset.MinValue && !si.Worked && si.Status == SessionStatus.Running, "apply+ts: Started -> StatusAt MinValue, not worked");
        foreach (var kind in new[] { NotifyEventKind.Running, NotifyEventKind.Ask, NotifyEventKind.Done })
        {
            var w = new SessionRegistry();
            w.Apply(EvAt(NotifyEventKind.Started, "w", T0), T0);
            w.Apply(EvAt(kind, "w", T0.AddSeconds(1)), T0.AddSeconds(2));
            Check(w.Snapshot()[0].Worked && w.Snapshot()[0].StatusAt == T0.AddSeconds(1), "apply+ts: " + kind + " marks the session as worked");
        }

        // A ts too far ahead (more than 5 seconds) is replaced by 'now'; a small lead is kept.
        var f = new SessionRegistry();
        f.Apply(EvAt(NotifyEventKind.Running, "f", T0.AddHours(1)), T0);
        Check(f.Snapshot()[0].StatusAt == T0, "apply+ts: a ts more than 5 seconds ahead uses 'now'");
        var g = new SessionRegistry();
        g.Apply(EvAt(NotifyEventKind.Running, "g", T0.AddSeconds(3)), T0);
        Check(g.Snapshot()[0].StatusAt == T0.AddSeconds(3), "apply+ts: a ts 3 seconds ahead is kept");

        // After a Started event (StatusAt MinValue) any older-looking real event still lands.
        var h = new SessionRegistry();
        h.Apply(EvAt(NotifyEventKind.Started, "h", T0.AddSeconds(50)), T0.AddSeconds(50));
        h.Apply(EvAt(NotifyEventKind.Done, "h", T0.AddSeconds(40)), T0.AddSeconds(51));
        Check(h.Snapshot()[0].Status == SessionStatus.DoneUnseen, "apply+ts: nothing is older than a Started event's MinValue");
    }

    private static void VisitSetsStatusAt()
    {
        var r = new SessionRegistry();
        r.Apply(EvAt(NotifyEventKind.Done, "d", T0.AddSeconds(10)), T0.AddSeconds(13));
        r.Visit("d", T0.AddSeconds(20));
        var d = r.Snapshot()[0];
        Check(d.Status == SessionStatus.DoneSeen && d.StatusAt == T0.AddSeconds(20), "visit+ts: a visit that changes the status sets StatusAt");
        // A done notice from before the click arrives late: the pet must not come back out.
        r.Apply(EvAt(NotifyEventKind.Done, "d", T0.AddSeconds(15)), T0.AddSeconds(25));
        Check(r.Snapshot()[0].Status == SessionStatus.DoneSeen, "visit+ts: an older done notice does not bring the pet out again");
        r.Apply(EvAt(NotifyEventKind.Done, "d", T0.AddSeconds(30)), T0.AddSeconds(33));
        Check(r.Snapshot()[0].Status == SessionStatus.DoneUnseen, "visit+ts: a done notice from after the click does");

        var q = new SessionRegistry();
        q.Apply(EvAt(NotifyEventKind.Ask, "q", T0.AddSeconds(10)), T0.AddSeconds(11));
        q.Visit("q", T0.AddSeconds(20));
        q.Apply(EvAt(NotifyEventKind.Ask, "q", T0.AddSeconds(12)), T0.AddSeconds(22));
        Check(q.Snapshot()[0].Status == SessionStatus.Running, "visit+ts: an older ask notice does not bring the question back");

        var n = new SessionRegistry();
        n.Apply(EvAt(NotifyEventKind.Running, "n", T0.AddSeconds(10)), T0.AddSeconds(11));
        n.Visit("n", T0.AddSeconds(50));
        Check(n.Snapshot()[0].StatusAt == T0.AddSeconds(10), "visit+ts: a visit that changes nothing does not touch StatusAt");

        var plain = new SessionRegistry();
        plain.Apply(Ev(NotifyEventKind.Done, "p"), T0);
        var before = DateTimeOffset.Now;
        plain.Visit("p");
        Check(plain.Snapshot()[0].StatusAt >= before, "visit+ts: Visit(id) uses the current time");
    }

    private static LiveSessionEntry LiveE(string id, int pid, string status, DateTimeOffset? statusAt, string cwd = @"C:\x\proj") =>
        new LiveSessionEntry { Pid = pid, SessionId = id, Cwd = cwd, Kind = "interactive", Status = status, StatusUpdatedAt = statusAt };

    private static SessionRegistry Hooked(string id, NotifyEventKind kind, DateTimeOffset at, int pid = 40)
    {
        var r = new SessionRegistry();
        r.Apply(new NotifyEvent { Kind = kind, SessionId = id, Pid = pid, ProjectPath = @"C:\x\proj", At = at }, at);
        return r;
    }

    private static void ReconcileNewestWins()
    {
        // asking (hook) -> the list says busy, newer -> working again
        var r = Hooked("a", NotifyEventKind.Ask, T0);
        int changes = 0;
        r.Changed += () => changes++;
        Check(r.Reconcile(LiveE("a", 40, "busy", T0.AddSeconds(5)), T0.AddSeconds(6)), "newest: a newer busy changes an asking session");
        var a = r.Snapshot()[0];
        Check(a.Status == SessionStatus.Running && a.Worked && a.StatusAt == T0.AddSeconds(5), "newest: asking -> Running, StatusAt follows the list");
        Check(changes == 1, "newest: Changed once");
        Check(!r.Reconcile(LiveE("a", 40, "busy", T0.AddSeconds(5)), T0.AddSeconds(7)) && changes == 1, "newest: the same list state again changes nothing");

        // working -> waiting -> asking
        var w = Hooked("w", NotifyEventKind.Running, T0);
        Check(w.Reconcile(LiveE("w", 40, "waiting", T0.AddSeconds(5)), T0.AddSeconds(6)) && w.Snapshot()[0].Status == SessionStatus.Asking,
            "newest: a newer waiting turns a working session into asking");
        var sh = Hooked("sh", NotifyEventKind.Ask, T0);
        sh.Reconcile(LiveE("sh", 40, "shell", T0.AddSeconds(5)), T0.AddSeconds(6));
        Check(sh.Snapshot()[0].Status == SessionStatus.Running, "newest: shell counts as working");

        // working (worked) -> idle -> finished, not yet seen
        var d = Hooked("d", NotifyEventKind.Running, T0);
        Check(d.Reconcile(LiveE("d", 40, "idle", T0.AddSeconds(5)), T0.AddSeconds(6)) && d.Snapshot()[0].Status == SessionStatus.DoneUnseen,
            "newest: a worked session that goes idle -> DoneUnseen");
        var dq = Hooked("dq", NotifyEventKind.Ask, T0);
        dq.Reconcile(LiveE("dq", 40, "idle", T0.AddSeconds(5)), T0.AddSeconds(6));
        Check(dq.Snapshot()[0].Status == SessionStatus.DoneUnseen, "newest: an asking session that goes idle -> DoneUnseen");

        // started only (never worked) -> idle -> quietly at rest
        var s = Hooked("s", NotifyEventKind.Started, T0);
        Check(!s.Snapshot()[0].Worked, "newest: a Started-only session has not worked");
        Check(s.Reconcile(LiveE("s", 40, "idle", T0.AddSeconds(5)), T0.AddSeconds(6)) && s.Snapshot()[0].Status == SessionStatus.DoneSeen,
            "newest: a session that never worked and goes idle just stands (DoneSeen), no 'finished' call");

        // finished (unseen) -> idle: nothing
        var u = Hooked("u", NotifyEventKind.Done, T0);
        Check(!u.Reconcile(LiveE("u", 40, "idle", T0.AddSeconds(5)), T0.AddSeconds(6)) && u.Snapshot()[0].Status == SessionStatus.DoneUnseen,
            "newest: DoneUnseen + idle stays DoneUnseen");
        var sn = Hooked("sn", NotifyEventKind.Done, T0);
        sn.Visit("sn", T0.AddSeconds(1));
        Check(!sn.Reconcile(LiveE("sn", 40, "idle", T0.AddSeconds(5)), T0.AddSeconds(6)) && sn.Snapshot()[0].Status == SessionStatus.DoneSeen,
            "newest: DoneSeen + idle stays DoneSeen");

        // not newer: nothing
        var e = Hooked("e", NotifyEventKind.Running, T0.AddSeconds(10));
        Check(!e.Reconcile(LiveE("e", 40, "waiting", T0.AddSeconds(10)), T0.AddSeconds(11)) && e.Snapshot()[0].Status == SessionStatus.Running,
            "newest: a list time equal to StatusAt changes nothing");
        Check(!e.Reconcile(LiveE("e", 40, "waiting", T0.AddSeconds(5)), T0.AddSeconds(12)) && e.Snapshot()[0].Status == SessionStatus.Running,
            "newest: an older list time changes nothing");
        Check(!e.Reconcile(LiveE("e", 40, "waiting", null), T0.AddSeconds(13)) && e.Snapshot()[0].Status == SessionStatus.Running,
            "newest: no statusUpdatedAt -> nothing");
        Check(!e.Reconcile(LiveE("e", 40, "thinking", T0.AddSeconds(20)), T0.AddSeconds(14)) && e.Snapshot()[0].Status == SessionStatus.Running,
            "newest: an unknown status -> nothing");
        Check(!e.Reconcile(LiveE("e", 40, "", T0.AddSeconds(20)), T0.AddSeconds(15)) && e.Snapshot()[0].Status == SessionStatus.Running,
            "newest: an empty status -> nothing");
        Check(e.Snapshot()[0].StatusAt == T0.AddSeconds(10), "newest: ... and StatusAt is untouched in those cases");

        // A list state equal to the current status still moves StatusAt, so an older hook cannot undo it.
        var m = Hooked("m", NotifyEventKind.Running, T0.AddSeconds(1));
        Check(!m.Reconcile(LiveE("m", 40, "busy", T0.AddSeconds(5)), T0.AddSeconds(6)), "newest: busy over Running changes no status (false)");
        m.Apply(EvAt(NotifyEventKind.Done, "m", T0.AddSeconds(3)), T0.AddSeconds(7));
        Check(m.Snapshot()[0].Status == SessionStatus.Running, "newest: ... but an older hook then no longer overrides it");

        // The hook that comes after the list's idle (its ts is later) still lands.
        var l = Hooked("l", NotifyEventKind.Running, T0);
        l.Reconcile(LiveE("l", 40, "idle", T0.AddSeconds(5)), T0.AddSeconds(6));
        l.Apply(EvAt(NotifyEventKind.Done, "l", T0.AddSeconds(6)), T0.AddSeconds(9));
        Check(l.Snapshot()[0].Status == SessionStatus.DoneUnseen, "newest: the Stop hook after the list's idle keeps it DoneUnseen");
    }

    private static void ReconcileNewSessions()
    {
        var r = new SessionRegistry();
        r.Reconcile(LiveE("b", 1, "busy", T0.AddSeconds(5)), T0.AddSeconds(9));
        r.Reconcile(LiveE("w", 2, "waiting", T0.AddSeconds(6)), T0.AddSeconds(9));
        r.Reconcile(LiveE("i", 3, "idle", T0.AddSeconds(7)), T0.AddSeconds(9));
        r.Reconcile(LiveE("n", 4, "idle", null), T0.AddSeconds(9));
        var snap = r.Snapshot();
        var b = snap.First(s => s.Id == "b"); var w = snap.First(s => s.Id == "w");
        var i = snap.First(s => s.Id == "i"); var n = snap.First(s => s.Id == "n");
        Check(b.Status == SessionStatus.Running && b.Worked && b.StatusAt == T0.AddSeconds(5), "new: busy -> Running, worked, StatusAt from the list");
        Check(w.Status == SessionStatus.Asking && w.Worked && w.StatusAt == T0.AddSeconds(6), "new: waiting -> Asking, worked");
        Check(i.Status == SessionStatus.DoneSeen && !i.Worked && i.StatusAt == T0.AddSeconds(7), "new: idle -> DoneSeen, not worked");
        Check(n.StatusAt == T0.AddSeconds(9), "new: no statusUpdatedAt -> StatusAt is now");

        // A session that has not worked goes busy, then idle: it worked, so DoneUnseen.
        r.Reconcile(LiveE("i", 3, "busy", T0.AddSeconds(20)), T0.AddSeconds(21));
        Check(r.Snapshot().First(s => s.Id == "i").Status == SessionStatus.Running, "new: a resting session that starts working shows as Running");
        r.Reconcile(LiveE("i", 3, "idle", T0.AddSeconds(30)), T0.AddSeconds(31));
        Check(r.Snapshot().First(s => s.Id == "i").Status == SessionStatus.DoneUnseen, "new: ... and when it rests again, DoneUnseen");
    }

    private static void ReconcileThenStageWalks()
    {
        double dist = PetStage.HomeX(0) - PetStage.FrontX(0);

        // asking (hook) -> list busy: the pet walks back and types
        var r = Hooked("a", NotifyEventKind.Ask, T0);
        var stage = new PetStage();
        void Sync() => stage.Sync(PetSelection.Select(r.Snapshot()).Displayed);
        Sync();
        stage.Advance(1e9);
        var a = stage.Actors[0];
        Check(a.AtFront && a.Anim == "ask", "list+stage: the asking pet is out in front");
        r.Reconcile(LiveE("a", 40, "busy", T0.AddSeconds(5)), T0.AddSeconds(6));
        Sync();
        stage.Advance(100);
        Check(a.Anim == "walk" && !a.FacingLeft, "list+stage: after the list says busy it walks back");
        stage.Advance(dist / PetStage.WalkSpeed * 1000);
        Check(a.X == PetStage.HomeX(0) && a.Anim == "typing", "list+stage: and sits typing");

        // working -> list waiting: the pet walks out and asks
        r.Reconcile(LiveE("a", 40, "waiting", T0.AddSeconds(10)), T0.AddSeconds(11));
        Sync();
        stage.Advance(100);
        Check(a.Anim == "walk" && a.FacingLeft, "list+stage: after the list says waiting it walks out");
        stage.Advance(dist / PetStage.WalkSpeed * 1000);
        Check(a.X == PetStage.FrontX(0) && a.Anim == "ask" && a.AtFront, "list+stage: and asks");

        // a discovered idle session just stands at its seat
        var r2 = new SessionRegistry();
        r2.Reconcile(LiveE("i", 5, "idle", T0), T0);
        var stage2 = new PetStage();
        stage2.Sync(PetSelection.Select(r2.Snapshot()).Displayed);
        stage2.Advance(1e9);
        Check(stage2.Actors[0].Anim == "idle" && stage2.Actors[0].X == PetStage.HomeX(0), "list+stage: a new idle session stands at its seat");
    }

    // ---- left-facing pictures (#4), fleeing (#11), birth and leaving (#12) ----

    private static string AnimJson(string name, int frames) =>
        "\"" + name + "\":{\"file\":\"" + name + ".png\",\"ms\":[" + string.Join(",", Enumerable.Repeat("100", frames)) + "]}";

    /// <summary>
    /// A sheet with the required animations plus the chosen optional ones
    /// (spawn/vanish: that many frames of 100 ms, 0 = none; startle: 3 frames = 300 ms).
    /// </summary>
    private static PetSheet SheetWith(bool walkL, bool run, bool runL, int spawn = 0, int vanish = 0, bool startle = false)
    {
        var parts = new List<string> { PetAnimsJson() };
        if (walkL) parts.Add(AnimJson("walkL", 2));
        if (run) parts.Add(AnimJson("run", 2));
        if (runL) parts.Add(AnimJson("runL", 2));
        if (spawn > 0) parts.Add(AnimJson("spawn", spawn));
        if (vanish > 0) parts.Add(AnimJson("vanish", vanish));
        if (startle) parts.Add(AnimJson("startle", 3));
        return PetSheet.Parse(PetJson(string.Join(",", parts)));
    }

    private static PetStage StageWith(PetSheet? sheet)
    {
        var st = new PetStage();
        st.UseSheet(sheet);
        return st;
    }

    private static void PetSheetOptionalReal()
    {
        var petDir = FindPetDir();
        if (petDir == null) { Check(false, "pet optional: pet folder found"); return; }
        var sheet = PetSheet.Load(Path.Combine(petDir, "pet.json"));
        foreach (var name in new[] { "walkL", "run", "runL", "spawn", "vanish", "startle" })
        {
            Check(sheet.Optional.TryGetValue(name, out var anim) && anim.Id == name && sheet.Find(name) == anim,
                "pet optional: pet.json has '" + name + "'");
            if (!sheet.Optional.TryGetValue(name, out var an)) continue;
            var png = Path.Combine(petDir, an.File);
            Check(File.Exists(png), "pet optional: " + an.File + " exists");
            if (!File.Exists(png)) continue;
            var head = new byte[24];
            using (var fs = File.OpenRead(png)) fs.Read(head, 0, head.Length);
            bool isPng = head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47;
            int width = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
            int height = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
            Check(isPng && width == an.Ms.Count * 32 && height == 32, "pet optional: " + an.File + " is a PNG, frames x 32 wide, 32 tall");
        }
        Check(sheet.Anims.Count == 5, "pet optional: the required animations stay five");
        Check(sheet.Find("typing") != null && sheet.Find("nope") == null, "pet optional: Find looks at required and optional");

        // The real sheet makes the stage use the left pictures, the birth/leaving, the startle and the run.
        var st = StageWith(sheet);
        var a = Sess("a");
        st.Sync(new[] { a }, announceNew: false);
        a.Status = SessionStatus.Asking;
        st.Sync(new[] { a });
        st.Advance(100);
        Check(st.Actors[0].Anim == "walkL" && !st.Actors[0].Flip, "pet optional: with the real sheet a pet going left uses walkL");
        a.Status = SessionStatus.Running;
        st.Sync(new[] { a });
        st.Advance(1e9);
        st.SetFleeing(true);
        Check(st.Actors[0].Anim == "startle" && st.Actors[0].Startling, "pet optional: with the real sheet the pets startle first");
        st.Advance(sheet.Find("startle")!.TotalMs);
        Check(st.Actors[0].Anim == "run" || st.Actors[0].Anim == "runL", "pet optional: and then run");
        st.SetFleeing(false);
        st.Advance(1e9);

        var born = StageWith(sheet);
        var b = Sess("b");
        born.Sync(new[] { b }, announceNew: true);
        Check(born.Actors[0].Anim == "spawn", "pet optional: with the real sheet a new pet plays spawn");
        born.Advance(sheet.Find("spawn")!.TotalMs);
        Check(born.Actors[0].Anim == "typing", "pet optional: ... once, then types");
        born.Sync(new SessionInfo[0]);
        Check(born.Actors.Count == 1 && born.Actors[0].Anim == "vanish", "pet optional: with the real sheet a pet whose session is gone plays vanish");
        born.Advance(sheet.Find("vanish")!.TotalMs);
        Check(born.Actors.Count == 0, "pet optional: ... and is gone when it is over");
    }

    private static void PetSheetOptionalParse()
    {
        string msg;
        var plain = PetSheet.Parse(PetJson(PetAnimsJson()));
        Check(plain.Optional.Count == 0 && plain.Find("walkL") == null && plain.AllAnims.Count() == 5,
            "pet optional: an old sheet without them parses and has none");
        var all = SheetWith(true, true, true, 3, 4);
        Check(all.Optional.Count == 5 && all.Anims.Count == 5 && all.AllAnims.Count() == 10, "pet optional: all five optional animations are read");
        Check(all.Find("spawn")!.TotalMs == 300 && all.Find("vanish")!.TotalMs == 400, "pet optional: spawn and vanish lengths are read");
        Check(SheetWith(true, true, true, 3, 4, startle: true).Find("startle")!.TotalMs == 300, "pet optional: startle is read");
        Check(Throws(() => PetSheet.Parse(PetJson(PetAnimsJson() + ",\"walkL\":{\"file\":\"a.png\",\"ms\":[]}")), out msg) && msg.Contains("walkL"),
            "pet optional: a listed optional animation with empty ms is rejected");
        Check(Throws(() => PetSheet.Parse(PetJson(PetAnimsJson() + ",\"vanish\":5")), out msg) && msg.Contains("vanish"),
            "pet optional: a listed optional animation that is not an object is rejected");
        Check(Throws(() => PetSheet.Parse(PetJson(PetAnimsJson() + ",\"run\":{\"file\":\"../x.png\",\"ms\":[100]}")), out msg) && msg.Contains("run"),
            "pet optional: a file name with a folder is rejected");
        Check(!PetSheet.RequiredAnims.Contains("walkL") && !PetSheet.RequiredAnims.Contains("spawn") &&
              !PetSheet.RequiredAnims.Contains("vanish") && !PetSheet.RequiredAnims.Contains("startle"),
            "pet optional: walkL, spawn, vanish and startle are not required");
    }

    private static void LeftPictures()
    {
        // With walkL: no mirroring.
        var st = StageWith(SheetWith(true, false, false));
        var a = Sess("a");
        st.Sync(new[] { a });
        a.Status = SessionStatus.Asking;
        st.Sync(new[] { a });
        st.Advance(100);
        var pa = st.Actors[0];
        Check(pa.Anim == "walkL" && pa.FacingLeft && !pa.Flip, "left: going left with walkL uses walkL and does not mirror");
        st.Advance(1e9);
        Check(pa.Anim == "ask" && !pa.Flip && !pa.FacingLeft, "left: arrived, the ask picture is not mirrored");
        a.Status = SessionStatus.Running;
        st.Sync(new[] { a });
        st.Advance(100);
        Check(pa.Anim == "walk" && !pa.FacingLeft && !pa.Flip, "left: going right uses walk, not mirrored");

        // Without walkL (an old sheet): the picture is mirrored, as before.
        var old = StageWith(SheetWith(false, false, false));
        var b = Sess("b", SessionStatus.Asking);
        old.Sync(new[] { b });
        old.Advance(100);
        var pb = old.Actors[0];
        Check(pb.Anim == "walk" && pb.FacingLeft && pb.Flip, "left: no walkL in the sheet -> walk, mirrored");
        b.Status = SessionStatus.Running;
        old.Sync(new[] { b });
        old.Advance(1e9);
        Check(pb.Anim == "typing" && !pb.Flip, "left: at the seat nothing is mirrored");

        // A stage that was never given a sheet behaves like the old one.
        var none = new PetStage();
        var c = Sess("c", SessionStatus.Asking);
        none.Sync(new[] { c });
        none.Advance(100);
        Check(none.Actors[0].Anim == "walk" && none.Actors[0].FacingLeft && none.Actors[0].Flip, "left: a stage without a sheet mirrors the walk");
    }

    private static string UsageJson(string five, string seven = "{\"used_percentage\":20,\"resets_at\":9999999999}") =>
        "{\"model\":{\"id\":\"x\"},\"rate_limits\":{\"five_hour\":" + five + ",\"seven_day\":" + seven + "}}";

    private static void UsageReading()
    {
        long now = T0.ToUnixTimeSeconds();
        string Five(string pct, string? resets) =>
            "{\"used_percentage\":" + pct + (resets == null ? "" : ",\"resets_at\":" + resets) + "}";

        Check(UsageReader.Parse(UsageJson(Five("89", (now + 3600).ToString())), T0) == 89, "usage: a normal file gives the five-hour percentage");
        Check(UsageReader.Parse(UsageJson(Five("91.5", (now + 3600).ToString())), T0) == 91.5, "usage: a fraction is kept");
        Check(UsageReader.Parse(UsageJson(Five("95", (now - 1).ToString())), T0) == 0, "usage: resets_at in the past counts as 0");
        Check(UsageReader.Parse(UsageJson(Five("95", now.ToString())), T0) == 0, "usage: resets_at exactly now counts as 0");
        Check(UsageReader.Parse(UsageJson(Five("95", (now + 1).ToString())), T0) == 95, "usage: resets_at one second ahead still counts");
        Check(UsageReader.Parse(UsageJson(Five("95", null)), T0) == 95, "usage: without resets_at the percentage is taken as it is");
        Check(UsageReader.Parse(UsageJson(Five("0", (now + 5).ToString())), T0) == 0, "usage: 0 is a known value");
        Check(UsageReader.Parse(UsageJson(Five("100", (now + 5).ToString())), T0) == 100, "usage: 100 is a known value");
        Check(UsageReader.Parse(UsageJson(Five("95", (now + 3600).ToString()), "{\"used_percentage\":1}"), T0) == 95,
            "usage: the seven-day window is not used");

        Check(UsageReader.Parse("{ not json", T0) == null, "usage: broken JSON is unknown");
        Check(UsageReader.Parse("", T0) == null, "usage: an empty file is unknown");
        Check(UsageReader.Parse("[]", T0) == null, "usage: a non-object is unknown");
        Check(UsageReader.Parse("{}", T0) == null, "usage: no rate_limits is unknown");
        Check(UsageReader.Parse("{\"rate_limits\":{}}", T0) == null, "usage: no five_hour is unknown");
        Check(UsageReader.Parse("{\"rate_limits\":{\"five_hour\":{}}}", T0) == null, "usage: no used_percentage is unknown");
        Check(UsageReader.Parse(UsageJson("{\"used_percentage\":\"90\",\"resets_at\":" + (now + 9) + "}"), T0) == null, "usage: a string percentage is unknown");
        Check(UsageReader.Parse(UsageJson(Five("-1", null)), T0) == null, "usage: a negative percentage is unknown");
        Check(UsageReader.Parse(UsageJson(Five("101", null)), T0) == null, "usage: a percentage over 100 is unknown");
        Check(UsageReader.Parse(UsageJson("null"), T0) == null, "usage: five_hour null is unknown");

        Check(UsageReader.ShouldFlee(90, 90) && UsageReader.ShouldFlee(95, 90), "usage: at and above the threshold the pets flee");
        Check(!UsageReader.ShouldFlee(89.99, 90) && !UsageReader.ShouldFlee(0, 90), "usage: below the threshold they do not");
        Check(!UsageReader.ShouldFlee(null, 90) && !UsageReader.ShouldFlee(null, 0), "usage: unknown never makes them flee");
        Check(UsageReader.ShouldFlee(0, 0) && UsageReader.ShouldFlee(100, 100), "usage: threshold 0 and 100 work at their edges");

        var dir = Path.Combine(Path.GetTempPath(), "ctb-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "statusline-last.json");
            Check(UsageReader.ReadFile(path, T0) == null, "usage file: a missing file is unknown");
            File.WriteAllText(path, UsageJson(Five("93", (now + 600).ToString())));
            Check(UsageReader.ReadFile(path, T0) == 93, "usage file: a good file is read");
            Check(UsageReader.ReadFile(path, T0.AddSeconds(601)) == 0, "usage file: the same file after resets_at counts as 0");
            File.WriteAllText(path, "{ \"rate_limits\": ");
            Check(UsageReader.ReadFile(path, T0) == null, "usage file: a half-written file is unknown");
            Check(UsageReader.ReadFile(dir, T0) == null, "usage file: a folder is unknown, not a crash");
            Check(UsageReader.ReadFile("", T0) == null, "usage file: an empty path is unknown");
            Check(UsageReader.DefaultPath().EndsWith("statusline-last.json") && UsageReader.DefaultPath().Contains(".claude"),
                "usage file: the default is ~/.claude/statusline-last.json");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void ConfigFlee()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ctb-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            var d = AppConfig.Load(path);
            Check(d.FleeEnabled && d.FleeThreshold == 90 && d.UsageFile == UsageReader.DefaultPath(), "flee config: defaults without a file");
            File.WriteAllText(path, "{\"port\": 9999}");
            d = AppConfig.Load(path);
            Check(d.FleeEnabled && d.FleeThreshold == 90 && d.UsageFile == UsageReader.DefaultPath(), "flee config: defaults without the keys");
            File.WriteAllText(path, "{\"fleeThreshold\": 75, \"fleeEnabled\": false, \"usageFile\": \"D:\\\\u\\\\usage.json\"}");
            d = AppConfig.Load(path);
            Check(d.FleeThreshold == 75 && !d.FleeEnabled && d.UsageFile == @"D:\u\usage.json", "flee config: all three are honored");
            foreach (var v in new[] { "0", "100", "89.5" })
            {
                File.WriteAllText(path, "{\"fleeThreshold\": " + v + "}");
                Check(AppConfig.Load(path).FleeThreshold == double.Parse(v, System.Globalization.CultureInfo.InvariantCulture), "flee config: threshold " + v + " is honored");
            }
            foreach (var v in new[] { "-1", "101", "1000", "\"80\"", "null", "true" })
            {
                File.WriteAllText(path, "{\"fleeThreshold\": " + v + "}");
                Check(AppConfig.Load(path).FleeThreshold == 90, "flee config: threshold " + v + " -> 90");
            }
            foreach (var v in new[] { "\"false\"", "0", "null", "[]" })
            {
                File.WriteAllText(path, "{\"fleeEnabled\": " + v + "}");
                Check(AppConfig.Load(path).FleeEnabled, "flee config: fleeEnabled " + v + " -> true");
            }
            foreach (var v in new[] { "\"\"", "\"  \"", "5", "null" })
            {
                File.WriteAllText(path, "{\"usageFile\": " + v + "}");
                Check(AppConfig.Load(path).UsageFile == UsageReader.DefaultPath(), "flee config: usageFile " + v + " -> default");
            }
            File.WriteAllText(path, "{\"usageFile\": \"~/x/u.json\"}");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Check(AppConfig.Load(path).UsageFile == Path.Combine(home, "x/u.json"), "flee config: a leading ~ means the user folder");
            File.WriteAllText(path, "{\"port\": 9999, \"petScale\": 4, \"fleeThreshold\": 70}");
            var all = AppConfig.Load(path);
            Check(all.Port == 9999 && all.PetScale == 4 && all.FleeThreshold == 70, "flee config: the older keys still work alongside");
            File.WriteAllText(path, "{ broken");
            Check(AppConfig.Load(path).FleeEnabled && AppConfig.Load(path).FleeThreshold == 90, "flee config: a broken file -> defaults");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void FleeBehaviour()
    {
        Check(PetStage.FleeSpeed == 90, "flee: the speed is 90 dots per second");
        Check(PetStage.FleeMinX == 16 && PetStage.FleeMaxX == PetStage.WidthDots - 16, "flee: the run covers the stage minus half a picture each side");

        var st = StageWith(SheetWith(true, true, true));
        var a = Sess("a"); var b = Sess("b");
        st.Sync(new[] { a, b });
        st.Advance(500);
        Check(!st.Fleeing && st.Actors[0].Anim == "typing", "flee: nothing runs until told to");
        st.SetFleeing(true);
        Check(st.Fleeing, "flee: SetFleeing(true) is remembered");
        st.Advance(1000);
        var pa = st.Actors[0]; var pb = st.Actors[1];
        Check(pa.Fleeing && pa.Anim == "runL" && pa.FacingLeft && !pa.Flip && !pa.AtFront && Math.Abs(pa.X - 167) < 1e-6,
            "flee: a working pet runs left at 90 dots per second (runL, no mirror)");
        Check(pb.Fleeing && Math.Abs(pb.X - 141) < 1e-6, "flee: every working pet runs, from its own place");
        st.Advance(2000);
        Check(pa.Anim == "run" && !pa.FacingLeft && Math.Abs(pa.X - 45) < 1e-6,
            "flee: it turned at the left end and runs right (run)");
        st.Advance(2000);
        Check(Math.Abs(pa.X - 225) < 1e-6 && !pa.FacingLeft, "flee: and keeps going right");
        st.Advance(1000);
        Check(pa.FacingLeft && pa.Anim == "runL" && Math.Abs(pa.X - (258 - (225 + 90 - 258))) < 1e-6, "flee: it turned at the right end and runs left again");

        // Always inside the stage, whatever the steps are.
        bool inside = true, names = true, sawLeft = false, sawRight = false;
        var rnd = new Random(7);
        for (int i = 0; i < 400; i++)
        {
            st.Advance(rnd.Next(1, 120));
            foreach (var p in st.Actors)
            {
                if (p.X < PetStage.FleeMinX - 1e-9 || p.X > PetStage.FleeMaxX + 1e-9) inside = false;
                if (p.Anim != "run" && p.Anim != "runL") names = false;
                if (p.Anim == "run" && p.FacingLeft) names = false;
                if (p.Anim == "runL" && !p.FacingLeft) names = false;
                if (p.FacingLeft) sawLeft = true; else sawRight = true;
            }
        }
        Check(inside, "flee: the pets never leave the stage");
        Check(names && sawLeft && sawRight, "flee: run goes with right, runL with left, and both directions happen");
        st.Advance(1e9);
        Check(pa.X >= PetStage.FleeMinX && pa.X <= PetStage.FleeMaxX && pa.Fleeing, "flee: a huge step stays inside the stage");

        // A call for attention comes first.
        var st2 = StageWith(SheetWith(true, true, true));
        var c = Sess("c"); var d = Sess("d");
        st2.Sync(new[] { c, d });
        st2.SetFleeing(true);
        st2.Advance(1000);
        var pc = st2.Actors[0]; var pd = st2.Actors[1];
        Check(pc.Fleeing && Math.Abs(pc.X - 167) < 1e-6, "flee+ask: before the call both run");
        c.Status = SessionStatus.Asking;
        st2.Sync(new[] { c, d });
        st2.Advance(100);
        Check(!pc.Fleeing && pc.Anim == "walkL" && pc.FacingLeft && Math.Abs(pc.X - 164) < 1e-6,
            "flee+ask: an asking pet stops running and walks to its front spot");
        Check(pd.Fleeing && (pd.Anim == "run" || pd.Anim == "runL"), "flee+ask: the others keep running");
        st2.Advance(3000);
        Check(pc.X == PetStage.FrontX(0) && pc.Anim == "ask" && pc.AtFront && !pc.Muttering, "flee+ask: it arrives and asks (the call's bubble shows) while the usage is high");
        st2.Advance(5000);
        Check(pc.X == PetStage.FrontX(0) && pc.AtFront, "flee+ask: and stays there");
        d.Status = SessionStatus.DoneUnseen;
        st2.Sync(new[] { c, d });
        st2.Advance(1e9);
        Check(pd.X == PetStage.FrontX(1) && pd.Anim == "done" && pd.AtFront && !pd.Fleeing, "flee+done: an unseen finish comes out and dances too");

        // Back to work while the usage is high: runs again.
        c.Status = SessionStatus.Running;
        st2.Sync(new[] { c, d });
        st2.Advance(100);
        Check(pc.Fleeing && !pc.AtFront && (pc.Anim == "run" || pc.Anim == "runL"), "flee: a pet that goes back to work runs again");
        // Seen = idle pets run too.
        d.Status = SessionStatus.DoneSeen;
        st2.Sync(new[] { c, d });
        st2.Advance(100);
        Check(pd.Fleeing && !pd.AtFront, "flee: an acknowledged (idle) pet runs too");

        // Usage went down: walk home.
        st2.SetFleeing(false);
        double xBefore = pc.X;
        st2.Advance(100);
        Check(!pc.Fleeing && pc.Anim == "walk" && !pc.FacingLeft && Math.Abs(pc.X - (xBefore + 3)) < 1e-6,
            "flee end: the pet walks (30 dots per second) toward its seat");
        st2.Advance(1e9);
        Check(pc.X == PetStage.HomeX(0) && pc.Anim == "typing" && !pc.Fleeing, "flee end: it sits at its seat typing again");
        Check(pd.X == PetStage.HomeX(1) && pd.Anim == "idle", "flee end: the idle pet is home and idle");

        // Walk speed on the way home is the walking speed, not the running speed.
        var st3 = StageWith(SheetWith(true, true, true));
        st3.Sync(new[] { Sess("e") });
        st3.SetFleeing(true);
        st3.Advance(2000);
        double x0 = st3.Actors[0].X;
        st3.SetFleeing(false);
        st3.Advance(1000);
        Check(Math.Abs(Math.Abs(st3.Actors[0].X - x0) - PetStage.WalkSpeed) < 1e-6, "flee end: back at walking speed");

        // Without a run picture nothing runs.
        var st4 = StageWith(SheetWith(true, false, false));
        st4.Sync(new[] { Sess("f") });
        st4.SetFleeing(true);
        st4.Advance(1000);
        Check(st4.Actors[0].X == PetStage.HomeX(0) && st4.Actors[0].Anim == "typing" && !st4.Actors[0].Fleeing,
            "flee: no run in the sheet -> the pets stay at their seats");
        var st5 = new PetStage(); // no sheet at all
        st5.Sync(new[] { Sess("g") });
        st5.SetFleeing(true);
        st5.Advance(1000);
        Check(st5.Actors[0].X == PetStage.HomeX(0) && st5.Actors[0].Anim == "typing", "flee: a stage without a sheet does not run");

        // run without runL: mirrored when going left.
        var st6 = StageWith(SheetWith(true, true, false));
        st6.Sync(new[] { Sess("h") });
        st6.SetFleeing(true);
        st6.Advance(1000);
        var ph = st6.Actors[0];
        Check(ph.Anim == "run" && ph.FacingLeft && ph.Flip, "flee: run without runL is mirrored going left");
        st6.Advance(2000);
        Check(ph.Anim == "run" && !ph.FacingLeft && !ph.Flip, "flee: ... and not mirrored going right");

        // A pet that stands at the very left end starts out to the right.
        var st7 = StageWith(SheetWith(true, true, true));
        var five = new List<SessionInfo>();
        for (int k = 0; k < PetStage.Slots; k++) five.Add(Sess("s" + k));
        five[4].Status = SessionStatus.Asking;
        st7.Sync(five);
        st7.Advance(1e9);
        var p4 = st7.Actors[4];
        Check(p4.X == PetStage.FrontX(4) && p4.AtFront, "flee: (setup) the last seat's pet is out at the left end");
        five[4].Status = SessionStatus.Running;
        st7.Sync(five);
        st7.SetFleeing(true);
        st7.Advance(100);
        Check(p4.Fleeing && !p4.FacingLeft && p4.Anim == "run" && Math.Abs(p4.X - 24) < 1e-6, "flee: at the left end it bounces and runs right");

        // On then off before any time passes changes nothing.
        var st8 = StageWith(SheetWith(true, true, true));
        st8.Sync(new[] { Sess("z") });
        st8.SetFleeing(true); st8.SetFleeing(false);
        st8.Advance(1000);
        Check(st8.Actors[0].X == PetStage.HomeX(0) && st8.Actors[0].Anim == "typing", "flee: on then off before any time passes changes nothing");
    }

    private static void StartleBehaviour()
    {
        var sheet = SheetWith(true, true, true, startle: true);
        var st = StageWith(sheet);
        var a = Sess("a"); var b = Sess("b", SessionStatus.Asking); var c = Sess("c");
        st.Sync(new[] { a, b, c });
        st.Advance(1e9);
        var pa = st.Actors[0]; var pb = st.Actors[1]; var pc = st.Actors[2];
        Check(pb.AtFront && pb.Anim == "ask", "startle: (setup) one pet is calling out");

        // The moment the usage goes over: the pets that would run startle on the spot.
        st.SetFleeing(true);
        Check(pa.Startling && pa.Anim == "startle" && pa.AnimMs == 0 && pa.X == PetStage.HomeX(0) && pa.Fleeing,
            "startle: when fleeing starts, a working pet plays the startle where it stands");
        Check(pc.Startling && pc.Anim == "startle", "startle: every pet that will run does");
        Check(!pb.Startling && pb.Anim == "ask" && pb.AtFront, "startle: a pet that is calling out does not (its call stays first)");
        st.Advance(200);
        Check(pa.Anim == "startle" && pa.X == PetStage.HomeX(0) && Math.Abs(pa.AnimMs - 200) < 1e-9, "startle: it stays put while it plays (300 ms)");
        st.Advance(100);
        Check(!pa.Startling && pa.Anim == "runL" && pa.AnimMs == 0 && pa.X == PetStage.HomeX(0), "startle: once over it starts running (left first)");
        st.Advance(1000);
        Check(Math.Abs(pa.X - (PetStage.HomeX(0) - 90)) < 1e-6, "startle: and runs at full speed after it");

        // Only once.
        bool again = false;
        st.SetFleeing(true); // no change: no second startle
        for (int i = 0; i < 100; i++) { st.Advance(100); foreach (var p in st.Actors) if (p.Anim == "startle") again = true; }
        Check(!again, "startle: it is played once, not again while the pets keep running");

        // Off and on again: once more.
        st.SetFleeing(false);
        st.Advance(1e9);
        Check(pa.X == PetStage.HomeX(0) && pa.Anim == "typing", "startle: (setup) back at the seats");
        st.SetFleeing(true);
        Check(pa.Startling && pa.Anim == "startle", "startle: a new flee plays it again");

        // Pets that join or come back to work later do not startle.
        var d = Sess("d");
        st.Sync(new[] { a, b, c, d });
        var pd = st.Actors.First(p => p.SessionId == "d");
        Check(!pd.Startling && pd.Anim != "startle" && (pd.Anim == "run" || pd.Anim == "runL"), "startle: a pet that appears while the pets run just runs");
        b.Status = SessionStatus.Running;
        st.Sync(new[] { a, b, c, d });
        st.Advance(100);
        Check(!pb.Startling && pb.Anim != "startle" && pb.Fleeing, "startle: a pet that goes back to work during the run just runs");

        // The usage goes down during the startle: back to walking, no running.
        var st2 = StageWith(sheet);
        st2.Sync(new[] { Sess("e") });
        st2.SetFleeing(true);
        st2.Advance(100);
        st2.SetFleeing(false);
        st2.Advance(100);
        var pe = st2.Actors[0];
        Check(!pe.Startling && !pe.Fleeing && pe.Anim == "typing", "startle: ending the flee during the startle stops it");

        // A call for attention during the startle takes over.
        var st3 = StageWith(sheet);
        var f = Sess("f");
        st3.Sync(new[] { f });
        st3.SetFleeing(true);
        st3.Advance(100);
        f.Status = SessionStatus.Asking;
        st3.Sync(new[] { f });
        st3.Advance(100);
        var pf = st3.Actors[0];
        Check(!pf.Startling && !pf.Fleeing && pf.Anim == "walkL", "startle: a pet that starts asking during the startle walks out");

        // A pet being born does not startle, and runs after the birth.
        var st4 = StageWith(SheetWith(true, true, true, spawn: 4, startle: true));
        st4.Sync(new[] { Sess("g") }, announceNew: true);
        st4.SetFleeing(true);
        var pg = st4.Actors[0];
        Check(!pg.Startling && pg.Anim == "spawn", "startle: a pet that is being born does not startle");
        st4.Advance(400);
        Check(pg.Fleeing && (pg.Anim == "run" || pg.Anim == "runL"), "startle: it runs once born");

        // No startle in the sheet: straight to running.
        var st5 = StageWith(SheetWith(true, true, true));
        st5.Sync(new[] { Sess("h") });
        st5.SetFleeing(true);
        st5.Advance(0);
        Check(!st5.Actors[0].Startling && st5.Actors[0].Anim == "runL", "startle: no startle in the sheet -> it just runs");

        // No run picture: no startle either (nothing to startle into).
        var st6 = StageWith(SheetWith(true, false, false, startle: true));
        st6.Sync(new[] { Sess("i") });
        st6.SetFleeing(true);
        st6.Advance(0);
        Check(!st6.Actors[0].Startling && st6.Actors[0].Anim == "typing", "startle: no run in the sheet -> no startle, no flee");

        // Already fleeing before the pets exist (the app started over the line): no startle.
        var st7 = StageWith(sheet);
        st7.SetFleeing(true);
        st7.Sync(new[] { Sess("j") }, announceNew: false);
        Check(!st7.Actors[0].Startling && st7.Actors[0].Anim == "runL", "startle: pets that exist after flee began (startup over the line) just run");
    }

    private static void MutterBehaviour()
    {
        Check(PetStage.MutterText == "もう無理！", "mutter: the text is 'もう無理！'");
        Check(PetStage.MutterPeriodMs == 12000 && PetStage.MutterShowMs == 2500, "mutter: every 12 seconds, for 2.5 seconds");

        var st = StageWith(SheetWith(true, true, true, startle: true));
        var a = Sess("a"); var b = Sess("b");
        st.Sync(new[] { a, b });
        var pa = st.Actors[0]; var pb = st.Actors[1];
        st.Advance(60000);
        Check(!pa.Muttering && !pb.Muttering, "mutter: nobody mutters while not running");
        st.SetFleeing(true);
        st.Advance(300); // the startle does not count as running
        Check(pa.Anim == "runL" || pa.Anim == "run", "mutter: (setup) the startle is over");

        // Sample every 100 ms of running for 40 seconds.
        var onA = new List<int>(); var onB = new List<int>();
        bool prevA = false, prevB = false;
        var startsA = new List<int>(); var startsB = new List<int>();
        for (int t = 100; t <= 40000; t += 100)
        {
            st.Advance(100);
            if (pa.Muttering) onA.Add(t);
            if (pb.Muttering) onB.Add(t);
            if (pa.Muttering && !prevA) startsA.Add(t);
            if (pb.Muttering && !prevB) startsB.Add(t);
            prevA = pa.Muttering; prevB = pb.Muttering;
        }
        // Seat 0: 12 s cycle, shown in the last 2.5 s -> from 9.5 s, 21.5 s, 33.5 s.
        Check(startsA.SequenceEqual(new[] { 9500, 21500, 33500 }), "mutter: the first pet starts at 9.5 s and then every 12 s (" + string.Join(",", startsA) + ")");
        Check(onA.Count == 3 * 25, "mutter: and each time it lasts 2.5 s (" + onA.Count + " samples of 100 ms)");
        // Seat 1 is 2.4 s ahead.
        Check(startsB.SequenceEqual(new[] { 7100, 19100, 31100 }), "mutter: the second pet is staggered by 2.4 s (" + string.Join(",", startsB) + ")");
        Check(onB.Count == 3 * 25, "mutter: the second pet also shows it for 2.5 s each time");
        int together = 0;
        foreach (var t in onA) if (onB.Contains(t)) together++;
        Check(together <= 3, "mutter: neighbouring pets overlap by at most one 100 ms sample per cycle (" + together + ")");

        // Stops with the run, and does not show for pets calling out.
        st.Advance(9700);
        a.Status = SessionStatus.Asking;
        st.Sync(new[] { a, b });
        st.Advance(1e9);
        Check(!pa.Muttering && pa.AtFront, "mutter: a pet that is calling out does not mutter");
        st.SetFleeing(false);
        st.Advance(1e9);
        Check(!pb.Muttering && !pa.Muttering, "mutter: nobody mutters after the run ends");

        // The clock starts again with the next run.
        st.SetFleeing(true);
        st.Advance(300);
        st.Advance(9400);
        Check(pb.Muttering && !pa.Muttering, "mutter: a new run counts from its own start (the second pet at 9.4 s of running)");
    }

    private static void SpawnBehaviour()
    {
        var sheet = SheetWith(true, true, true, spawn: 4, vanish: 3);
        var st = StageWith(sheet);
        var a = Sess("a");
        st.Sync(new[] { a }, announceNew: true);
        var pa = st.Actors[0];
        Check(pa.Spawning && pa.Anim == "spawn" && pa.AnimMs == 0 && pa.X == PetStage.HomeX(0) && !pa.AtFront && !pa.Flip,
            "spawn: a new session starts with the birth animation at its seat");
        st.Advance(300);
        Check(pa.Spawning && pa.Anim == "spawn" && Math.Abs(pa.AnimMs - 300) < 1e-9, "spawn: it keeps playing for its length (400 ms)");
        st.Advance(100);
        Check(!pa.Spawning && pa.Anim == "typing" && pa.AnimMs == 0 && pa.X == PetStage.HomeX(0), "spawn: after one play it goes on as usual");
        st.Advance(2000);
        Check(pa.Anim == "typing", "spawn: and does not play again");

        // The sessions that exist at startup do not play it; new ones afterwards do.
        var st2 = StageWith(sheet);
        var b = Sess("b"); var c = Sess("c");
        st2.Sync(new[] { b }, announceNew: false);
        Check(!st2.Actors[0].Spawning && st2.Actors[0].Anim == "typing", "spawn: a pet that existed at startup appears without it");
        st2.Sync(new[] { b, c }, announceNew: true);
        Check(!st2.Actors[0].Spawning && st2.Actors[1].Spawning && st2.Actors[1].Anim == "spawn", "spawn: a session that comes later plays it");
        st2.Sync(new[] { b, c }, announceNew: false);
        Check(st2.Actors[1].Spawning, "spawn: a later sync does not cancel it");

        // No spawn animation in the sheet: it just appears.
        var st3 = StageWith(SheetWith(true, true, true));
        st3.Sync(new[] { Sess("d") }, announceNew: true);
        Check(!st3.Actors[0].Spawning && st3.Actors[0].Anim == "typing", "spawn: no spawn in the sheet -> appears at once");

        // A call for attention waits for the birth to finish.
        var st4 = StageWith(sheet);
        var e = Sess("e", SessionStatus.Asking);
        st4.Sync(new[] { e });
        st4.Advance(300);
        var pe = st4.Actors[0];
        Check(pe.X == PetStage.HomeX(0) && pe.Anim == "spawn" && !pe.AtFront, "spawn: a pet that is already asking stays put while it is born");
        st4.Advance(100);
        st4.Advance(1000);
        Check(Math.Abs(pe.X - (PetStage.HomeX(0) - 30)) < 1e-6 && pe.Anim == "walkL", "spawn: then it walks out to ask");

        // Running away waits for the birth to finish too.
        var st5 = StageWith(sheet);
        st5.SetFleeing(true);
        st5.Sync(new[] { Sess("f") });
        st5.Advance(300);
        Check(st5.Actors[0].X == PetStage.HomeX(0) && st5.Actors[0].Anim == "spawn", "spawn: no running during the birth");
        st5.Advance(100);
        st5.Advance(1000);
        Check(Math.Abs(st5.Actors[0].X - (PetStage.HomeX(0) - 90)) < 1e-6 && st5.Actors[0].Anim == "runL", "spawn: then it runs");

        // The same session that is already on the stage does not get a new birth.
        var st6 = StageWith(sheet);
        var g = Sess("g");
        st6.Sync(new[] { g }, announceNew: false);
        st6.Advance(100);
        st6.Sync(new[] { g }, announceNew: true);
        Check(!st6.Actors[0].Spawning && st6.Actors[0].Anim == "typing", "spawn: a pet that is already here is not born again");
    }

    private static void VanishBehaviour()
    {
        var sheet = SheetWith(true, true, true, spawn: 4, vanish: 3);
        var st = StageWith(sheet);
        var a = Sess("a"); var b = Sess("b");
        st.Sync(new[] { a, b }, announceNew: false);
        st.Advance(100);
        var pa = st.Actors[0];
        st.Sync(new[] { b });
        Check(st.Actors.Count == 2 && pa.Leaving && pa.Anim == "vanish" && pa.AnimMs == 0 && pa.X == PetStage.HomeX(0) && !pa.AtFront,
            "vanish: a pet whose session is gone stays and plays the leaving animation");
        st.Advance(200);
        Check(st.Actors.Count == 2 && pa.Leaving && Math.Abs(pa.AnimMs - 200) < 1e-9, "vanish: it stays until the animation is over (300 ms)");

        // The seat stays taken meanwhile.
        var c = Sess("c");
        st.Sync(new[] { b, c });
        PetActor? pc = null;
        foreach (var p in st.Actors) if (p.SessionId == "c") pc = p;
        Check(pc != null && pc.Slot == 2, "vanish: a new session cannot take the seat of a leaving pet");
        st.Advance(100);
        Check(st.Actors.Count == 2 && !st.Actors.Contains(pa), "vanish: when the animation is over the pet is taken off");
        var d = Sess("d");
        st.Sync(new[] { b, c, d }, announceNew: false);
        Check(st.Actors[0].SessionId == "d" && st.Actors[0].Slot == 0, "vanish: and its seat is free again");

        // Nothing is called out while leaving.
        var st2 = StageWith(sheet);
        var e = Sess("e", SessionStatus.Asking);
        st2.Sync(new[] { e }, announceNew: false);
        st2.Advance(1e9);
        var pe = st2.Actors[0];
        Check(pe.AtFront && pe.Anim == "ask", "vanish: (setup) the asking pet is out in front");
        st2.Sync(new SessionInfo[0]);
        Check(pe.Leaving && !pe.AtFront && pe.Anim == "vanish" && pe.X == PetStage.FrontX(0), "vanish: it leaves from where it stands, with no call for attention");
        st2.Advance(200);
        Check(pe.X == PetStage.FrontX(0) && pe.Anim == "vanish" && st2.Actors.Count == 1, "vanish: it does not move while leaving");
        st2.Advance(1e9);
        Check(st2.Actors.Count == 0, "vanish: and then it is gone (the last one too)");

        // Statuses changing during the leaving are ignored.
        var st3 = StageWith(sheet);
        var f = Sess("f");
        st3.Sync(new[] { f }, announceNew: false);
        st3.Sync(new SessionInfo[0]);
        var pf = st3.Actors[0];
        f.Status = SessionStatus.Asking;
        st3.Advance(100);
        Check(pf.Leaving && pf.Anim == "vanish" && pf.X == PetStage.HomeX(0) && !pf.AtFront, "vanish: it does not walk out while leaving");

        // The session comes back while leaving: it stays.
        var st4 = StageWith(sheet);
        var g = Sess("g");
        st4.Sync(new[] { g }, announceNew: false);
        st4.Advance(100);
        var pg = st4.Actors[0];
        st4.Sync(new SessionInfo[0]);
        st4.Advance(100);
        st4.Sync(new[] { g });
        Check(st4.Actors.Count == 1 && ReferenceEquals(st4.Actors[0], pg) && !pg.Leaving && pg.Anim == "typing" && pg.X == PetStage.HomeX(0),
            "vanish: if the session comes back the leaving stops and the same pet stays");
        st4.Advance(1e9);
        Check(st4.Actors.Count == 1 && st4.Actors[0].Anim == "typing" && !pg.Spawning, "vanish: it is not removed afterwards and is not born again");

        // No vanish animation: gone at once, as before.
        var st5 = StageWith(SheetWith(true, true, true));
        st5.Sync(new[] { Sess("h") }, announceNew: false);
        st5.Sync(new SessionInfo[0]);
        Check(st5.Actors.Count == 0, "vanish: no vanish in the sheet -> removed at once");
        var st5b = new PetStage();
        st5b.Sync(new[] { Sess("h2") });
        st5b.Sync(new SessionInfo[0]);
        Check(st5b.Actors.Count == 0, "vanish: a stage without a sheet removes at once");

        // Running away stops where it is.
        var st6 = StageWith(sheet);
        st6.Sync(new[] { Sess("i") }, announceNew: false);
        st6.SetFleeing(true);
        st6.Advance(1000);
        var pi = st6.Actors[0];
        double xi = pi.X;
        st6.Sync(new SessionInfo[0]);
        st6.Advance(100);
        Check(pi.Leaving && !pi.Fleeing && !pi.Muttering && pi.X == xi && pi.Anim == "vanish", "vanish: a running pet stops and leaves where it is");

        // All seats taken, one leaving: the waiting session gets the seat as soon as it is free.
        var st7 = StageWith(sheet);
        var five = new List<SessionInfo>();
        for (int k = 0; k < PetStage.Slots; k++) five.Add(Sess("s" + k));
        st7.Sync(five, announceNew: false);
        var next = new List<SessionInfo>(five.GetRange(1, PetStage.Slots - 1)) { Sess("fresh") };
        st7.Sync(next, announceNew: true);
        Check(st7.Actors.Count == PetStage.Slots && !st7.Actors.Any(p => p.SessionId == "fresh"),
            "vanish: with every seat taken the newcomer waits");
        st7.Advance(300);
        var born = st7.Actors.FirstOrDefault(p => p.SessionId == "fresh");
        Check(born != null && born.Slot == 0 && born.Spawning && born.Anim == "spawn" && st7.Actors.Count == PetStage.Slots,
            "vanish: it takes the seat the moment the leaving pet is gone, and is born");
        var h1 = st7.Actors.First(p => p.SessionId == "s1");
        Check(h1.Slot == 1 && h1.X == PetStage.HomeX(1), "vanish: the other pets never move seats");
    }

    private static async Task ServerRoundTrip()
    {
        // Best-effort live socket check. Some sandboxes intercept direct
        // TCP from test processes; there the parse-level checks above are
        // the coverage and this part reports SKIP instead of FAIL.
        var received = new List<NotifyEvent>();
        var server = new NotifyServer(18765, ev => { lock (received) received.Add(ev); });
        server.Start();
        try
        {
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
            { Timeout = TimeSpan.FromSeconds(5) };
            var resp = await http.PostAsync("http://127.0.0.1:18765/event",
                new StringContent("{\"session_id\":\"net-1\",\"type\":\"done\",\"source\":\"proj\",\"surface\":\"terminal\",\"orca_terminal\":\"" + OrcaHandle + "\"}",
                    System.Text.Encoding.UTF8, "application/json"));
            Check(resp.IsSuccessStatusCode, "POST /event answers 200");

            var resp2 = await http.GetAsync("http://127.0.0.1:18765/done?source=old&hwnd=7&type=done&orca_terminal=term_abc123");
            Check(resp2.IsSuccessStatusCode, "legacy GET /done answers 200");

            await Task.Delay(300);
            lock (received)
            {
                Check(received.Count == 2, "server delivered both events");
                Check(received[0].SessionId == "net-1" && received[0].Kind == NotifyEventKind.Done,
                    "POST event content is intact");
                Check(received[0].OrcaTerminal == OrcaHandle, "POST orca_terminal survives the socket");
                Check(received[1].Source == "old" && received[1].Kind == NotifyEventKind.Done,
                    "legacy event content is intact");
                Check(received[1].OrcaTerminal == "term_abc123", "legacy GET orca_terminal survives the socket");
            }
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("invalid status line"))
        {
            Console.WriteLine("SKIP live socket round-trip (TCP intercepted by the sandbox)");
        }
        finally { server.Stop(); }
    }
}
