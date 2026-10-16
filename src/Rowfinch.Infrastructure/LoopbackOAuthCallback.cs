using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Rowfinch.Infrastructure;

public sealed class LoopbackOAuthCallback : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    public Uri RedirectUri { get; }
    public LoopbackOAuthCallback()
    {
        listener.Start();
        RedirectUri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/auth/callback");
    }

    public async Task<IReadOnlyDictionary<string, string>> WaitAsync(string state, CancellationToken token)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await using var stream = client.GetStream();
            try
            {
                // Read bounded HTTP headers: callbacks contain a code, never access/refresh tokens.
                var data = new List<byte>();
                var buffer = new byte[1];
                while (data.Count < 16_384)
                {
                    if (await stream.ReadAsync(buffer, timeout.Token) == 0) break;
                    data.Add(buffer[0]);
                    var n = data.Count;
                    if (n >= 4 && data[n - 4] == 13 && data[n - 3] == 10 && data[n - 2] == 13 && data[n - 1] == 10) break;
                }
                var requestLine = Encoding.ASCII.GetString(data.ToArray()).Split("\r\n")[0].Split(' ');
                var valid = requestLine.Length == 3 && requestLine[0] == "GET" && requestLine[1].StartsWith("/auth/callback?", StringComparison.Ordinal);
                var query = valid ? ParseQuery(new Uri(RedirectUri, requestLine[1]).Query) : new Dictionary<string, string>();
                valid &= query.TryGetValue("state", out var returned) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(returned));
                var body = valid ? "Return to Rowfinch to finish signing in. You can close this browser tab." : "Invalid callback. Return to Rowfinch and try again.";
                var bytes = Encoding.UTF8.GetBytes(body);
                var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {(valid ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: {bytes.Length}\r\n\r\n");
                await stream.WriteAsync(headers, timeout.Token);
                await stream.WriteAsync(bytes, timeout.Token);
                if (valid) return query;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (IOException) { }
        }
    }

    public static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            if (!values.TryAdd(key, parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "")) return [];
        }
        return values;
    }
    public void Dispose() => listener.Stop();
}
