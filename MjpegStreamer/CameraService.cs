using Android.Graphics;
using Android.Hardware;
using Android.Util;
using Java.IO;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MjpegStreamer
{
    // Simple camera service using legacy Camera API for easier cross-device support.
    // Produces JPEG byte[] frames via OnFrameAvailable.
    public class CameraService : Java.Lang.Object, Camera.IPreviewCallback, IDisposable
    {
        Camera camera;
        int width = 640;
        int height = 480;
        byte[] buffer;
        bool isRunning;
        SurfaceTexture surfaceTexture;
        readonly object sync = new object();

        public event Action<byte[]> OnFrameAvailable;
        public event Action<string> OnStatusChanged;

        public CameraService()
        {
        }

        public Task<bool> StartCaptureAsync()
        {
            return Task.Run(() =>
            {
                lock (sync)
                {
                    try
                    {
                        if (isRunning) { OnStatusChanged?.Invoke("Camera already running."); return true; }
                        OnStatusChanged?.Invoke("Opening camera...");
                        camera = Camera.Open(0); // try first camera
                        if (camera == null) { OnStatusChanged?.Invoke("No camera available."); return false; }

                        var parameters = camera.GetParameters();
                        // Choose a supported preview size close to desired
                        var supported = parameters.SupportedPreviewSizes;
                        Camera.Size chosen = null;
                        foreach (var s in supported)
                        {
                            if (s.Width == width && s.Height == height) { chosen = s; break; }
                        }
                        if (chosen == null) chosen = supported[0];
                        width = chosen.Width;
                        height = chosen.Height;

                        parameters.SetPreviewSize(width, height);
                        parameters.PreviewFormat = Android.Graphics.ImageFormatType.Nv21;
                        camera.SetParameters(parameters);

                        // Use a dummy SurfaceTexture so preview can start without a UI Surface
                        surfaceTexture = new SurfaceTexture(10);
                        camera.SetPreviewTexture(surfaceTexture);

                        // prepare buffer for NV21 frames
                        var bufferLen = width * height * 3 / 2;
                        buffer = new byte[bufferLen];
                        camera.AddCallbackBuffer(buffer);
                        camera.SetPreviewCallbackWithBuffer(this);

                        camera.StartPreview();
                        isRunning = true;
                        OnStatusChanged?.Invoke("Camera started.");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        OnStatusChanged?.Invoke("StartCapture error: " + ex.Message);
                        try { camera?.Release(); camera = null; } catch { }
                        return false;
                    }
                }
            });
        }

        public void StopCapture()
        {
            lock (sync)
            {
                try
                {
                    if (!isRunning) { OnStatusChanged?.Invoke("Camera not running."); return; }
                    camera?.SetPreviewCallbackWithBuffer(null);
                    camera?.StopPreview();
                    camera?.Release();
                    camera = null;
                    surfaceTexture?.Release();
                    surfaceTexture = null;
                    isRunning = false;
                    OnStatusChanged?.Invoke("Camera stopped.");
                }
                catch (Exception ex)
                {
                    OnStatusChanged?.Invoke("Stop error: " + ex.Message);
                }
            }
        }

        public void OnPreviewFrame(byte[] data, Camera cam)
        {
            try
            {
                // Convert NV21 -> JPEG using YuvImage
                var yuv = new YuvImage(data, ImageFormat.Nv21, width, height, null);
                using (var ms = new MemoryStream())
                {
                    yuv.CompressToJpeg(new Rect(0, 0, width, height), 70, ms);
                    var jpeg = ms.ToArray();
                    OnFrameAvailable?.Invoke(jpeg);
                }
            }
            catch (Exception e)
            {
                Log.Debug("CameraService", "OnPreviewFrame error: " + e.Message);
            }
            finally
            {
                // return buffer to camera for reuse
                try
                {
                    if (camera != null)
                        camera.AddCallbackBuffer(data);
                }
                catch { }
            }
        }

        public void Dispose()
        {
            StopCapture();
        }
    }
}
