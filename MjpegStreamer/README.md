# MjpegStreamer (Xamarin.Android) — ZIP-ready project

What this is:
- A minimal Xamarin.Android app (Android 11 target) that starts an embedded HTTP server and serves:
  - / — an HTML page with Start/Stop controls and an <img> showing the MJPEG stream
  - /stream.mjpg — the MJPEG stream (multipart/x-mixed-replace)
  - /start and /stop — remote start/stop endpoints for camera capture

How to use:
1. Save the folder `MjpegStreamer` with the files above.
2. Zip it (instructions below) or open the folder directly in Visual Studio:
   - In Visual Studio: File → Open → Project/Solution → open the MjpegStreamer.csproj file.
3. Set Target Framework / Android SDK to Android 11 (API 30) in project properties.
4. Deploy to a physical Android device (not the emulator) running Android 11. Ensure the device and the client browser are on the same Wi‑Fi network.
5. Grant CAMERA permission when prompted.
6. Find the phone's IP address (Settings → About → Status or using `adb shell ip -f inet addr show wlan0`).
7. In a browser on the same network open: `http://<phone-ip>:8080`
8. Use the Start / Stop buttons on the page to control the camera stream.

Create a ZIP (example commands):
- macOS / Linux:
  - From the directory containing the MjpegStreamer folder:
    - zip -r MjpegStreamer.zip MjpegStreamer
- Windows PowerShell:
  - Compress-Archive -Path .\MjpegStreamer -DestinationPath .\MjpegStreamer.zip

Notes & caveats:
- This sample uses the legacy Camera API (Camera1) and a dummy SurfaceTexture. It was chosen for compactness and broad device compatibility.
- The sample does not implement authentication, TLS, or production-grade error handling — use on trusted networks only or add authentication before exposing externally.
- If you prefer a fully packaged Visual Studio solution (.sln), or a Kotlin/Java Android Studio project, tell me and I will produce that variant.

If you want I can:
- Package these files into a single downloadable ZIP for you (I will produce a zip file content inline as base64 or a downloadable attachment) — tell me which you prefer.
- Convert the project to a .sln with a specific GUIDs for direct double-click open in older Visual Studio versions.
