using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeTaskBar;

/// <summary>
/// Local event receiver. Accepts the current protocol
/// (POST /event with a JSON body from notify.ps1) and keeps the legacy
/// GET /done?source=..&amp;hwnd=..&amp;type=.. route working so older
/// installed hooks do not silently break.
/// </summary>
public class NotifyServer
{
    private readonly TcpListener _listener;
    private readonly Action<NotifyEvent> _onEvent;
    private CancellationTokenSource? _cts;

    public NotifyServer(int port, Action<NotifyEvent> onEvent)
    {
        _onEvent = onEvent;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public void Start()
    {
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch { break; }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var raw = await ReadRequestAsync(stream);
                if (raw.Length == 0) return;

                var ev = ParseRequest(raw);
                if (ev != null && ev.Kind != NotifyEventKind.Unknown) _onEvent(ev);

                var respBody = "ok";
                var resp = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nAccess-Control-Allow-Origin: *\r\nContent-Length: "
                         + Encoding.UTF8.GetByteCount(respBody) + "\r\nConnection: close\r\n\r\n" + respBody;
                var rb = Encoding.UTF8.GetBytes(resp);
                await stream.WriteAsync(rb, 0, rb.Length);
                await stream.FlushAsync();
            }
        }
        catch (Exception ex)
        {
            // Do not swallow silently: a dead receiver looks like "the app
            // does nothing", which was a core complaint about the old design.
            System.Diagnostics.Debug.WriteLine("NotifyServer: " + ex.Message);
        }
    }

    /// <summary>Parse one raw HTTP request into an event (null when unparseable).</summary>
    public static NotifyEvent? ParseRequest(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        int headEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = headEnd >= 0 ? raw.Substring(0, headEnd) : raw;
        var body = headEnd >= 0 ? raw.Substring(headEnd + 4) : "";
        var firstLine = head.Split('\n')[0].Trim();
        var parts = firstLine.Split(' ');
        if (parts.Length < 2) return null;
        var method = parts[0].ToUpperInvariant();
        if (method == "POST" && !string.IsNullOrWhiteSpace(body))
            return NotifyEvent.FromJson(body);
        return NotifyEvent.FromQuery(parts[1]);
    }

    /// <summary>Read headers plus the Content-Length body (JSON posts are multi-read).</summary>
    private static async Task<string> ReadRequestAsync(NetworkStream stream)
    {
        var ms = new MemoryStream();
        var buf = new byte[4096];
        int contentLength = -1;
        int headEnd = -1;

        while (true)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            int n;
            try { n = await stream.ReadAsync(buf, 0, buf.Length, timeout.Token); }
            catch { break; }
            if (n <= 0) break;
            ms.Write(buf, 0, n);

            var soFar = ms.ToArray();
            if (headEnd < 0)
            {
                var text = Encoding.UTF8.GetString(soFar);
                int idx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    headEnd = idx + 4;
                    foreach (var line in text.Substring(0, idx).Split('\n'))
                    {
                        var t = line.Trim();
                        if (t.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(t.Substring("Content-Length:".Length).Trim(), out contentLength);
                    }
                }
            }
            if (headEnd >= 0)
            {
                int bodyLen = soFar.Length - headEnd;
                if (contentLength >= 0 && bodyLen >= contentLength) break;
                if (contentLength < 0) break; // no body expected
            }
            if (soFar.Length > 65536) break; // sanity cap
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
    }
}
