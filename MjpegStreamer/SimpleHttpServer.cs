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
    // HTTP server with full camera control interface: streams, resolutions, exposure, ISO, focus, white balance, and rotation
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
                var request = await ReadRequest(ns);
                if (string.IsNullOrEmpty(request)) { client.Close(); return; }
                var path = ParsePath(request);
                var query = ParseQuery(request);

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
                else if (path == "/resolutions")
                {
                    var resolutions = camera.GetSupportedResolutions();
                    var json = "[\n";
                    for (int i = 0; i < resolutions.Count; i++)
                    {
                        json += $"  {{\"index\": {i}, \"width\": {resolutions[i].Width}, \"height\": {resolutions[i].Height}, \"label\": \"{resolutions[i].Width}x{resolutions[i].Height}\"}}";
                        if (i < resolutions.Count - 1) json += ",";
                        json += "\n";
                    }
                    json += "]";
                    await WriteJsonResponse(ns, json);
                }
                else if (path == "/resolution")
                {
                    var current = camera.GetCurrentResolution();
                    var json = $"{{\"width\": {current.Width}, \"height\": {current.Height}, \"label\": \"{current.Width}x{current.Height}\"}}";
                    await WriteJsonResponse(ns, json);
                }
                else if (path == "/setresolution")
                {
                    if (int.TryParse(query, out int index))
                    {
                        await WriteSimpleResponse(ns, $"Setting resolution...");
                        _ = Task.Run(async () =>
                        {
                            var ok = await camera.SetResolutionAsync(index);
                            statusCallback?.Invoke(ok ? $"Resolution changed." : "Resolution change failed.");
                        });
                    }
                    else
                    {
                        await WriteSimpleResponse(ns, "Invalid index parameter");
                    }
                }
                else if (path == "/controls")
                {
                    // Get current control settings as JSON
                    var json = camera.GetControlRangesJson();
                    await WriteJsonResponse(ns, json);
                }
                else if (path == "/exposure")
                {
                    // Set exposure time: /exposure?us=1000 (microseconds)
                    if (long.TryParse(query, out long exposureUs))
                    {
                        camera.SetExposureTime(exposureUs);
                        await WriteSimpleResponse(ns, $"Exposure set to {exposureUs} µs");
                    }
                    else
                    {
                        await WriteSimpleResponse(ns, "Invalid exposure parameter");
                    }
                }
                else if (path == "/iso")
                {
                    // Set ISO: /iso?value=400
                    if (int.TryParse(query, out int iso))
                    {
                        camera.SetISO(iso);
                        await WriteSimpleResponse(ns, $"ISO set to {iso}");
                    }
                    else
                    {
                        await WriteSimpleResponse(ns, "Invalid ISO parameter");
                    }
                }
                else if (path == "/focus")
                {
                    // Set focus distance: /focus?distance=0.5
                    if (float.TryParse(query.Replace(",", "."), out float distance))
                    {
                        camera.SetFocusDistance(distance);
                        await WriteSimpleResponse(ns, $"Focus distance set to {distance}");
                    }
                    else
                    {
                        await WriteSimpleResponse(ns, "Invalid focus distance parameter");
                    }
                }
                else if (path == "/autoexposure")
                {
                    // Enable/Disable auto exposure: /autoexposure?enabled=true
                    if (bool.TryParse(query, out bool enabled))
                    {
                        camera.SetAutoExposure(enabled);
                        await WriteSimpleResponse(ns, $"Auto exposure: {(enabled ? "ON" : "OFF")}");
                    }
                    else
                    {
                        await WriteSimpleResponse(ns, "Invalid autoexposure parameter");
                    }
                }
                else if (path == "/autofocus")
                {
                    // Enable/Disable auto focus: /autofocus?enabled=true
                    if (bool.TryParse(query, out bool enabled))
                    {
                        camera.SetAutoFocus(enabled);
                        await WriteSimpleResponse(ns, $"Auto focus: {(enabled ? "ON" : "OFF")}");
                    }
                    else
                    {
                        await WriteSimpleResponse(ns, "Invalid autofocus parameter");
                    }
                }
                else if (path == "/whitebalance")
                {
                    // Set white balance mode: /whitebalance?mode=daylight
                    if (Enum.TryParse<Android.Hardware.Camera2.ControlAwbMode>(query, true, out var mode))
                    {
                        camera.SetWhiteBalance(mode);
                        await WriteSimpleResponse(ns, $"White balance set to {mode}");
                    }
                    else
                    {
                        await WriteSimpleResponse(ns, "Invalid white balance mode");
                    }
                }
                else if (path == "/exposurecomp")
                {
                    // Set exposure compensation: /exposurecomp?comp=1.5
                    if (float.TryParse(query.Replace(",", "."), out float comp))
                    {
                        camera.SetExposureCompensation(comp);
                        await WriteSimpleResponse(ns, $"Exposure compensation set to {comp}");
                    }
                    else
                    {
                        await WriteSimpleResponse(ns, "Invalid compensation parameter");
                    }
                }
                else if (path == "/rotation")
                {
                    // Set image rotation: /rotation?degrees=90
                    if (int.TryParse(query, out int degrees))
                    {
                        camera.SetImageRotation(degrees);
                        await WriteSimpleResponse(ns, $"Image rotation set to {degrees}°");
                    }
                    else
                    {
                        await WriteSimpleResponse(ns, "Invalid rotation parameter");
                    }
                }
                else if (path == "/getrotation")
                {
                    // Get current rotation
                    int rotation = camera.GetImageRotation();
                    var json = $"{{\"rotation\": {rotation}}}";
                    await WriteJsonResponse(ns, json);
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
            
            var pathWithQuery = parts[1];
            var questionMarkIndex = pathWithQuery.IndexOf('?');
            if (questionMarkIndex > -1)
                return pathWithQuery.Substring(0, questionMarkIndex);
            return pathWithQuery;
        }

        static string ParseQuery(string request)
        {
            var lines = request.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return "";
            var parts = lines[0].Split(' ');
            if (parts.Length < 2) return "";
            
            var pathWithQuery = parts[1];
            var questionMarkIndex = pathWithQuery.IndexOf('?');
            if (questionMarkIndex > -1)
                return pathWithQuery.Substring(questionMarkIndex + 1);
            return "";
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

        async Task WriteJsonResponse(NetworkStream ns, string json)
        {
            var b = Encoding.UTF8.GetBytes(json);
            var header = $"HTTP/1.0 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {b.Length}\r\n\r\n";
            var headerBytes = Encoding.UTF8.GetBytes(header);
            await ns.WriteAsync(headerBytes, 0, headerBytes.Length);
            await ns.WriteAsync(b, 0, b.Length);
            try { ns.Close(); } catch { }
        }

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
<head>
  <meta charset='utf-8'>
  <meta name='viewport' content='width=device-width, initial-scale=1.0'>
  <title>Phone MJPEG Stream - Advanced Controls</title>
  <style>
    * { box-sizing: border-box; }
    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; margin: 0; padding: 20px; background: #f5f5f5; }
    .container { max-width: 1200px; margin: 0 auto; }
    h1 { color: #333; margin-top: 0; }
    .control-panel { background: white; border-radius: 8px; box-shadow: 0 2px 8px rgba(0,0,0,0.1); padding: 20px; margin-bottom: 20px; }
    .section { margin-bottom: 20px; }
    .section-title { font-weight: 600; color: #333; margin-bottom: 12px; font-size: 14px; text-transform: uppercase; letter-spacing: 0.5px; }
    .button-group { display: flex; gap: 10px; flex-wrap: wrap; }
    button { padding: 10px 20px; border: none; border-radius: 4px; background: #007bff; color: white; cursor: pointer; font-size: 14px; font-weight: 500; transition: background 0.2s; }
    button:hover { background: #0056b3; }
    button.secondary { background: #6c757d; }
    button.secondary:hover { background: #5a6268; }
    button.danger { background: #dc3545; }
    button.danger:hover { background: #c82333; }
    button.rotation-btn { padding: 8px 16px; background: #17a2b8; }
    button.rotation-btn:hover { background: #138496; }
    button.rotation-btn.active { background: #0c5460; font-weight: bold; }
    
    .control-row { display: flex; gap: 20px; flex-wrap: wrap; align-items: flex-end; margin-bottom: 15px; }
    .control-row > div { flex: 1; min-width: 200px; }
    
    label { display: block; margin-bottom: 6px; font-weight: 500; color: #333; font-size: 13px; }
    select, input[type='range'], input[type='number'] { width: 100%; padding: 8px 12px; border: 1px solid #ddd; border-radius: 4px; font-size: 14px; }
    input[type='range'] { padding: 0; }
    
    .slider-value { display: inline-block; background: #f0f0f0; padding: 4px 8px; border-radius: 3px; font-size: 12px; margin-left: 8px; }
    
    .toggle { display: flex; gap: 8px; }
    .toggle input[type='checkbox'] { margin-right: 8px; cursor: pointer; width: auto; }
    
    .video-container { background: #000; border-radius: 8px; overflow: hidden; box-shadow: 0 2px 8px rgba(0,0,0,0.2); margin-bottom: 20px; }
    img { width: 100%; height: auto; display: block; }
    
    .status-box { background: #e8f4f8; border-left: 4px solid #17a2b8; padding: 12px; border-radius: 4px; margin-bottom: 15px; font-size: 13px; color: #333; }
    
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); gap: 20px; }
    
    .info-text { font-size: 12px; color: #666; margin-top: 4px; }
    
    input[type='checkbox'] { cursor: pointer; }
    
    .rotation-buttons { display: flex; gap: 8px; flex-wrap: wrap; }
    .rotation-preview { margin-top: 12px; padding: 10px; background: #f9f9f9; border-radius: 4px; text-align: center; font-size: 12px; color: #666; }
  </style>
</head>
<body>
  <div class='container'>
    <h1>📷 Phone MJPEG Stream - Advanced Controls</h1>
    
    <div class='control-panel'>
      <div class='section'>
        <div class='section-title'>Stream Controls</div>
        <div class='button-group'>
          <button onclick='startStream()'>▶ Start Stream</button>
          <button onclick='stopStream()' class='danger'>⏹ Stop Stream</button>
        </div>
        <div class='status-box' id='status' style='margin-top: 12px;'>Waiting for commands...</div>
      </div>
    </div>

    <div class='video-container'>
      <img id='mjpeg' src='/stream.mjpg' alt='MJPEG Stream'/>
    </div>

    <div class='grid'>
      <!-- Resolution Settings -->
      <div class='control-panel'>
        <div class='section'>
          <div class='section-title'>Resolution</div>
          <div class='control-row'>
            <div>
              <label for='resolutionSelect'>Select Resolution:</label>
              <select id='resolutionSelect' onchange='changeResolution()'>
                <option>Loading...</option>
              </select>
              <div class='info-text' id='currentRes'></div>
            </div>
          </div>
        </div>
      </div>

      <!-- Image Rotation -->
      <div class='control-panel'>
        <div class='section'>
          <div class='section-title'>Image Rotation</div>
          <div class='rotation-buttons' id='rotationButtons'>
            <button class='rotation-btn active' onclick='setRotation(0)'>0°</button>
            <button class='rotation-btn' onclick='setRotation(90)'>90°</button>
            <button class='rotation-btn' onclick='setRotation(180)'>180°</button>
            <button class='rotation-btn' onclick='setRotation(270)'>270°</button>
          </div>
          <div class='rotation-preview' id='rotationPreview'>Current: 0°</div>
        </div>
      </div>

      <!-- Auto Controls -->
      <div class='control-panel'>
        <div class='section'>
          <div class='section-title'>Auto Controls</div>
          <div class='control-row'>
            <label class='toggle'>
              <input type='checkbox' id='autoExposure' onchange='toggleAutoExposure(this.checked)' checked>
              <span>Auto Exposure</span>
            </label>
          </div>
          <div class='control-row'>
            <label class='toggle'>
              <input type='checkbox' id='autoFocus' onchange='toggleAutoFocus(this.checked)' checked>
              <span>Auto Focus</span>
            </label>
          </div>
        </div>
      </div>

      <!-- Exposure Settings -->
      <div class='control-panel'>
        <div class='section'>
          <div class='section-title'>Exposure (Manual)</div>
          <div class='control-row'>
            <div>
              <label for='exposureSlider'>Exposure Time (µs):
                <span class='slider-value' id='exposureValue'>0</span>
              </label>
              <input type='range' id='exposureSlider' min='0' max='30000' value='1000' step='100' 
                oninput='updateExposure(this.value)'>
              <div class='info-text'>Range: <span id='exposureRange'></span></div>
            </div>
          </div>
        </div>
      </div>

      <!-- ISO Settings -->
      <div class='control-panel'>
        <div class='section'>
          <div class='section-title'>ISO (Manual)</div>
          <div class='control-row'>
            <div>
              <label for='isoSlider'>ISO Sensitivity:
                <span class='slider-value' id='isoValue'>100</span>
              </label>
              <input type='range' id='isoSlider' min='100' max='3200' value='400' step='50' 
                oninput='updateISO(this.value)'>
              <div class='info-text'>Range: <span id='isoRange'></span></div>
            </div>
          </div>
        </div>
      </div>

      <!-- Focus Settings -->
      <div class='control-panel'>
        <div class='section'>
          <div class='section-title'>Focus (Manual)</div>
          <div class='control-row'>
            <div>
              <label for='focusSlider'>Focus Distance:
                <span class='slider-value' id='focusValue'>0</span>
              </label>
              <input type='range' id='focusSlider' min='0' max='10' value='0' step='0.1' 
                oninput='updateFocus(this.value)'>
              <div class='info-text'>0 = Infinity, Higher = Closer. Range: <span id='focusRange'></span></div>
            </div>
          </div>
        </div>
      </div>

      <!-- White Balance -->
      <div class='control-panel'>
        <div class='section'>
          <div class='section-title'>White Balance</div>
          <div class='control-row'>
            <div>
              <label for='whiteBalanceSelect'>Mode:</label>
              <select id='whiteBalanceSelect' onchange='changeWhiteBalance(this.value)'>
                <option value='Auto'>Auto</option>
                <option value='Incandescent'>Incandescent</option>
                <option value='Fluorescent'>Fluorescent</option>
                <option value='WarmFluorescent'>Warm Fluorescent</option>
                <option value='Daylight'>Daylight</option>
                <option value='CloudyDaylight'>Cloudy Daylight</option>
                <option value='Twilight'>Twilight</option>
                <option value='Shade'>Shade</option>
              </select>
            </div>
          </div>
        </div>
      </div>

      <!-- Exposure Compensation -->
      <div class='control-panel'>
        <div class='section'>
          <div class='section-title'>Exposure Compensation</div>
          <div class='control-row'>
            <div>
              <label for='expCompSlider'>Compensation:
                <span class='slider-value' id='expCompValue'>0</span>
              </label>
              <input type='range' id='expCompSlider' min='-2' max='2' value='0' step='0.1' 
                oninput='updateExposureComp(this.value)'>
              <div class='info-text'>-2.0 (Darker) to +2.0 (Brighter)</div>
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>

<script>
const STATUS_BOX = document.getElementById('status');
let currentRotation = 0;

function setStatus(msg, type = 'info') {
  STATUS_BOX.textContent = msg;
  STATUS_BOX.style.borderLeftColor = type === 'error' ? '#dc3545' : type === 'success' ? '#28a745' : '#17a2b8';
  STATUS_BOX.style.background = type === 'error' ? '#f8d7da' : type === 'success' ? '#d4edda' : '#e8f4f8';
}

async function loadResolutions() {
  try {
    const res = await fetch('/resolutions');
    const resolutions = await res.json();
    const select = document.getElementById('resolutionSelect');
    select.innerHTML = '';
    resolutions.forEach((r, idx) => {
      const opt = document.createElement('option');
      opt.value = idx;
      opt.textContent = r.label;
      select.appendChild(opt);
    });
    
    const current = await fetch('/resolution');
    const currentRes = await current.json();
    document.getElementById('currentRes').textContent = '(Current: ' + currentRes.label + ')';
  } catch (e) {
    setStatus('Failed to load resolutions: ' + e.message, 'error');
  }
}

async function loadControls() {
  try {
    const res = await fetch('/controls');
    const controls = await res.json();
    
    // Update sliders and ranges
    const expMin = Math.round(controls.exposure.min / 1000);
    const expMax = Math.round(controls.exposure.max / 1000);
    document.getElementById('exposureSlider').min = expMin;
    document.getElementById('exposureSlider').max = expMax;
    document.getElementById('exposureRange').textContent = expMin + ' - ' + expMax + ' µs';
    
    document.getElementById('isoSlider').min = controls.iso.min;
    document.getElementById('isoSlider').max = controls.iso.max;
    document.getElementById('isoRange').textContent = controls.iso.min + ' - ' + controls.iso.max;
    
    document.getElementById('focusSlider').max = controls.focus.max.toFixed(2);
    document.getElementById('focusRange').textContent = '0 - ' + controls.focus.max.toFixed(2);
    
    // Load current rotation
    const rotRes = await fetch('/getrotation');
    const rotData = await rotRes.json();
    currentRotation = rotData.rotation;
    updateRotationUI(currentRotation);
    
    setStatus('Controls loaded successfully', 'success');
  } catch (e) {
    setStatus('Failed to load controls: ' + e.message, 'error');
  }
}

function updateRotationUI(rotation) {
  // Update active button
  const buttons = document.querySelectorAll('.rotation-btn');
  buttons.forEach(btn => {
    btn.classList.remove('active');
    if (btn.textContent.includes(rotation + '°')) {
      btn.classList.add('active');
    }
  });
  
  // Update preview text
  document.getElementById('rotationPreview').textContent = 'Current: ' + rotation + '°';
}

async function setRotation(degrees) {
  currentRotation = degrees;
  updateRotationUI(degrees);
  setStatus('Setting image rotation to ' + degrees + '°...');
  
  try {
    await fetch('/rotation?' + degrees);
    setStatus('Image rotation set to ' + degrees + '°', 'success');
  } catch (e) {
    setStatus('Error setting rotation: ' + e.message, 'error');
  }
}

async function startStream() {
  try {
    await fetch('/start');
    setTimeout(() => {
      document.getElementById('mjpeg').src = '';
      document.getElementById('mjpeg').src = '/stream.mjpg?t=' + Date.now();
    }, 500);
    setStatus('Camera stream started', 'success');
  } catch (e) {
    setStatus('Failed to start stream: ' + e.message, 'error');
  }
}

async function stopStream() {
  try {
    document.getElementById('mjpeg').src = '';
    await fetch('/stop');
    setStatus('Camera stream stopped', 'success');
  } catch (e) {
    setStatus('Failed to stop stream: ' + e.message, 'error');
  }
}

async function changeResolution() {
  const select = document.getElementById('resolutionSelect');
  const index = select.value;
  setStatus('Changing resolution...');
  
  try {
    await fetch('/setresolution?' + index);
    setTimeout(() => {
      document.getElementById('mjpeg').src = '';
      setTimeout(() => document.getElementById('mjpeg').src = '/stream.mjpg?t=' + Date.now(), 500);
    }, 1000);
    setStatus('Resolution changed', 'success');
  } catch (e) {
    setStatus('Error changing resolution: ' + e.message, 'error');
  }
}

async function updateExposure(value) {
  document.getElementById('exposureValue').textContent = value;
  try {
    await fetch('/exposure?' + value);
  } catch (e) {
    setStatus('Error setting exposure: ' + e.message, 'error');
  }
}

async function updateISO(value) {
  document.getElementById('isoValue').textContent = value;
  try {
    await fetch('/iso?' + value);
  } catch (e) {
    setStatus('Error setting ISO: ' + e.message, 'error');
  }
}

async function updateFocus(value) {
  document.getElementById('focusValue').textContent = parseFloat(value).toFixed(2);
  try {
    await fetch('/focus?' + value);
  } catch (e) {
    setStatus('Error setting focus: ' + e.message, 'error');
  }
}

async function updateExposureComp(value) {
  document.getElementById('expCompValue').textContent = parseFloat(value).toFixed(1);
  try {
    await fetch('/exposurecomp?' + value);
  } catch (e) {
    setStatus('Error setting exposure compensation: ' + e.message, 'error');
  }
}

async function toggleAutoExposure(enabled) {
  try {
    await fetch('/autoexposure?' + enabled);
    setStatus('Auto exposure: ' + (enabled ? 'ON' : 'OFF'), 'success');
  } catch (e) {
    setStatus('Error toggling auto exposure: ' + e.message, 'error');
  }
}

async function toggleAutoFocus(enabled) {
  try {
    await fetch('/autofocus?' + enabled);
    setStatus('Auto focus: ' + (enabled ? 'ON' : 'OFF'), 'success');
  } catch (e) {
    setStatus('Error toggling auto focus: ' + e.message, 'error');
  }
}

async function changeWhiteBalance(mode) {
  try {
    await fetch('/whitebalance?' + mode);
    setStatus('White balance set to: ' + mode, 'success');
  } catch (e) {
    setStatus('Error setting white balance: ' + e.message, 'error');
  }
}

// Initialize on load
window.addEventListener('load', () => {
  loadResolutions();
  loadControls();
  setStatus('Interface ready. Click Start Stream to begin.', 'success');
});
</script>
</body>
</html>";
    }
}
