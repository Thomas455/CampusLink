using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CoreTests
{
    public class MockRequest
    {
        public string Method { get; set; }
        public string Path { get; set; }
        public string Query { get; set; }
        public string Body { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public class MockResponse
    {
        public int Status { get; set; } = 200;
        public string Reason { get; set; } = "OK";
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body { get; set; } = Array.Empty<byte>();

        public static MockResponse Text(string body, string contentType = "text/html;charset=UTF-8")
        {
            var response = new MockResponse { Body = Encoding.UTF8.GetBytes(body) };
            response.Headers["Content-Type"] = contentType;
            return response;
        }

        public static MockResponse Json(string body)
        {
            return Text(body, "application/json;charset=UTF-8");
        }

        public static MockResponse Redirect(string location)
        {
            var response = new MockResponse { Status = 302, Reason = "Found" };
            response.Headers["Location"] = location;
            return response;
        }
    }

    /// <summary>极简 TCP HTTP 服务，避免 HttpListener 需要 URL ACL 管理员权限。</summary>
    public class MockPortal : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _acceptTask;

        public MockPortal(Func<MockRequest, MockResponse> handler)
        {
            Handler = handler;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptTask = Task.Run(AcceptLoopAsync);
        }

        public Func<MockRequest, MockResponse> Handler { get; }

        public int Port { get; }

        public string BaseUrl => "http://127.0.0.1:" + Port;

        public List<MockRequest> Requests { get; } = new List<MockRequest>();

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch
                {
                    if (_cts.IsCancellationRequested) return;
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var requestLine = await ReadLineAsync(stream).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(requestLine)) return;

                    var request = new MockRequest();
                    var mainParts = requestLine.Split(' ');
                    if (mainParts.Length >= 2)
                    {
                        request.Method = mainParts[0];
                        var target = mainParts[1];
                        var q = target.IndexOf('?');
                        if (q >= 0)
                        {
                            request.Path = target.Substring(0, q);
                            request.Query = target.Substring(q + 1);
                        }
                        else
                        {
                            request.Path = target;
                            request.Query = "";
                        }
                    }

                    while (true)
                    {
                        var line = await ReadLineAsync(stream).ConfigureAwait(false);
                        if (line == null || line.Length == 0) break;
                        var index = line.IndexOf(':');
                        if (index <= 0) continue;
                        request.Headers[line.Substring(0, index).Trim()] = line.Substring(index + 1).Trim();
                    }

                    int contentLength = 0;
                    if (request.Headers.TryGetValue("Content-Length", out var lengthText))
                    {
                        int.TryParse(lengthText, out contentLength);
                    }

                    if (contentLength > 0)
                    {
                        var body = new byte[contentLength];
                        var offset = 0;
                        while (offset < contentLength)
                        {
                            var read = await stream.ReadAsync(body, offset, contentLength - offset).ConfigureAwait(false);
                            if (read <= 0) break;
                            offset += read;
                        }
                        request.Body = Encoding.UTF8.GetString(body, 0, offset);
                    }

                    lock (Requests) Requests.Add(request);

                    var response = Handler(request) ?? new MockResponse { Status = 404, Reason = "Not Found" };

                    var head = new StringBuilder();
                    head.Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(response.Reason).Append("\r\n");
                    foreach (var header in response.Headers)
                    {
                        head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
                    }
                    head.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n");
                    head.Append("Connection: close\r\n\r\n");

                    var headBytes = Encoding.ASCII.GetBytes(head.ToString());
                    await stream.WriteAsync(headBytes, 0, headBytes.Length).ConfigureAwait(false);
                    if (response.Body.Length > 0)
                    {
                        await stream.WriteAsync(response.Body, 0, response.Body.Length).ConfigureAwait(false);
                    }
                    await stream.FlushAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                // 测试用服务，忽略连接错误
            }
        }

        private static async Task<string> ReadLineAsync(Stream stream)
        {
            var buffer = new List<byte>(128);
            var one = new byte[1];
            while (true)
            {
                var read = await stream.ReadAsync(one, 0, 1).ConfigureAwait(false);
                if (read == 0) return buffer.Count == 0 ? null : Encoding.ASCII.GetString(buffer.ToArray()).TrimEnd('\r');
                if (one[0] == (byte)'\n') return Encoding.ASCII.GetString(buffer.ToArray()).TrimEnd('\r');
                buffer.Add(one[0]);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _acceptTask.Wait(1000); } catch { }
        }
    }
}
