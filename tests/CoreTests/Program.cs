using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
