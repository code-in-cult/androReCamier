using Android;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using System;

namespace MjpegStreamer
{
    [Activity(Label = "MjpegStreamer", MainLauncher = true, Icon = "@mipmap/icon")]
    public class MainActivity : Activity
    {
        const int RequestCameraId = 1000;
        SimpleHttpServer server;
        CameraService cameraService;
        TextView statusText;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            SetContentView(Resource.Layout.activity_main);

            statusText = FindViewById<TextView>(Resource.Id.statusText);

            cameraService = new CameraService();
            cameraService.OnFrameAvailable += CameraService_OnFrameAvailable;
            cameraService.OnStatusChanged += CameraService_OnStatusChanged;

            server = new SimpleHttpServer(8080, cameraService, UpdateStatus);
            server.Start();

            EnsureCameraPermission();
        }

        void CameraService_OnStatusChanged(string s)
        {
            RunOnUiThread(() => statusText.Text = s);
        }

        void CameraService_OnFrameAvailable(byte[] jpeg)
        {
            server.BroadcastFrame(jpeg);
        }

        void UpdateStatus(string s)
        {
            RunOnUiThread(() => statusText.Text = s);
        }

        void EnsureCameraPermission()
        {
            if (CheckSelfPermission(Manifest.Permission.Camera) != Permission.Granted)
            {
                RequestPermissions(new[] { Manifest.Permission.Camera }, RequestCameraId);
            }
            else
            {
                UpdateStatus("Camera permission granted (or previously allowed). Use the web page to Start/Stop.");
            }
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
            if (requestCode == RequestCameraId)
            {
                if (grantResults.Length > 0 && grantResults[0] == Permission.Granted)
                {
                    UpdateStatus("Camera permission granted.");
                }
                else
                {
                    UpdateStatus("Camera permission denied. App will not be able to stream.");
                }
            }
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            server?.Stop();
            cameraService?.Dispose();
        }
    }
}
