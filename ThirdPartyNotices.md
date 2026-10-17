# Third-party notices

HandDesk bundles or references the following third-party software and model files. The two model license texts are also included next to the model files.

## OpenCV Zoo MediaPipe hand models

Both models are distributed by the OpenCV Zoo repository under Apache License 2.0, as stated in the model directories. Apache 2.0 allows commercial use and redistribution subject to its conditions, including preservation of the license and applicable notices.

- Palm detector: [`palm_detection_mediapipe_2023feb.onnx`](https://raw.githubusercontent.com/opencv/opencv_zoo/main/models/palm_detection_mediapipe/palm_detection_mediapipe_2023feb.onnx), 3,905,734 bytes. SHA-256: `78FF51C38496B7FC8B8EBDB6CC8C1ABB02FA6C38427C6848254CDABA57FCCE7C`.
- Hand pose estimator: [`handpose_estimation_mediapipe_2023feb.onnx`](https://raw.githubusercontent.com/opencv/opencv_zoo/main/models/handpose_estimation_mediapipe/handpose_estimation_mediapipe_2023feb.onnx), 4,099,621 bytes. SHA-256: `DB0898AE717B76B075D9BF563AF315B29562E11F8DF5027A1EF07B02BEF6D81C`.
- Model documentation and licenses: [palm detection](https://github.com/opencv/opencv_zoo/tree/main/models/palm_detection_mediapipe) and [hand pose estimation](https://github.com/opencv/opencv_zoo/tree/main/models/handpose_estimation_mediapipe).
- The C# preprocessing and postprocessing port follows the corresponding OpenCV Zoo references: [`mp_palmdet.py`](https://github.com/opencv/opencv_zoo/blob/main/models/palm_detection_mediapipe/mp_palmdet.py) and [`mp_handpose.py`](https://github.com/opencv/opencv_zoo/blob/main/models/handpose_estimation_mediapipe/mp_handpose.py).

The full Apache 2.0 text is in `src/HandsFree.Windows/Models/PalmDetection/LICENSE` and `src/HandsFree.Windows/Models/HandPose/LICENSE`.

## Runtime and image capture packages

- `Microsoft.ML.OnnxRuntime` 1.31.0 — CPU inference runtime, MIT license: [repository and license](https://github.com/microsoft/onnxruntime).
- `OpenCvSharp4` 4.13.0.20260627 — managed OpenCV bindings, Apache-2.0: [repository and license](https://github.com/shimat/opencvsharp).
- `OpenCvSharp4.runtime.win` 4.13.0.20260627 — Windows native OpenCV runtime components, Apache-2.0: [package](https://www.nuget.org/packages/OpenCvSharp4.runtime.win/).

These NuGet packages are restored by the .NET SDK and are not downloaded at camera start. Consult the linked upstream package and repository notices when redistributing builds.
