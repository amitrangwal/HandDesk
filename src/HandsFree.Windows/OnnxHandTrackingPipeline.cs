using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using Core = HandsFree.Core;

namespace HandsFree.Windows;

/// <summary>Local CPU pipeline ported from OpenCV Zoo's MediaPipe palm detector and hand pose reference.</summary>
public sealed class OnnxHandTrackingPipeline : IDisposable
{
    private const int PalmInputSize = 192;
    private const int PalmAnchorCount = 2016;
    private const int PalmValuesPerAnchor = 18;
    private const int MaximumPalmCandidates = 4;
    private const int HandInputSize = 224;
    private const float PalmScoreThreshold = 0.5f;
    private const float PalmNmsThreshold = 0.3f;
    private const float HandPresenceThreshold = 0.8f;
    private readonly InferenceSession _palmSession;
    private readonly InferenceSession _handSession;
    private readonly string _palmInputName;
    private readonly string _palmBoxOutputName;
    private readonly string _palmScoreOutputName;
    private readonly string _handInputName;
    private readonly string _handLandmarkOutputName;
    private readonly string _handConfidenceOutputName;
    private readonly string _handednessOutputName;
    private readonly Vector2[] _anchors;

    public OnnxHandTrackingPipeline(string palmModelPath, string handModelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(palmModelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(handModelPath);
        if (!File.Exists(palmModelPath)) throw new FileNotFoundException("The bundled palm detector model was not found.", palmModelPath);
        if (!File.Exists(handModelPath)) throw new FileNotFoundException("The bundled hand landmark model was not found.", handModelPath);

        var options = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            InterOpNumThreads = 1
        };
        InferenceSession? palmSession = null;
        InferenceSession? handSession = null;
        try
        {
            palmSession = new InferenceSession(palmModelPath, options);
            handSession = new InferenceSession(handModelPath, options);
            _palmSession = palmSession;
            _handSession = handSession;

            _palmInputName = ValidateSingleInput(_palmSession, [1, PalmInputSize, PalmInputSize, 3], "palm detector");
            _palmBoxOutputName = FindOutput(_palmSession, [1, PalmAnchorCount, PalmValuesPerAnchor], "palm boxes and landmarks");
            _palmScoreOutputName = FindOutput(_palmSession, [1, PalmAnchorCount, 1], "palm confidence logits");
            _handInputName = ValidateSingleInput(_handSession, [1, HandInputSize, HandInputSize, 3], "hand landmark model");
            var handLandmarkOutputs = _handSession.OutputMetadata.Where(item => ShapeMatches(item.Value.Dimensions, [1, 63])).ToArray();
            if (handLandmarkOutputs.Length != 2)
                throw new InvalidDataException($"Expected screen and world landmark tensors from the hand model. Actual outputs: {FormatOutputs(_handSession)}");
            // OpenCV Zoo's reference output order is screen landmarks, confidence, handedness, world landmarks.
            _handLandmarkOutputName = handLandmarkOutputs[0].Key;
            var scalarOutputs = _handSession.OutputMetadata.Where(item => item.Value.Dimensions.Aggregate(1L, (size, dimension) => size * dimension) == 1).ToArray();
            if (scalarOutputs.Length != 2) throw new InvalidDataException($"Expected confidence and handedness tensors. Actual outputs: {FormatOutputs(_handSession)}");
            _handConfidenceOutputName = scalarOutputs[0].Key;
            _handednessOutputName = scalarOutputs[1].Key;
            _anchors = CreateAnchors();
        }
        catch
        {
            handSession?.Dispose();
            palmSession?.Dispose();
            throw;
        }
        finally { options.Dispose(); }
    }

    public Core.HandTrackingResult Infer(byte[] bgrPixels, int width, int height)
    {
        var candidates = InferCandidates(bgrPixels, width, height);
        return candidates.Count == 0 ? Core.HandTrackingResult.NoHand : candidates[0];
    }

    /// <summary>Returns a bounded set of valid hand poses for Core identity association.</summary>
    public IReadOnlyList<Core.HandTrackingResult> InferCandidates(byte[] bgrPixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgrPixels);
        if (width <= 0 || height <= 0 || bgrPixels.Length != (long)width * height * 3)
            throw new ArgumentException("The camera frame must be tightly packed BGR24 pixels.", nameof(bgrPixels));

