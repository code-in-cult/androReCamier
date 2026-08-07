using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.Util;

namespace MjpegStreamer
{
    // Minimal HTTP server: serves / (index.html), /stream.mjpg, /start, /stop
    public class SimpleHttpServer
    {
        readonly int port;
        TcpListener listener;
        CancellationTokenSource cts;
        readonly ConcurrentDictionary<TcpClient, NetworkStream> streamClients = new ConcurrentDictionary<TcpClient, NetworkStream>();
        CameraService camera;
        Action<string> statusCallback;
        readonly byte[] indexHtmlBytes;

        const string boundary = "myboundary";

        public SimpleHttpServer(int port, CameraService cameraService, Action<string> statusCb)
        {
            this.port = port;
            this.camera = cameraService;
            this.statusCallback = statusCb;
            indexHtmlBytes = Encoding.UTF8.GetBytes(HtmlIndex);
        }

        public void Start()
        {
            cts = new CancellationTokenSource();
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            Task.Run(() => AcceptLoop(cts.Token));
            statusCallback?.Invoke($"HTTP server listening on port {port}");
        }

        public void Stop()
        {
            try
            {
                cts.Cancel();
                listener.Stop();
                foreach (var kv in streamClients)
                {
                    try { kv.Value.Close(); kv.Key.Close(); } catch { }
                }
            }
            catch { }
            statusCallback?.Invoke("Server stopped.");
        }

        async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleClient(client, token));
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    Log.Debug("SimpleHttpServer", $"Accept exception: {ex.Message}");
                }
            }
        }

        async Task HandleClient(TcpClient client, CancellationToken token)
        {
            var ns = client.GetStream();
            ns.ReadTimeout = 5000;
            try
            {
                // read request
                var request = await ReadRequest(ns);
                if (string.IsNullOrEmpty(request)) { client.Close(); return; }
                var path = ParsePath(request);

                if (path == "/stream.mjpg")
                {
                    var header = $"HTTP/1.0 200 OK\r\n" +
                                 $"Connection: close\r\n" +
                                 $"Max-Age: 0\r\n" +
                                 $"Expires: 0\r\n" +
                                 $"Cache-Control: no-cache, private\r\n" +
                                 $"Pragma: no-cache\r\n" +
                                 $"Content-Type: multipart/x-mixed-replace; boundary={boundary}\r\n\r\n";
                    var headerBytes = Encoding.UTF8.GetBytes(header);
                    await ns.WriteAsync(headerBytes, 0, headerBytes.Length);
                    if (!streamClients.TryAdd(client, ns))
                    {
                        client.Close();
                        return;
                    }
                    statusCallback?.Invoke($"Client connected for stream ({streamClients.Count})");
                    // Keep the connection open until it disconnects.
                    while (!token.IsCancellationRequested && client.Connected)
                    {
                        await Task.Delay(1000, token).ContinueWith(t => { });
                    }
                    streamClients.TryRemove(client, out _);
                    try { ns.Close(); client.Close(); } catch { }
                }
                else if (path == "/start")
                {
                    await WriteSimpleResponse(ns, "Starting camera...");
                    _ = Task.Run(async () =>
                    {
                        var ok = await camera.StartCaptureAsync();
                        statusCallback?.Invoke(ok ? "Camera started." : "Camera start failed.");
                    });
                }
                else if (path == "/stop")
                {
                    camera.StopCapture();
                    await WriteSimpleResponse(ns, "Stopped camera.");
                }
                else
                {
                    var response = "HTTP/1.0 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + indexHtmlBytes.Length + "\r\n\r\n";
                    var headerBytes = Encoding.UTF8.GetBytes(response);
                    await ns.WriteAsync(headerBytes, 0, headerBytes.Length);
                    await ns.WriteAsync(indexHtmlBytes, 0, indexHtmlBytes.Length);
                    ns.Close();
                    client.Close();
                }
            }
            catch (Exception ex)
            {
                Log.Debug("SimpleHttpServer", $"Client handler error: {ex.Message}");
                try { ns.Close(); client.Close(); } catch { }
            }
        }

        static async Task<string> ReadRequest(NetworkStream ns)
        {
            var buffer = new byte[4096];
            int read = 0;
            try
            {
                read = await ns.ReadAsync(buffer, 0, buffer.Length);
            }
            catch { return null; }
            if (read <= 0) return null;
            return Encoding.UTF8.GetString(buffer, 0, read);
        }

        static string ParsePath(string request)
        {
            var lines = request.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return "/";
            var parts = lines[0].Split(' ');
            if (parts.Length < 2) return "/";
            return parts[1];
        }

        async Task WriteSimpleResponse(NetworkStream ns, string body)
        {
            var b = Encoding.UTF8.GetBytes(body);
            var header = $"HTTP/1.0 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {b.Length}\r\n\r\n";
            var headerBytes = Encoding.UTF8.GetBytes(header);
            await ns.WriteAsync(headerBytes, 0, headerBytes.Length);
            await ns.WriteAsync(b, 0, b.Length);
            try { ns.Close(); } catch { }
        }

        // Called by CameraService when a new JPEG frame is available
        public void BroadcastFrame(byte[] jpeg)
        {
            if (streamClients.IsEmpty) return;
            var partHeader = $"--{boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n";
            var headerBytes = Encoding.UTF8.GetBytes(partHeader);
            var trailer = "\r\n";
            var trailerBytes = Encoding.UTF8.GetBytes(trailer);

            foreach (var kv in streamClients)
            {
                var client = kv.Key;
                var ns = kv.Value;
                if (!client.Connected) { streamClients.TryRemove(client, out _); continue; }
                try
                {
                    ns.Write(headerBytes, 0, headerBytes.Length);
                    ns.Write(jpeg, 0, jpeg.Length);
                    ns.Write(trailerBytes, 0, trailerBytes.Length);
                    ns.Flush();
                }
                catch
                {
                    try { ns.Close(); client.Close(); } catch { }
                    streamClients.TryRemove(client, out _);
                }
            }
        }

        const string HtmlIndex = @"<!doctype html>
<html>
<head><meta charset='utf-8'><title>Phone MJPEG Stream</title></head>
<body>
  <h2>Phone MJPEG Stream</h2>
  <div>
    <button onclick="startStream()">Start</button>
    <button onclick="stopStream()">Stop</button>
  </div>
  <div style='margin-top:12px;'>
    <img id='mjpeg' src='/stream.mjpg' style='max-width:100%; border:1px solid #ccc;'/>
  </div>
<script>
async function startStream(){
  await fetch('/start');
}
async function stopStream(){
  await fetch('/stop');
  document.getElementById('mjpeg').src = '';
  setTimeout(()=>document.getElementById('mjpeg').src='/stream.mjpg',300);
}
</script>
</body>
</html>";
    }
}
