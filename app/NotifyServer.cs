using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeTaskBar;

public class NotifyServer
{
    private readonly TcpListener _listener;
    private readonly Action<string, string, string, string> _onDone;
    private CancellationTokenSource? _cts;

    public NotifyServer(int port, Action<string, string, string, string> onDone)
    {
        _onDone = onDone;
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
                client.ReceiveTimeout = 2000;
                var stream = client.GetStream();
                var buf = new byte[8192];
                int n = await stream.ReadAsync(buf, 0, buf.Length);
                var text = Encoding.UTF8.GetString(buf, 0, n);
                var firstLine = text.Split('\n')[0].Trim();
                var parts = firstLine.Split(' ');
                if (parts.Length >= 2)
                {
                    var (source, hwnd, type, surface) = ParseQuery(parts[1]);
                    _onDone(source, hwnd, type, surface);
                }
                var body = "ok";
                var resp = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nAccess-Control-Allow-Origin: *\r\nContent-Length: "
                         + Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body;
                var rb = Encoding.UTF8.GetBytes(resp);
                await stream.WriteAsync(rb, 0, rb.Length);
                await stream.FlushAsync();
            }
        }
        catch { }
    }

    private static (string, string, string, string) ParseQuery(string path)
    {
        string source = "Claude", hwnd = "", type = "done", surface = "";
        int q = path.IndexOf('?');
        if (q < 0) return (source, hwnd, type, surface);
        foreach (var pair in path.Substring(q + 1).Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0) continue;
            var key = pair.Substring(0, eq);
            var val = Uri.UnescapeDataString(pair.Substring(eq + 1));
            if (key == "source" && !string.IsNullOrWhiteSpace(val)) source = val;
            else if (key == "hwnd") hwnd = val;
            else if (key == "type" && !string.IsNullOrWhiteSpace(val)) type = val;
            else if (key == "surface") surface = val;
        }
        return (source, hwnd, type, surface);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
    }
}