        using var image = new Mat(height, width, MatType.CV_8UC3);
        Marshal.Copy(bgrPixels, 0, image.Data, bgrPixels.Length);
        var palms = DetectPalms(image);
        if (palms.Count == 0) return Array.Empty<Core.HandTrackingResult>();
        var results = new List<Core.HandTrackingResult>(palms.Count);
        foreach (var palm in palms)
        {
            try
            {
                var result = DetectHand(image, palm);
                if (result.IsDetected) results.Add(result);
            }
            catch (InvalidDataException)
            {
                // A geometrically invalid detector box is an unusable candidate, not a camera failure.
            }
        }
        return results;
    }

    public static IReadOnlyDictionary<string, int[]> InspectModelShapes(string palmModelPath, string handModelPath)
    {
        using var palm = new InferenceSession(palmModelPath);
        using var hand = new InferenceSession(handModelPath);
        var shapes = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var item in palm.InputMetadata) shapes[$"palm.input.{item.Key}"] = item.Value.Dimensions;
        foreach (var item in palm.OutputMetadata) shapes[$"palm.output.{item.Key}"] = item.Value.Dimensions;
        foreach (var item in hand.InputMetadata) shapes[$"hand.input.{item.Key}"] = item.Value.Dimensions;
        foreach (var item in hand.OutputMetadata) shapes[$"hand.output.{item.Key}"] = item.Value.Dimensions;
        return shapes;
    }

    public void Dispose()
    {
        _palmSession.Dispose();
        _handSession.Dispose();
    }

    private IReadOnlyList<PalmDetection> DetectPalms(Mat bgrImage)
    {
        var ratio = Math.Min((double)PalmInputSize / bgrImage.Rows, (double)PalmInputSize / bgrImage.Cols);
        var resizedWidth = (int)(bgrImage.Cols * ratio);
        var resizedHeight = (int)(bgrImage.Rows * ratio);
        var left = (PalmInputSize - resizedWidth) / 2;
        var top = (PalmInputSize - resizedHeight) / 2;
        using var resized = new Mat();
        Cv2.Resize(bgrImage, resized, new Size(resizedWidth, resizedHeight));
        using var letterboxed = new Mat(PalmInputSize, PalmInputSize, MatType.CV_8UC3, Scalar.Black);
        using (var region = new Mat(letterboxed, new Rect(left, top, resizedWidth, resizedHeight))) resized.CopyTo(region);
        using var rgb = new Mat();
        Cv2.CvtColor(letterboxed, rgb, ColorConversionCodes.BGR2RGB);
        var input = NormalizeImage(rgb);
        var tensor = new DenseTensor<float>(input, [1, PalmInputSize, PalmInputSize, 3]);
        using var output = _palmSession.Run([NamedOnnxValue.CreateFromTensor(_palmInputName, tensor)]);
        var regressors = GetOutput(output, _palmBoxOutputName);
        var logits = GetOutput(output, _palmScoreOutputName);

        var scale = Math.Max(bgrImage.Cols, bgrImage.Rows);
        var padBiasX = (int)(left / ratio);
        var padBiasY = (int)(top / ratio);
        var candidates = new List<PalmDetection>();
        for (var index = 0; index < PalmAnchorCount; index++)
        {
            var score = Sigmoid(logits[index]);
            if (score < PalmScoreThreshold) continue;
            var offset = index * PalmValuesPerAnchor;
            var anchor = _anchors[index];
            var centerX = (regressors[offset] / PalmInputSize + anchor.X) * scale - padBiasX;
            var centerY = (regressors[offset + 1] / PalmInputSize + anchor.Y) * scale - padBiasY;
            var boxWidth = regressors[offset + 2] / PalmInputSize * scale;
            var boxHeight = regressors[offset + 3] / PalmInputSize * scale;
            var box = new RectF(centerX - boxWidth / 2, centerY - boxHeight / 2, boxWidth, boxHeight);
            var landmarks = new Vector2[7];
            for (var landmarkIndex = 0; landmarkIndex < 7; landmarkIndex++)
            {
                var landmarkOffset = offset + 4 + landmarkIndex * 2;
                landmarks[landmarkIndex] = new Vector2(
                    (regressors[landmarkOffset] / PalmInputSize + anchor.X) * scale - padBiasX,
                    (regressors[landmarkOffset + 1] / PalmInputSize + anchor.Y) * scale - padBiasY);
            }
            candidates.Add(new PalmDetection(box, landmarks, score));
        }

        candidates.Sort(static (leftCandidate, rightCandidate) => rightCandidate.Score.CompareTo(leftCandidate.Score));
        // Preserve the reference NMS selection order, while returning its first result because
        // this milestone intentionally tracks one hand only.
        var kept = new List<PalmDetection>();
        foreach (var candidate in candidates)
        {
            if (kept.All(previous => IntersectionOverUnion(candidate.Box, previous.Box) <= PalmNmsThreshold))
                kept.Add(candidate);
        }
        return kept.Take(MaximumPalmCandidates).ToArray();
    }

    private Core.HandTrackingResult DetectHand(Mat bgrImage, PalmDetection palm)
    {
        var initialBox = palm.Box;
        using var palmCrop = CropAndPad(bgrImage, initialBox, forRotation: true, out var croppedPalmBox, out var padBiasX, out var padBiasY);
        var palmLandmarks = palm.Landmarks.Select(point => new Vector2(point.X - padBiasX, point.Y - padBiasY)).ToArray();
        var palmBoxInCrop = new RectF(croppedPalmBox.X - padBiasX, croppedPalmBox.Y - padBiasY, croppedPalmBox.Width, croppedPalmBox.Height);

        var firstPalmPoint = palmLandmarks[0];
        var middleMcpPoint = palmLandmarks[2];
        var radians = Math.PI / 2 - Math.Atan2(-(middleMcpPoint.Y - firstPalmPoint.Y), middleMcpPoint.X - firstPalmPoint.X);
        radians -= 2 * Math.PI * Math.Floor((radians + Math.PI) / (2 * Math.PI));
        var angle = radians * 180 / Math.PI;
        var palmCenter = new Point2f(palmBoxInCrop.X + palmBoxInCrop.Width / 2, palmBoxInCrop.Y + palmBoxInCrop.Height / 2);
        using var rotation = Cv2.GetRotationMatrix2D(palmCenter, angle, 1.0);
        using var rotated = new Mat();
        Cv2.WarpAffine(palmCrop, rotated, rotation, new Size(palmCrop.Cols, palmCrop.Rows));

        var rotatedPalmPoints = palmLandmarks.Select(point => TransformPoint(rotation, point)).ToArray();
        var rotatedPalmBox = Bounds(rotatedPalmPoints);
        using var handCrop = CropAndPad(rotated, rotatedPalmBox, forRotation: false, out var paddedHandBox, out _, out _);
        using var resized = new Mat();
        Cv2.Resize(handCrop, resized, new Size(HandInputSize, HandInputSize), 0, 0, InterpolationFlags.Area);
        using var rgb = new Mat();
        Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);
        var input = NormalizeImage(rgb);
        var tensor = new DenseTensor<float>(input, [1, HandInputSize, HandInputSize, 3]);
        using var output = _handSession.Run([NamedOnnxValue.CreateFromTensor(_handInputName, tensor)]);
        var presence = GetOutput(output, _handConfidenceOutputName)[0];
        if (presence < HandPresenceThreshold) return Core.HandTrackingResult.NoHand;

        var screen = GetOutput(output, _handLandmarkOutputName);
        var modelHandedness = Math.Clamp(GetOutput(output, _handednessOutputName)[0], 0, 1);
        // OpenCV Zoo's reference returns this bundled model output directly as 0=left, 1=right.
        // Keep inference on the unmirrored camera frame; the preview mirror is display-only.
        var handedness = modelHandedness;
        var boxScaleX = paddedHandBox.Width / HandInputSize;
        var boxScaleY = paddedHandBox.Height / HandInputSize;
        var scale = Math.Max(boxScaleX, boxScaleY);
        var localPoints = new Vector3[21];
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        for (var index = 0; index < 21; index++)
        {
            var x = (screen[index * 3] - HandInputSize / 2f) * scale;
            var y = (screen[index * 3 + 1] - HandInputSize / 2f) * scale;
            var z = screen[index * 3 + 2] * scale;
            localPoints[index] = new Vector3((float)(x * cosine - y * sine), (float)(x * sine + y * cosine), z);
        }

        var centerX = (paddedHandBox.X + paddedHandBox.Width / 2f);
        var centerY = (paddedHandBox.Y + paddedHandBox.Height / 2f);
        var rotationM00 = rotation.At<double>(0, 0);
        var rotationM01 = rotation.At<double>(0, 1);
        var rotationM02 = rotation.At<double>(0, 2);
        var rotationM10 = rotation.At<double>(1, 0);
        var rotationM11 = rotation.At<double>(1, 1);
        var rotationM12 = rotation.At<double>(1, 2);
        var originalCenterX = rotationM00 * centerX + rotationM10 * centerY
            - (rotationM00 * rotationM02 + rotationM10 * rotationM12);
        var originalCenterY = rotationM01 * centerX + rotationM11 * centerY
            - (rotationM01 * rotationM02 + rotationM11 * rotationM12);

        var landmarks = new Core.HandLandmark[21];
        for (var index = 0; index < 21; index++)
        {
            var point = localPoints[index];
            var x = Math.Clamp((float)(point.X + originalCenterX + padBiasX) / bgrImage.Cols, 0, 1);
            var y = Math.Clamp((float)(point.Y + originalCenterY + padBiasY) / bgrImage.Rows, 0, 1);
            var z = point.Z / Math.Max(bgrImage.Cols, bgrImage.Rows);
            landmarks[index] = new Core.HandLandmark(x, y, z);
        }

        return Core.HandTrackingResult.TryCreate(landmarks, Math.Clamp(presence, 0, 1), handedness, out var result)
            ? result!
            : Core.HandTrackingResult.NoHand;
    }

    private static Mat CropAndPad(Mat image, RectF box, bool forRotation, out RectF resultBox, out int biasX, out int biasY)
    {
        var shiftX = forRotation ? 0 : 0;
        var shiftY = forRotation ? 0 : -0.4f;
        var x1 = (int)(box.X + box.Width * shiftX);
        var y1 = (int)(box.Y + box.Height * shiftY);
        var x2 = (int)(box.Right + box.Width * shiftX);
        var y2 = (int)(box.Bottom + box.Height * shiftY);
        var centerX = (x1 + x2) / 2f;
        var centerY = (y1 + y2) / 2f;
        var scale = forRotation ? 4f : 3f;
        var halfWidth = (x2 - x1) * scale / 2;
        var halfHeight = (y2 - y1) * scale / 2;
        x1 = Math.Clamp((int)(centerX - halfWidth), 0, image.Cols);
        y1 = Math.Clamp((int)(centerY - halfHeight), 0, image.Rows);
        x2 = Math.Clamp((int)(centerX + halfWidth), 0, image.Cols);
        y2 = Math.Clamp((int)(centerY + halfHeight), 0, image.Rows);
        if (x2 <= x1 || y2 <= y1) throw new InvalidDataException("The palm detector returned an invalid crop.");

        var width = x2 - x1;
        var height = y2 - y1;
        var side = forRotation ? (int)Math.Sqrt((double)width * width + (double)height * height) : Math.Max(width, height);
        var padWidth = side - width;
        var padHeight = side - height;
        var left = padWidth / 2;
        var top = padHeight / 2;
        var output = new Mat(side, side, MatType.CV_8UC3, Scalar.Black);
        using (var source = new Mat(image, new Rect(x1, y1, width, height)))
        using (var destination = new Mat(output, new Rect(left, top, width, height))) source.CopyTo(destination);

        biasX = x1 - left;
        biasY = y1 - top;
        resultBox = new RectF(x1, y1, width, height);
        return output;
    }

    private static Vector2 TransformPoint(Mat affine, Vector2 point) => new(
        (float)(affine.At<double>(0, 0) * point.X + affine.At<double>(0, 1) * point.Y + affine.At<double>(0, 2)),
        (float)(affine.At<double>(1, 0) * point.X + affine.At<double>(1, 1) * point.Y + affine.At<double>(1, 2)));

    private static RectF Bounds(IReadOnlyList<Vector2> points)
    {
        var minX = points.Min(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxX = points.Max(point => point.X);
        var maxY = points.Max(point => point.Y);
        return new RectF(minX, minY, maxX - minX, maxY - minY);
    }

    private static float[] NormalizeImage(Mat rgb)
    {
        var bytes = new byte[rgb.Rows * rgb.Cols * 3];
        Marshal.Copy(rgb.Data, bytes, 0, bytes.Length);
        var values = new float[bytes.Length];
        for (var index = 0; index < bytes.Length; index++) values[index] = bytes[index] / 255f;
        return values;
    }

    private static float[] GetOutput(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> values, string name) =>
        values.First(value => string.Equals(value.Name, name, StringComparison.Ordinal)).AsTensor<float>().ToArray();

    private static string ValidateSingleInput(InferenceSession session, int[] expectedShape, string modelName)
    {
        if (session.InputMetadata.Count != 1) throw new InvalidDataException($"The {modelName} must have exactly one input tensor.");
        var input = session.InputMetadata.Single();
        if (!ShapeMatches(input.Value.Dimensions, expectedShape))
            throw new InvalidDataException($"Unexpected {modelName} input shape {FormatShape(input.Value.Dimensions)}; expected {FormatShape(expectedShape)}.");
        if (input.Value.ElementType != typeof(float))
            throw new InvalidDataException($"The {modelName} input tensor must contain float32 values.");
        return input.Key;
    }

    private static string FindOutput(InferenceSession session, int[] expectedShape, string description)
    {
        var matches = session.OutputMetadata.Where(item => ShapeMatches(item.Value.Dimensions, expectedShape)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException($"Could not identify the {description} output tensor. Actual outputs: {FormatOutputs(session)}");
        return matches[0].Key;
    }

    private static bool ShapeMatches(IReadOnlyList<int> actual, IReadOnlyList<int> expected) =>
        actual.Count == expected.Count && actual.Zip(expected).All(pair => pair.First < 0 || pair.First == pair.Second);

    private static string FormatShape(IEnumerable<int> dimensions) => $"[{string.Join(",", dimensions)}]";

    private static string FormatOutputs(InferenceSession session) => string.Join(", ", session.OutputMetadata.Select(item => $"{item.Key}:{FormatShape(item.Value.Dimensions)}"));

    private static float Sigmoid(float value) => value >= 0 ? 1f / (1f + MathF.Exp(-value)) : MathF.Exp(value) / (1f + MathF.Exp(value));

    private static float IntersectionOverUnion(RectF left, RectF right)
    {
        var intersectionWidth = Math.Max(0, Math.Min(left.Right, right.Right) - Math.Max(left.X, right.X));
        var intersectionHeight = Math.Max(0, Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Y, right.Y));
        var intersection = intersectionWidth * intersectionHeight;
        var union = left.Width * left.Height + right.Width * right.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static Vector2[] CreateAnchors()
    {
        var anchors = new Vector2[PalmAnchorCount];
        var index = 0;
        AddGrid(24, 2);
        AddGrid(12, 6);
        if (index != anchors.Length) throw new InvalidDataException("The palm detector anchor configuration produced an unexpected count.");
        return anchors;

        void AddGrid(int gridSize, int anchorsPerCell)
        {
            for (var y = 0; y < gridSize; y++)
                for (var x = 0; x < gridSize; x++)
                    for (var anchor = 0; anchor < anchorsPerCell; anchor++)
                        anchors[index++] = new Vector2((x + 0.5f) / gridSize, (y + 0.5f) / gridSize);
        }
    }

    private readonly record struct PalmDetection(RectF Box, Vector2[] Landmarks, float Score);

    private readonly record struct Vector2(float X, float Y);

    private readonly record struct Vector3(float X, float Y, float Z);

    private readonly record struct RectF(float X, float Y, float Width, float Height)
    {
        public float Right => X + Width;
        public float Bottom => Y + Height;
    }
}
