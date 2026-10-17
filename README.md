# HandsFree

A Windows-first, local-first desktop application built with Avalonia UI and .NET 10. The shell includes explicit one-shot Windows control tests, an opt-in keyboard scroll mode, and manually started local hand tracking. Hand landmarks are displayed in the preview only; they are not connected to computer-control actions. There is no speech recognition, AI, or screen understanding.

## Prerequisites

- .NET 10 SDK
- Windows 10/11 x64 for camera capture and hand tracking. Avalonia keeps the UI portable, but the Windows camera and control adapters are Windows-only.
- A webcam and camera access enabled in Windows Privacy settings.
- Internet access for the first NuGet restore. The ONNX models are included locally; the app does not download models when tracking starts.

## Build and run

From this directory:

```powershell
dotnet restore HandsFree.sln
dotnet build HandsFree.sln --no-restore
dotnet run --project tests/HandsFree.Core.Tests
dotnet run --project src/HandsFree.App
```

In the app, choose **Start Tracking** to open the camera and run local CPU inference. Choose **Stop Tracking** to release both. Camera errors and missing local models appear in the tracking status. The camera is not opened at startup.

## Structure

- `src/HandsFree.App` — Avalonia shell, explicit tracking controls, camera preview, and 21-landmark overlay.
- `src/HandsFree.Core` — platform-independent interaction and hand-tracking contracts, result validation, and session lifecycle.
- `src/HandsFree.Windows` — Windows Media Foundation webcam capture, latest-frame handoff, CPU ONNX inference, and existing desktop adapters.
- `tests/HandsFree.Core.Tests` — checks for control routing, 21-landmark validation, hand identity association/recovery, model tensor metadata, and tracking lifecycle. Tracking lifecycle tests use fakes; no mouse or keyboard input is injected.

## Local hand tracking and model licensing

The Windows provider uses the OpenCV Zoo MediaPipe palm detector and hand-pose estimator as a two-stage local pipeline. It keeps up to four post-NMS palm candidates for single-hand association, then uses position, bounding-box overlap, handedness similarity, and confidence as a tie-breaker to retain the selected hand. A temporarily missing hand can recover for one second by default; after that the identity expires and the selector searches fresh for the preferred hand. **Reset Hand Lock** remains available to clear the identity sooner. The recovery window can be configured through `WindowsHandTrackingProvider`.

Hand selection defaults to **Right hand only**. The [OpenCV Zoo hand-pose reference](https://github.com/opencv/opencv_zoo/blob/main/models/handpose_estimation_mediapipe/mp_handpose.py) defines its handedness scalar as `0 = left`, `1 = right`, and returns the model output directly. Although [MediaPipe's general handedness documentation](https://github.com/google-ai-edge/mediapipe/blob/master/docs/solutions/hands.md#multi_handedness) describes a mirrored-selfie convention, manually checking the bundled model with the actual webcam showed that swapping this model output made a physical left hand appear right-handed. HandsFree therefore preserves the bundled model score; the preview's horizontal mirror is display-only and does not alter inference input. Values below 80% handedness confidence are treated as uncertain and rejected. This mapping should be checked again on other camera/model combinations. There is no existing application settings store, so the preference currently uses the code default and is not persisted.

Both ONNX files are shipped under `src/HandsFree.Windows/Models`; combined model size is about 7.6 MiB. Their upstream model directories provide Apache-2.0 licensing; the corresponding license texts and source links are included beside the models and in [ThirdPartyNotices.md](ThirdPartyNotices.md). The app performs no runtime downloads.

`Microsoft.ML.OnnxRuntime` supplies CPU inference. `OpenCvSharp4` and `OpenCvSharp4.runtime.win` supply image preprocessing and Windows webcam capture through the Media Foundation backend. See the third-party notices for pinned versions and license links. Model loading and camera access happen only after the user chooses Start Tracking. Capture uses a one-frame drop-oldest handoff, so inference works on the newest available frame instead of accumulating stale frames.

## Development principles

- Run locally without cloud services; hand tracking runs from bundled model files after package restore.
- Keep optional AI on-demand, with no model download or loading at startup.
- Keep resource use low; do not add background services without a concrete feature need.
- Put platform-independent validation and routing in Core and platform APIs in the Windows adapter.
- Keep computer-control actions explicit and single-shot; no automatic input loop or startup behavior.
- Add dependencies only when a feature requires them.
- Keep camera capture and inference in the Windows adapter, and keep tracking results independent of UI and desktop-control APIs.

## Targeted keyboard scroll test mode

The mode starts disabled. Enable it, choose **Capture Target Window**, move the pointer over the target application's scrollable content, then press the capture shortcut. Defaults are `Ctrl+Alt+F9` to capture, `Ctrl+Alt+F10` to scroll down, and `Ctrl+Alt+F11` to scroll up. The captured target's process name, title, and PID are shown. Use **Recapture Target Window** to select another child window or **Clear Target** to remove the saved target. Windows reserves F12, and all three shortcuts must be distinct.

The Windows adapter records the child HWND under the pointer and its top-level owner. A scroll action posts one `WM_MOUSEWHEEL` message to that captured child; the cursor and focus are not changed. The UI reports that the message was posted, not that scrolling was visible. Applications may handle posted wheel messages differently from physical input; this behavior needs manual verification in each target app. If the target closes or its handle becomes invalid, the adapter clears it and requires recapture.

The Windows adapter uses `RegisterHotKey`/`UnregisterHotKey` on a dedicated message thread and `MOD_NOREPEAT`. It does not install a keyboard hook or poll in the background. Closing the app unregisters the capture and scroll shortcuts.
