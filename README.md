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

At startup, the ONNX pipeline validates required output names, float32 types, ranks, and dimensions against the bundled files. The verified palm outputs are `Identity` (`[1,2016,18]` box/landmark regressors) and `Identity_1` (`[1,2016,1]` score logits). The hand-pose outputs are `Identity` (`[1,63]` screen landmarks), `Identity_1` (`[1,1]` presence), `Identity_2` (`[1,1]` handedness), and `Identity_3` (`[1,63]` world landmarks); each flattened landmark output contains 21 repeated x/y/z triples. These meanings follow the bundled model output order and OpenCV Zoo reference. Inference values are checked for expected lengths and finite values, and classification outputs must be in `[0,1]`. A model contract mismatch or malformed output faults tracking with an explicit error instead of silently ignoring or substituting data.

Hand selection defaults to **Right hand only**. The [OpenCV Zoo hand-pose reference](https://github.com/opencv/opencv_zoo/blob/main/models/handpose_estimation_mediapipe/mp_handpose.py) defines its handedness scalar as `0 = left`, `1 = right`, and returns the model output directly. Although [MediaPipe's general handedness documentation](https://github.com/google-ai-edge/mediapipe/blob/master/docs/solutions/hands.md#multi_handedness) describes a mirrored-selfie convention, manually checking the bundled model with the actual webcam showed that swapping this model output made a physical left hand appear right-handed. HandsFree therefore preserves the bundled model score; the preview's horizontal mirror is display-only and does not alter inference input. Values below 80% handedness confidence are treated as uncertain and rejected. This mapping should be checked again on other camera/model combinations. There is no existing application settings store, so the preference currently uses the code default and is not persisted.

Both ONNX files are shipped under `src/HandsFree.Windows/Models`; combined model size is about 7.6 MiB. Their upstream model directories provide Apache-2.0 licensing; the corresponding license texts and source links are included beside the models and in [ThirdPartyNotices.md](ThirdPartyNotices.md). The app performs no runtime downloads.

`Microsoft.ML.OnnxRuntime` supplies CPU inference. `OpenCvSharp4` and `OpenCvSharp4.runtime.win` supply image preprocessing and Windows webcam capture through the Media Foundation backend. See the third-party notices for pinned versions and license links. Model loading and camera access happen only after the user chooses Start Tracking. Capture uses a one-frame drop-oldest handoff, so inference works on the newest available frame instead of accumulating stale frames.

## Resource monitor

The compact resource panel refreshes once per second and does not start the camera or model sessions. Process CPU is calculated from HandsFree's process CPU-time delta divided by elapsed wall time; **100% means one fully used logical CPU core**, so a multi-threaded process can exceed 100%. Working set is resident process memory and includes native allocations; managed heap is the .NET GC heap only. ONNX latency sums the time spent inside the actual ONNX Runtime `Run` calls for each completed frame and shows a rolling average and p95 over at most 120 samples. Tracking FPS uses timestamps of successfully processed frames. Dropped frames are estimated from sequence gaps between captured frames that reach inference; camera-driver failures before a frame is captured are not counted. Tracking-only counters reset at session start and stop and show idle while tracking is stopped.

## Hand-tracking diagnostics

During a webcam test, open **Resource monitor** and read the **Tracking diagnostics** section. Counts are session totals and are updated with the existing one-second monitor refresh. “Score-qualified palms before NMS” counts anchors above the unchanged palm score threshold; the next values show survivors after NMS and after the existing top-four cap. Crop skips and hand-pose model calls/rejections identify later pipeline stages. A frame with no valid pose candidate is distinct from a candidate rejected by handedness or spatial association. The current state/message and selected confidence values (or the most recently rejected handedness score) are included when available. Choose **Reset diagnostics** immediately before a test to start clean totals; this does not reset hand identity, camera, or model state. Counters are bounded scalar totals only: HandsFree keeps no per-frame logs, images, or landmark history for diagnostics. They indicate where candidates are discarded but do not prove that a displayed frame failure has one specific cause without observing runtime measurements.

## Local landmark sample collection

While hand tracking is running, enter a gesture label and choose **Start Collection**. The collector writes one JSON object per line (JSONL) under `%LOCALAPPDATA%\HandsFree\Samples` (normally `C:\Users\<you>\AppData\Local\HandsFree\Samples`). Choose **Stop Collection** to flush and close the file. Each recording creates a separate file and session ID. The collector accepts samples only when the already-selected hand is present with exactly 21 valid landmarks; during tracking loss it pauses sample writes and resumes when a selected hand returns. Empty labels are rejected, and each sample stores its label so label changes during a recording remain explicit.

Schema version 1 records contain `schemaVersion`, `sessionId`, incrementing `sampleId`, frame capture time in UTC, label, model confidence, handedness score and confidence, and 21 `{x,y,z}` points. x/y are normalized image coordinates and z is the model's relative depth value. A bounded 128-sample in-memory channel feeds an asynchronous local file writer; if that queue fills, the affected sample is skipped and the status reports the drop. Data stays on this machine and is not uploaded or used to train anything by HandsFree.

Simple workflow: start tracking, wait until the intended hand is selected, enter a short label (for example `open_palm`), start collection, perform that gesture, then stop collection before changing labels or starting another session. Review the saved JSONL files before using them in a future training pipeline. Collection is disabled by default and stops when tracking stops or the app closes.

## Index-finger pointer prototype

While tracking is running, explicitly enable **Pointer simulation** to evaluate a geometric index-point pose: index extended, middle/ring/little fingers curled, thumb near the palm, and hand confidence at least 80%. The selected right-hand result from the existing tracking pipeline is reused; fingertip landmark 8 is the control point. The yellow rectangle over the camera preview is adjustable with its left/top/width/height sliders. Its normalized coordinates refer to the displayed image, excluding letterbox space, and match the preview's horizontal mirroring. The simulated desktop is rendered at 16:9 and maps to a 1280×720 coordinate surface independent of preview resizing.

Movement is smoothed. When the fingertip exits the rectangle or the pose/tracking is lost, the simulated marker pauses; it is not clamped to the rectangle. On re-entry or pose recovery, movement reanchors at the marker's last position to avoid a jump, then resumes from fingertip deltas. The toggle is off by default and resets when tracking stops. This is a visual prototype only: it never moves the Windows cursor or sends clicks, scrolls, keystrokes, or other OS input. The geometric pose heuristic has not been validated across users or webcams and may need calibration; no gesture classification is performed.

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

## Gesture Studio

Open **Gesture Studio** from the HandsFree header. It reuses the one running `HandTrackingSession`; it does not create another camera or ONNX session. Tracking must be started from the main window before recording. Recording is opt-in and starts only after **Start Recording** with a non-empty gesture label. Closing Gesture Studio finalizes the current sample and leaves the main window and tracking session running. Main application shutdown finalizes the Studio writer before releasing tracking.

Samples are written locally under `%LOCALAPPDATA%\HandsFree\Samples\{sessionId}\{sampleId}`. Each sample contains `original.mp4`, `landmarks.jsonl`, and a version 1 `manifest.json`. The manifest stores the original label/notes, review label/notes, disposition, nominal video frame rate, and optional trimmed export filenames. Marking a sample discarded updates its manifest and preserves all original files. Trim export writes `trimmed.mp4` and `trimmed.jsonl` without overwriting originals.

The JSONL uses one row per video frame and includes schema/session/sample IDs, sequential `videoFrameIndex`, the source `capturedAtUtc`, elapsed capture time, tracking state, selected-hand confidence/handedness when present, and either 21 x/y/z landmarks or explicit `landmarks: null` plus a missing reason. The MP4 is constant-rate at 30 frames per second; `videoFrameIndex` is the synchronization key, while `capturedAtUtc` records the actual processed-frame time. The existing camera pipeline has a one-frame drop-oldest handoff before inference, so the recording contains each frame delivered by that shared inference stream, including frames where the selected hand is lost, rather than every raw sensor frame. A bounded eight-frame recorder queue drops and counts frames if disk/encoding cannot keep up; it never blocks inference. OpenCV's already-bundled FFmpeg video I/O plugin is retained for local MP4 encoding and review.

During recording, the Studio preview follows the same live tracking frames. Sample review supports play/pause, frame stepping, seeking, and an optional landmark overlay. Enter trim start/end in seconds and choose **Preview interval** or **Export trimmed interval**; the selected video frame range and JSONL rows are remapped together to a zero-based output frame index. Review reads JSONL sequentially with a single bounded decode operation at a time, including when the user seeks. No data is uploaded, no model is trained, and no computer-control action is triggered by Gesture Studio.
