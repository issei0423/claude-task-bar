using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeTaskBar;

/// <summary>
/// Switches Orca to the terminal tab a session runs in, via the Orca CLI
/// (<c>orca terminal switch --terminal &lt;handle&gt; --json</c>).
/// The handle comes from ORCA_TERMINAL_HANDLE, forwarded by notify.ps1.
/// Everything here is best effort: on any failure the caller has already
/// brought the window forward, so the user still lands in Orca.
/// No WPF here, so the tests compile this file directly.
/// </summary>
public static class OrcaJump
{
    /// <summary>The CLI gets this long, then it is killed.</summary>
    public static readonly TimeSpan SwitchTimeout = TimeSpan.FromSeconds(5);

    // \z, not $: in .NET "$" also matches before a trailing newline.
    private static readonly Regex HandleShape =
        new(@"^term_[A-Za-z0-9_-]{1,128}\z", RegexOptions.CultureInvariant);

    /// <summary>True only for the documented handle shape (term_ + 1..128 of [A-Za-z0-9_-]).</summary>
    public static bool IsValidHandle(string? handle) =>
        !string.IsNullOrEmpty(handle) && HandleShape.IsMatch(handle);

    /// <summary>
    /// Find orca.exe: config "orcaPath" (absolute and ending in .exe only),
    /// then the per-user install location, then each PATH directory.
    /// orca.cmd is never used (it would run through cmd.exe). Null when none exists.
    /// </summary>
    public static string? ResolveCli(string? configured, Func<string, bool> exists, string localAppData, string? pathEnv)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var c = configured.Trim().Trim('"');
            if (c.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                IsFullyQualified(c) && SafeExists(exists, c))
                return c;
        }

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var def = Path.Combine(localAppData, "Programs", "orca", "resources", "bin", "orca.exe");
            if (SafeExists(exists, def)) return def;
        }

        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var entry in pathEnv.Split(';'))
            {
                var dir = entry.Trim().Trim('"');
                if (dir.Length == 0 || !IsFullyQualified(dir)) continue;
                string candidate;
                try { candidate = Path.Combine(dir, "orca.exe"); }
                catch { continue; }
                if (SafeExists(exists, candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// The process to run. Arguments go through ArgumentList (no shell, no
    /// string splicing). Null when the handle is not the documented shape.
    /// </summary>
    public static ProcessStartInfo? BuildStartInfo(string cli, string handle)
    {
        if (string.IsNullOrWhiteSpace(cli) || !IsValidHandle(handle)) return null;
        var psi = new ProcessStartInfo(cli)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("terminal");
        psi.ArgumentList.Add("switch");
        psi.ArgumentList.Add("--terminal");
        psi.ArgumentList.Add(handle);
        psi.ArgumentList.Add("--json");
        return psi;
    }

    /// <summary>
    /// Resolve the CLI and switch to the tab. True only when orca.exe exits 0.
    /// Failures are written as one line to %TEMP%\ctb.log (or to <paramref name="log"/>).
    /// </summary>
    public static async Task<bool> TrySwitchAsync(string? configuredPath, string handle, Action<string>? log = null)
    {
        log ??= Log;
        if (!IsValidHandle(handle))
        {
            log("Orca switch skipped: terminal handle has an unexpected shape");
            return false;
        }
        var cli = ResolveCli(
            configuredPath,
            File.Exists,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("PATH"));
        if (cli == null)
        {
            log("Orca switch failed: orca.exe not found (set \"orcaPath\" in config.json)");
            return false;
        }
        var psi = BuildStartInfo(cli, handle);
        if (psi == null) return false;
        var ok = await RunAsync(psi, SwitchTimeout, log).ConfigureAwait(false);
        if (ok) log("Orca switch ok: " + handle);
        return ok;
    }

    /// <summary>Run one process with a time limit; kill it (and its children) on timeout.</summary>
    public static async Task<bool> RunAsync(ProcessStartInfo psi, TimeSpan timeout, Action<string> log)
    {
        using var p = new Process { StartInfo = psi };
        try
        {
            if (!p.Start())
            {
                log("Orca switch failed: process did not start");
                return false;
            }
        }
        catch (Exception ex)
        {
            log("Orca switch failed: could not start " + psi.FileName + ": " + OneLine(ex.Message));
            return false;
        }

        var stdout = psi.RedirectStandardOutput ? p.StandardOutput.ReadToEndAsync() : Task.FromResult("");
        var stderr = psi.RedirectStandardError ? p.StandardError.ReadToEndAsync() : Task.FromResult("");

        using (var cts = new CancellationTokenSource(timeout))
        {
            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                try { p.WaitForExit(2000); } catch { }
                log("Orca switch failed: timed out after " + timeout.TotalSeconds.ToString("0.#") + "s (process killed)");
                return false;
            }
        }

        // A child of orca.exe could keep the pipes open after it exits:
        // give the output a moment, never wait on it forever.
        try { await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(1000)).ConfigureAwait(false); } catch { }
        string output = stdout.Status == TaskStatus.RanToCompletion ? stdout.Result : "";
        string error = stderr.Status == TaskStatus.RanToCompletion ? stderr.Result : "";

        if (p.ExitCode == 0) return true;
        var detail = !string.IsNullOrWhiteSpace(error) ? error : output;
        log("Orca switch failed: exit code " + p.ExitCode + (detail.Length > 0 ? " " + OneLine(detail) : ""));
        return false;
    }

    /// <summary>Same file and line format as WindowFocus, so one log tells the whole story.</summary>
    public static void Log(string m)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "ctb.log"),
                DateTime.Now.ToString("HH:mm:ss.fff ") + m + Environment.NewLine);
        }
        catch { }
    }

    private static bool IsFullyQualified(string path)
    {
        try { return Path.IsPathFullyQualified(path); }
        catch { return false; }
    }

    private static bool SafeExists(Func<string, bool> exists, string path)
    {
        try { return exists(path); }
        catch { return false; }
    }

    private static string OneLine(string s)
    {
        var t = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length > 200 ? t.Substring(0, 200) + "..." : t;
    }
}
