using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Media;
using Android.OS;
using Android.Util;
using Java.IO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MjpegStreamer
{
    // Camera2 API implementation with manual controls, resolution management, image rotation, photo capture, and video recording.
    public class CameraService : Java.Lang.Object, IDisposable
    {
        readonly Context context;
        CameraManager cameraManager;
        CameraCaptureSession captureSession;
        CameraDevice cameraDevice;
        ImageReader imageReader;
        HandlerThread handlerThread;
        Handler backgroundHandler;
        CameraCharacteristics characteristics;
        
        int width = 640;
        int height = 480;
        bool isRunning;
        readonly object sync = new object();
        
        // Available resolutions
        private List<Resolution> supportedResolutions = new List<Resolution>();
        private int currentResolutionIndex = 0;

        // Manual control properties
        public int MinExposure { get; private set; }
        public int MaxExposure { get; private set; }
        public int MinISO { get; private set; }
        public int MaxISO { get; private set; }
        public float MinFocusDistance { get; private set; }
        public float MaxFocusDistance { get; private set; }

        // Current manual control values
        private long currentExposureTime;
        private int currentISO;
        private float currentFocusDistance = 0;
        private ControlAEMode currentAEMode = ControlAEMode.On;
        private ControlAFMode currentAFMode = ControlAFMode.Auto;
        private ControlAwbMode currentAwbMode = ControlAwbMode.Auto;
        
        // Image rotation
        private int imageRotationDegrees = 0; // 0, 90, 180, 270

        // Photo and video recording
        private bool isRecordingVideo = false;
        private MediaRecorder mediaRecorder;
        private string photoDirectory;
        private string videoDirectory;

        public event Action<byte[]> OnFrameAvailable;
        public event Action<string> OnStatusChanged;
        public event Action<string> OnPhotoSaved;
        public event Action<string> OnVideoSaved;

        public class Resolution
        {
            public int Width { get; set; }
            public int Height { get; set; }
            
            public Resolution(int w, int h)
            {
                Width = w;
                Height = h;
            }

            public override string ToString()
            {
                return $"{Width}x{Height}";
            }
        }

        public CameraService(Context context)
        {
            this.context = context;
            InitializeDirectories();
        }

        private void InitializeDirectories()
        {
            try
            {
                string picturesPath = Android.OS.Environment.GetExternalFilesDir(Android.OS.Environment.DirectoryPictures).AbsolutePath;
                photoDirectory = Path.Combine(picturesPath, "Photos");
                videoDirectory = Path.Combine(picturesPath, "Videos");

                Directory.CreateDirectory(photoDirectory);
                Directory.CreateDirectory(videoDirectory);

                OnStatusChanged?.Invoke($"Photo directory: {photoDirectory}\nVideo directory: {videoDirectory}");
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke("Error initializing directories: " + ex.Message);
            }
        }

        /// <summary>
        /// Get list of supported resolutions
        /// </summary>
        public List<Resolution> GetSupportedResolutions()
        {
            lock (sync)
            {
                return new List<Resolution>(supportedResolutions);
            }
        }

        /// <summary>
        /// Get current resolution
        /// </summary>
        public Resolution GetCurrentResolution()
        {
            lock (sync)
            {
                return new Resolution(width, height);
            }
        }

        /// <summary>
        /// Set image rotation in degrees (0, 90, 180, 270)
        /// </summary>
        public void SetImageRotation(int degrees)
        {
            lock (sync)
            {
                if (degrees == 0 || degrees == 90 || degrees == 180 || degrees == 270)
                {
                    imageRotationDegrees = degrees;
                    OnStatusChanged?.Invoke($"Image rotation set to {degrees}°");
                }
                else
                {
                    OnStatusChanged?.Invoke($"Invalid rotation degree: {degrees}. Use 0, 90, 180, or 270.");
                }
            }
        }

        /// <summary>
        /// Get current image rotation
        /// </summary>
        public int GetImageRotation()
        {
            lock (sync)
            {
                return imageRotationDegrees;
            }
        }

        /// <summary>
        /// Capture a photo and save to storage
        /// </summary>
        public Task<bool> CapturePhotoAsync()
        {
            return Task.Run(() =>
            {
                lock (sync)
                {
                    if (!isRunning)
                    {
                        OnStatusChanged?.Invoke("Camera not running. Cannot capture photo.");
                        return false;
                    }

                    try
                    {
                        string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                        string photoFile = Path.Combine(photoDirectory, $"Photo_{timestamp}.jpg");

                        OnStatusChanged?.Invoke("Capturing photo...");
                        
                        // This will be called from the next frame in OnFrameReceived
                        captureNextFrameAsPhoto = true;
                        nextPhotoPath = photoFile;

                        return true;
                    }
                    catch (Exception ex)
                    {
                        OnStatusChanged?.Invoke("Photo capture error: " + ex.Message);
                        return false;
                    }
                }
            });
        }

        private bool captureNextFrameAsPhoto = false;
        private string nextPhotoPath = "";

        /// <summary>
        /// Start video recording
        /// </summary>
        public Task<bool> StartVideoRecordingAsync()
        {
            return Task.Run(() =>
            {
                lock (sync)
                {
                    if (!isRunning)
                    {
                        OnStatusChanged?.Invoke("Camera not running. Cannot start recording.");
                        return false;
                    }

                    if (isRecordingVideo)
                    {
                        OnStatusChanged?.Invoke("Already recording video.");
                        return false;
                    }

                    try
                    {
                        string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                        string videoFile = Path.Combine(videoDirectory, $"Video_{timestamp}.mp4");

                        mediaRecorder = new MediaRecorder();
                        mediaRecorder.SetAudioSource(AudioSource.Mic);
                        mediaRecorder.SetVideoSource(VideoSource.Camera);
                        mediaRecorder.SetOutputFormat(OutputFormat.Mpeg4);
                        mediaRecorder.SetVideoEncoder(VideoEncoder.H264);
                        mediaRecorder.SetAudioEncoder(AudioEncoder.Aac);
                        mediaRecorder.SetVideoSize(width, height);
                        mediaRecorder.SetVideoFrameRate(30);
                        mediaRecorder.SetAudioSamplingRate(44100);
                        mediaRecorder.SetOutputFile(videoFile);

                        mediaRecorder.Prepare();
                        mediaRecorder.Start();

                        isRecordingVideo = true;
                        OnStatusChanged?.Invoke($"Video recording started: {videoFile}");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        OnStatusChanged?.Invoke("Video recording error: " + ex.Message);
                        mediaRecorder?.Release();
                        mediaRecorder = null;
                        isRecordingVideo = false;
                        return false;
                    }
                }
            });
        }

        /// <summary>
        /// Stop video recording
        /// </summary>
        public Task<bool> StopVideoRecordingAsync()
        {
            return Task.Run(() =>
            {
                lock (sync)
                {
                    if (!isRecordingVideo)
                    {
                        OnStatusChanged?.Invoke("Not currently recording video.");
                        return false;
                    }

                    try
                    {
                        mediaRecorder?.Stop();
                        mediaRecorder?.Release();
                        mediaRecorder = null;
                        isRecordingVideo = false;

                        OnStatusChanged?.Invoke("Video recording stopped.");
                        OnVideoSaved?.Invoke("Video saved successfully");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        OnStatusChanged?.Invoke("Video stop error: " + ex.Message);
                        return false;
                    }
                }
            });
        }

        /// <summary>
        /// Get video recording status
        /// </summary>
        public bool IsRecordingVideo()
        {
            lock (sync)
            {
                return isRecordingVideo;
            }
        }

        /// <summary>
        /// Rotate a bitmap by specified degrees
        /// </summary>
        private Bitmap RotateBitmap(Bitmap source, int degrees)
        {
            if (degrees == 0) return source;
            
            try
            {
                var matrix = new Matrix();
                matrix.PostRotate(degrees);
                var rotated = Bitmap.CreateBitmap(source, 0, 0, source.Width, source.Height, matrix, true);
                if (rotated != source)
                {
                    source.Dispose();
                }
                return rotated;
            }
            catch (Exception ex)
            {
                Log.Debug("CameraService", "RotateBitmap error: " + ex.Message);
                return source;
            }
        }

        /// <summary>
        /// Encode rotated JPEG
        /// </summary>
        private byte[] RotateJpeg(byte[] jpegData, int degrees)
        {
            if (degrees == 0) return jpegData;

            try
            {
                var bitmap = BitmapFactory.DecodeByteArray(jpegData, 0, jpegData.Length);
                var rotated = RotateBitmap(bitmap, degrees);
                
                using (var ms = new MemoryStream())
                {
                    rotated.Compress(Bitmap.CompressFormat.Jpeg, 85, ms);
                    byte[] result = ms.ToArray();
                    rotated.Dispose();
                    return result;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("CameraService", "RotateJpeg error: " + ex.Message);
                return jpegData;
            }
        }

        /// <summary>
        /// Set resolution by index
        /// </summary>
        public Task<bool> SetResolutionAsync(int index)
        {
            return Task.Run(() =>
            {
                lock (sync)
                {
                    if (!isRunning)
                    {
                        OnStatusChanged?.Invoke("Camera not running. Start camera first.");
                        return false;
                    }

                    if (index < 0 || index >= supportedResolutions.Count)
                    {
                        OnStatusChanged?.Invoke($"Invalid resolution index: {index}");
                        return false;
                    }

                    try
                    {
                        StopCaptureInternal();
                        var res = supportedResolutions[index];
                        width = res.Width;
                        height = res.Height;
                        currentResolutionIndex = index;

                        var result = StartCaptureInternal();
                        return result;
                    }
                    catch (Exception ex)
                    {
                        OnStatusChanged?.Invoke("SetResolution error: " + ex.Message);
                        return false;
                    }
                }
            });
        }

        /// <summary>
        /// Set resolution by width and height
        /// </summary>
        public Task<bool> SetResolutionByDimensionsAsync(int w, int h)
        {
            return Task.Run(() =>
            {
                lock (sync)
                {
                    int index = -1;
                    for (int i = 0; i < supportedResolutions.Count; i++)
                    {
                        if (supportedResolutions[i].Width == w && supportedResolutions[i].Height == h)
                        {
                            index = i;
                            break;
                        }
                    }

                    if (index == -1)
                    {
                        OnStatusChanged?.Invoke($"Resolution {w}x{h} not supported");
                        return false;
                    }

                    return SetResolutionAsync(index).Result;
                }
            });
        }

        public Task<bool> StartCaptureAsync()
        {
            return Task.Run(() =>
            {
                lock (sync)
                {
                    return StartCaptureInternal();
                }
            });
        }

        private bool StartCaptureInternal()
        {
            try
            {
                if (isRunning) 
                { 
                    OnStatusChanged?.Invoke("Camera already running."); 
                    return true; 
                }

                OnStatusChanged?.Invoke("Opening camera...");

                cameraManager = (CameraManager)context.GetSystemService(Context.CameraService);
                if (cameraManager == null)
                {
                    OnStatusChanged?.Invoke("Camera manager not available.");
                    return false;
                }

                string[] cameraIds = cameraManager.GetCameraIdList();
                if (cameraIds.Length == 0)
                {
                    OnStatusChanged?.Invoke("No camera available.");
                    return false;
                }

                string cameraId = cameraIds[0];
                characteristics = cameraManager.GetCameraCharacteristics(cameraId);
                var map = (StreamConfigurationMap)characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
                
                if (map == null)
                {
                    OnStatusChanged?.Invoke("Cannot get stream configuration map.");
                    return false;
                }

                InitializeControlRanges();

                var outputSizes = map.GetOutputSizes(ImageFormatType.Jpeg);
                if (outputSizes.Length == 0)
                {
                    OnStatusChanged?.Invoke("No supported output sizes.");
                    return false;
                }

                supportedResolutions.Clear();
                supportedResolutions.AddRange(outputSizes.Select(s => new Resolution(s.Width, s.Height)).Distinct());
                supportedResolutions = supportedResolutions.OrderByDescending(r => r.Width * r.Height).ToList();
                
                OnStatusChanged?.Invoke($"Supported resolutions: {string.Join(", ", supportedResolutions)}");

                Android.Util.Size chosen = outputSizes[0];
                foreach (var size in outputSizes)
                {
                    if (size.Width == width && size.Height == height)
                    {
                        chosen = size;
                        break;
                    }
                }

                width = chosen.Width;
                height = chosen.Height;

                for (int i = 0; i < supportedResolutions.Count; i++)
                {
                    if (supportedResolutions[i].Width == width && supportedResolutions[i].Height == height)
                    {
                        currentResolutionIndex = i;
                        break;
                    }
                }

                handlerThread = new HandlerThread("CameraBackground");
                handlerThread.Start();
                backgroundHandler = new Handler(handlerThread.Looper);

                imageReader = ImageReader.NewInstance(width, height, ImageFormatType.Jpeg, 2);
                imageReader.SetOnImageAvailableListener(new ImageAvailableListener(this), backgroundHandler);

                cameraManager.OpenCamera(cameraId, new CameraDeviceCallback(this), backgroundHandler);

                isRunning = true;
                OnStatusChanged?.Invoke($"Camera started. Resolution: {width}x{height}");
                return true;
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke("StartCapture error: " + ex.Message);
                StopCaptureInternal();
                return false;
            }
        }

        private void InitializeControlRanges()
        {
            try
            {
                var exposureRange = (Android.Util.Range)characteristics.Get(CameraCharacteristics.SensorInfoExposureTimeRange);
                MinExposure = (int)(exposureRange.Lower as Java.Lang.Long);
                MaxExposure = (int)(exposureRange.Upper as Java.Lang.Long);
                currentExposureTime = MinExposure;

                var isoRange = (Android.Util.Range)characteristics.Get(CameraCharacteristics.SensorInfoSensitivityRange);
                MinISO = (int)(isoRange.Lower as Java.Lang.Integer);
                MaxISO = (int)(isoRange.Upper as Java.Lang.Integer);
                currentISO = MinISO;

                var focusRange = characteristics.Get(CameraCharacteristics.LensInfoMinimumFocusDistance);
                MaxFocusDistance = focusRange != null ? (float)focusRange : 0;
                MinFocusDistance = 0;
            }
            catch (Exception ex)
            {
                Log.Debug("CameraService", "Error initializing control ranges: " + ex.Message);
            }
        }

        /// <summary>
        /// Get control ranges as JSON
        /// </summary>
        public string GetControlRangesJson()
        {
            lock (sync)
            {
                return $@"{{
  ""exposure"": {{ ""min"": {MinExposure}, ""max"": {MaxExposure}, ""current"": {currentExposureTime} }},
  ""iso"": {{ ""min"": {MinISO}, ""max"": {MaxISO}, ""current"": {currentISO} }},
  ""focus"": {{ ""min"": 0, ""max"": {MaxFocusDistance}, ""current"": {currentFocusDistance} }},
  ""aeMode"": ""{currentAEMode}"",
  ""afMode"": ""{currentAFMode}"",
  ""awbMode"": ""{currentAwbMode}"",
  ""rotation"": {imageRotationDegrees},
  ""isRecordingVideo"": {(isRecordingVideo ? "true" : "false")}
}}";
            }
        }

        /// <summary>
        /// Set manual exposure time (in microseconds)
        /// </summary>
        public void SetExposureTime(long exposureTimeUs)
        {
            if (captureSession == null || cameraDevice == null) return;

            try
            {
                lock (sync)
                {
                    long exposureTimeNs = exposureTimeUs * 1000;
                    exposureTimeNs = Math.Max(MinExposure, Math.Min(MaxExposure, exposureTimeNs));
                    currentExposureTime = exposureTimeNs;

                    var builder = cameraDevice.CreateCaptureRequest(CameraTemplate.Preview);
                    builder.AddTarget(imageReader.Surface);
                    
                    builder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.Off);
                    builder.Set(CaptureRequest.SensorExposureTime, exposureTimeNs);
                    builder.Set(CaptureRequest.SensorSensitivity, currentISO);

                    captureSession.SetRepeatingRequest(builder.Build(), null, backgroundHandler);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("CameraService", "SetExposureTime error: " + ex.Message);
            }
        }

        /// <summary>
        /// Set manual ISO sensitivity
        /// </summary>
        public void SetISO(int iso)
        {
            if (captureSession == null || cameraDevice == null) return;

            try
            {
                lock (sync)
                {
                    iso = Math.Max(MinISO, Math.Min(MaxISO, iso));
                    currentISO = iso;

                    var builder = cameraDevice.CreateCaptureRequest(CameraTemplate.Preview);
                    builder.AddTarget(imageReader.Surface);
                    
                    builder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.Off);
                    builder.Set(CaptureRequest.SensorExposureTime, currentExposureTime);
                    builder.Set(CaptureRequest.SensorSensitivity, iso);

                    captureSession.SetRepeatingRequest(builder.Build(), null, backgroundHandler);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("CameraService", "SetISO error: " + ex.Message);
            }
        }

        /// <summary>
        /// Set manual focus distance
        /// </summary>
        public void SetFocusDistance(float focusDistance)
        {
            if (captureSession == null || cameraDevice == null) return;

            try
            {
                lock (sync)
                {
                    focusDistance = Math.Max(MinFocusDistance, Math.Min(MaxFocusDistance, focusDistance));
                    currentFocusDistance = focusDistance;

                    var builder = cameraDevice.CreateCaptureRequest(CameraTemplate.Preview);
                    builder.AddTarget(imageReader.Surface);
                    
                    builder.Set(CaptureRequest.ControlAfMode, (int)ControlAFMode.Off);
                    builder.Set(CaptureRequest.LensInfocusDistance, focusDistance);

                    captureSession.SetRepeatingRequest(builder.Build(), null, backgroundHandler);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("CameraService", "SetFocusDistance error: " + ex.Message);
            }
        }

        /// <summary>
        /// Enable/Disable auto exposure
        /// </summary>
        public void SetAutoExposure(bool enable)
        {
            if (captureSession == null || cameraDevice == null) return;

            try
            {
                lock (sync)
                {
                    var builder = cameraDevice.CreateCaptureRequest(CameraTemplate.Preview);
                    builder.AddTarget(imageReader.Surface);
                    
                    if (enable)
                    {
                        currentAEMode = ControlAEMode.On;
                        builder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.On);
                    }
                    else
                    {
                        currentAEMode = ControlAEMode.Off;
                        builder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.Off);
                        builder.Set(CaptureRequest.SensorExposureTime, currentExposureTime);
                        builder.Set(CaptureRequest.SensorSensitivity, currentISO);
                    }

                    captureSession.SetRepeatingRequest(builder.Build(), null, backgroundHandler);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("CameraService", "SetAutoExposure error: " + ex.Message);
            }
        }

        /// <summary>
        /// Enable/Disable auto focus
        /// </summary>
        public void SetAutoFocus(bool enable)
        {
            if (captureSession == null || cameraDevice == null) return;

            try
            {
                lock (sync)
                {
                    var builder = cameraDevice.CreateCaptureRequest(CameraTemplate.Preview);
                    builder.AddTarget(imageReader.Surface);
                    
                    if (enable)
                    {
                        currentAFMode = ControlAFMode.Auto;
                        builder.Set(CaptureRequest.ControlAfMode, (int)ControlAFMode.Auto);
                    }
                    else
                    {
                        currentAFMode = ControlAFMode.Off;
                        builder.Set(CaptureRequest.ControlAfMode, (int)ControlAFMode.Off);
                        builder.Set(CaptureRequest.LensInfocusDistance, currentFocusDistance);
                    }

                    captureSession.SetRepeatingRequest(builder.Build(), null, backgroundHandler);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("CameraService", "SetAutoFocus error: " + ex.Message);
            }
        }

        /// <summary>
        /// Set white balance mode
        /// </summary>
        public void SetWhiteBalance(ControlAwbMode mode)
        {
            if (captureSession == null || cameraDevice == null) return;

            try
            {
                lock (sync)
                {
                    currentAwbMode = mode;
                    var builder = cameraDevice.CreateCaptureRequest(CameraTemplate.Preview);
                    builder.AddTarget(imageReader.Surface);
                    builder.Set(CaptureRequest.ControlAwbMode, (int)mode);

                    captureSession.SetRepeatingRequest(builder.Build(), null, backgroundHandler);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("CameraService", "SetWhiteBalance error: " + ex.Message);
            }
        }

        /// <summary>
        /// Set exposure compensation
        /// </summary>
        public void SetExposureCompensation(float compensation)
        {
            if (captureSession == null || cameraDevice == null) return;

            try
            {
                lock (sync)
                {
                    var builder = cameraDevice.CreateCaptureRequest(CameraTemplate.Preview);
                    builder.AddTarget(imageReader.Surface);
                    
                    builder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.On);
                    compensation = Math.Max(-2.0f, Math.Min(2.0f, compensation));
                    builder.Set(CaptureRequest.ControlAeExposureCompensation, (int)(compensation * 2));

                    captureSession.SetRepeatingRequest(builder.Build(), null, backgroundHandler);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("CameraService", "SetExposureCompensation error: " + ex.Message);
            }
        }

        public void StopCapture()
        {
            lock (sync)
            {
                StopCaptureInternal();
            }
        }

        private void StopCaptureInternal()
        {
            try
            {
                if (!isRunning) 
                { 
                    OnStatusChanged?.Invoke("Camera not running."); 
                    return; 
                }

                captureSession?.StopRepeating();
                captureSession?.Close();
                captureSession = null;

                cameraDevice?.Close();
                cameraDevice = null;

                imageReader?.Close();
                imageReader = null;

                handlerThread?.QuitSafely();
                handlerThread = null;

                isRunning = false;
                OnStatusChanged?.Invoke("Camera stopped.");
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke("Stop error: " + ex.Message);
            }
        }

        internal void OnCameraOpened(CameraDevice camera)
        {
            try
            {
                lock (sync)
                {
                    if (!isRunning) return;
                    
                    cameraDevice = camera;

                    var outputSurface = imageReader.Surface;
                    var surfaces = new[] { outputSurface };
                    cameraDevice.CreateCaptureSession(surfaces, 
                        new CaptureSessionCallback(this), backgroundHandler);
                }
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke("OnCameraOpened error: " + ex.Message);
            }
        }

        internal void OnCaptureSessionConfigured(CameraCaptureSession session)
        {
            try
            {
                lock (sync)
                {
                    if (!isRunning) return;

                    captureSession = session;

                    var captureRequestBuilder = cameraDevice.CreateCaptureRequest(CameraTemplate.Preview);
                    captureRequestBuilder.AddTarget(imageReader.Surface);

                    captureRequestBuilder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.On);
                    captureRequestBuilder.Set(CaptureRequest.ControlAfMode, (int)ControlAFMode.Auto);
                    captureRequestBuilder.Set(CaptureRequest.ControlAwbMode, (int)ControlAwbMode.Auto);

                    captureSession.SetRepeatingRequest(captureRequestBuilder.Build(), null, backgroundHandler);
                }
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke("OnCaptureSessionConfigured error: " + ex.Message);
            }
        }

        internal void OnFrameReceived(Image image)
        {
            try
            {
                if (image.Format != ImageFormatType.Jpeg)
                {
                    image.Close();
                    return;
                }

                var planes = image.GetPlanes();
                var buffer = planes[0].Buffer;
                byte[] jpeg = new byte[buffer.Remaining()];
                buffer.Get(jpeg);
                image.Close();

                // Apply rotation if needed
                if (imageRotationDegrees != 0)
                {
                    jpeg = RotateJpeg(jpeg, imageRotationDegrees);
                }

                // Check if we need to save this frame as a photo
                if (captureNextFrameAsPhoto)
                {
                    try
                    {
                        File.WriteAllBytes(nextPhotoPath, jpeg);
                        OnStatusChanged?.Invoke($"Photo saved: {nextPhotoPath}");
                        OnPhotoSaved?.Invoke(nextPhotoPath);
                    }
                    catch (Exception ex)
                    {
                        OnStatusChanged?.Invoke($"Error saving photo: {ex.Message}");
                    }
                    finally
                    {
                        captureNextFrameAsPhoto = false;
                        nextPhotoPath = "";
                    }
                }

                OnFrameAvailable?.Invoke(jpeg);
            }
            catch (Exception ex)
            {
                Log.Debug("CameraService", "OnFrameReceived error: " + ex.Message);
            }
        }

        public void Dispose()
        {
            if (isRecordingVideo)
            {
                try
                {
                    mediaRecorder?.Stop();
                    mediaRecorder?.Release();
                }
                catch { }
            }
            StopCapture();
        }

        private class CameraDeviceCallback : CameraDevice.StateCallback
        {
            readonly CameraService service;

            public CameraDeviceCallback(CameraService service)
            {
                this.service = service;
            }

            public override void OnOpened(CameraDevice camera)
            {
                service.OnCameraOpened(camera);
            }

            public override void OnDisconnected(CameraDevice camera)
            {
                camera.Close();
            }

            public override void OnError(CameraDevice camera, CameraError error)
            {
                camera.Close();
                service.OnStatusChanged?.Invoke($"Camera error: {error}");
            }
        }

        private class CaptureSessionCallback : CameraCaptureSession.StateCallback
        {
            readonly CameraService service;

            public CaptureSessionCallback(CameraService service)
            {
                this.service = service;
            }

            public override void OnConfigured(CameraCaptureSession session)
            {
                service.OnCaptureSessionConfigured(session);
            }

            public override void OnConfigureFailed(CameraCaptureSession session)
            {
                service.OnStatusChanged?.Invoke("Capture session configuration failed.");
            }
        }

        private class ImageAvailableListener : Java.Lang.Object, ImageReader.IOnImageAvailableListener
        {
            readonly CameraService service;

            public ImageAvailableListener(CameraService service)
            {
                this.service = service;
            }

            public void OnImageAvailable(ImageReader reader)
            {
                var image = reader.AcquireNextImage();
                if (image != null)
                {
                    service.OnFrameReceived(image);
                }
            }
        }
    }
}
