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
