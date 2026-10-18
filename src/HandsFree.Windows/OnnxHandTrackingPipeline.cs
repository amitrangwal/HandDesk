using System.Runtime.InteropServices;
using System.Diagnostics;
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
    private const string PalmRegressorsOutput = "Identity";
    private const string PalmScoresOutput = "Identity_1";
    private const string HandScreenLandmarksOutput = "Identity";
    private const string HandPresenceOutput = "Identity_1";
    private const string HandednessOutput = "Identity_2";
    private const string HandWorldLandmarksOutput = "Identity_3";
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
            ValidateOutputMetadata("Palm detector", ReadOutputMetadata(_palmSession), new Dictionary<string, int[]>(StringComparer.Ordinal)
            {
                [PalmRegressorsOutput] = [1, PalmAnchorCount, PalmValuesPerAnchor],
                [PalmScoresOutput] = [1, PalmAnchorCount, 1]
            });
            _palmBoxOutputName = PalmRegressorsOutput;
            _palmScoreOutputName = PalmScoresOutput;
            _handInputName = ValidateSingleInput(_handSession, [1, HandInputSize, HandInputSize, 3], "hand landmark model");
            ValidateOutputMetadata("Hand pose model", ReadOutputMetadata(_handSession), new Dictionary<string, int[]>(StringComparer.Ordinal)
            {
                [HandScreenLandmarksOutput] = [1, 63],
                [HandPresenceOutput] = [1, 1],
                [HandednessOutput] = [1, 1],
                [HandWorldLandmarksOutput] = [1, 63]
            });
            // Verified OpenCV Zoo output order: screen landmarks, presence, handedness, world landmarks.
            // Names, rather than collection ordering, bind each value below.
            _handLandmarkOutputName = HandScreenLandmarksOutput;
            _handConfidenceOutputName = HandPresenceOutput;
            _handednessOutputName = HandednessOutput;
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
        => InferCandidates(bgrPixels, width, height, out _);

    /// <summary>Returns candidates and elapsed time spent inside the ONNX Runtime Run calls.</summary>
    public IReadOnlyList<Core.HandTrackingResult> InferCandidates(byte[] bgrPixels, int width, int height, out TimeSpan onnxInferenceTime)
    {
        onnxInferenceTime = TimeSpan.Zero;
        ArgumentNullException.ThrowIfNull(bgrPixels);
        if (width <= 0 || height <= 0 || bgrPixels.Length != (long)width * height * 3)
            throw new ArgumentException("The camera frame must be tightly packed BGR24 pixels.", nameof(bgrPixels));

        using var image = new Mat(height, width, MatType.CV_8UC3);
        Marshal.Copy(bgrPixels, 0, image.Data, bgrPixels.Length);
        long inferenceTicks = 0;
        var palms = DetectPalms(image, ref inferenceTicks);
        if (palms.Count == 0)
        {
            onnxInferenceTime = TimeSpan.FromSeconds((double)inferenceTicks / Stopwatch.Frequency);
            return Array.Empty<Core.HandTrackingResult>();
        }
        var results = new List<Core.HandTrackingResult>(palms.Count);
        foreach (var palm in palms)
        {
            try
            {
                var result = DetectHand(image, palm, ref inferenceTicks);
                if (result.IsDetected) results.Add(result);
            }
            catch (InvalidPalmCropException)
            {
                // A geometrically invalid detector box is an unusable candidate, not a camera failure.
            }
        }
        onnxInferenceTime = TimeSpan.FromSeconds((double)inferenceTicks / Stopwatch.Frequency);
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

    private IReadOnlyList<PalmDetection> DetectPalms(Mat bgrImage, ref long inferenceTicks)
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
        var inferenceStarted = Stopwatch.GetTimestamp();
        using var output = _palmSession.Run([NamedOnnxValue.CreateFromTensor(_palmInputName, tensor)]);
        inferenceTicks += Stopwatch.GetTimestamp() - inferenceStarted;
        ValidateOutputNames("Palm detector inference", output.Select(value => value.Name).ToArray(), [PalmRegressorsOutput, PalmScoresOutput]);
        var regressors = ReadFloatOutput(output, "Palm detector", _palmBoxOutputName, [1, PalmAnchorCount, PalmValuesPerAnchor]);
        var logits = ReadFloatOutput(output, "Palm detector", _palmScoreOutputName, [1, PalmAnchorCount, 1]);

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
            if (!float.IsFinite(centerX) || !float.IsFinite(centerY) || !float.IsFinite(boxWidth) || !float.IsFinite(boxHeight)
                || boxWidth <= 0 || boxHeight <= 0)
                throw new InvalidDataException($"Palm detector output '{_palmBoxOutputName}' contains invalid box coordinates or size for anchor {index}.");
            var box = new RectF(centerX - boxWidth / 2, centerY - boxHeight / 2, boxWidth, boxHeight);
            var landmarks = new Vector2[7];
            for (var landmarkIndex = 0; landmarkIndex < 7; landmarkIndex++)
            {
                var landmarkOffset = offset + 4 + landmarkIndex * 2;
                landmarks[landmarkIndex] = new Vector2(
                    (regressors[landmarkOffset] / PalmInputSize + anchor.X) * scale - padBiasX,
                    (regressors[landmarkOffset + 1] / PalmInputSize + anchor.Y) * scale - padBiasY);
                if (!float.IsFinite(landmarks[landmarkIndex].X) || !float.IsFinite(landmarks[landmarkIndex].Y))
                    throw new InvalidDataException($"Palm detector output '{_palmBoxOutputName}' contains invalid landmark coordinates for anchor {index}.");
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

    private Core.HandTrackingResult DetectHand(Mat bgrImage, PalmDetection palm, ref long inferenceTicks)
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
        var inferenceStarted = Stopwatch.GetTimestamp();
        using var output = _handSession.Run([NamedOnnxValue.CreateFromTensor(_handInputName, tensor)]);
        inferenceTicks += Stopwatch.GetTimestamp() - inferenceStarted;
        ValidateOutputNames("Hand pose inference", output.Select(value => value.Name).ToArray(),
            [HandScreenLandmarksOutput, HandPresenceOutput, HandednessOutput, HandWorldLandmarksOutput]);
        var screen = ReadFloatOutput(output, "Hand pose model", _handLandmarkOutputName, [1, 63]);
        var presenceValues = ReadFloatOutput(output, "Hand pose model", _handConfidenceOutputName, [1, 1]);
        var handednessValues = ReadFloatOutput(output, "Hand pose model", _handednessOutputName, [1, 1]);
        _ = ReadFloatOutput(output, "Hand pose model", HandWorldLandmarksOutput, [1, 63]);
        var presence = RequireProbability(presenceValues[0], "hand presence", _handConfidenceOutputName);
        var modelHandedness = RequireProbability(handednessValues[0], "handedness", _handednessOutputName);
        if (presence < HandPresenceThreshold) return Core.HandTrackingResult.NoHand;

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

        return Core.HandTrackingResult.TryCreate(landmarks, presence, handedness, out var result)
            ? result!
            : throw new InvalidDataException($"Hand pose output '{_handLandmarkOutputName}' produced landmarks outside the supported normalized coordinate range.");
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
        if (x2 <= x1 || y2 <= y1) throw new InvalidPalmCropException("The palm detector returned an invalid crop.");

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

    private static float[] ReadFloatOutput(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> values,
        string modelName,
        string outputName,
        int[] expectedDimensions)
    {
        var output = values.FirstOrDefault(value => string.Equals(value.Name, outputName, StringComparison.Ordinal));
        if (output is null)
            throw new InvalidDataException($"{modelName} inference did not return required output '{outputName}'. Check that the bundled ONNX file matches the supported model contract.");

        Tensor<float> tensor;
        try { tensor = output.AsTensor<float>(); }
        catch (Exception exception) when (exception is InvalidCastException or InvalidOperationException)
        {
            throw new InvalidDataException($"{modelName} output '{outputName}' must be a float32 tensor.", exception);
        }

        var dimensions = tensor.Dimensions.ToArray();
        var valuesArray = tensor.ToArray();
        ValidateOutputValues(modelName, new ModelTensorMetadata(outputName, typeof(float), dimensions), expectedDimensions, valuesArray);
        return valuesArray;
    }

    internal static void ValidateOutputMetadata(
        string modelName,
        IReadOnlyList<ModelTensorMetadata> actual,
        IReadOnlyDictionary<string, int[]> expected)
    {
        ValidateOutputNames(modelName, actual.Select(item => item.Name).ToArray(), expected.Keys.ToArray());
        if (actual.Count != expected.Count)
            throw new InvalidDataException($"{modelName} has {actual.Count} output tensors; expected {expected.Count}.");

        foreach (var output in actual)
        {
            var dimensions = expected[output.Name];
            if (output.ElementType != typeof(float) || !output.Dimensions.SequenceEqual(dimensions))
                throw new InvalidDataException($"{modelName} output '{output.Name}' has type {output.ElementType.Name}, rank {output.Dimensions.Count}, shape {FormatShape(output.Dimensions)}; expected float32, rank {dimensions.Length}, shape {FormatShape(dimensions)}.");
        }
    }

    internal static void ValidateOutputNames(string modelName, IReadOnlyList<string> actual, IReadOnlyList<string> expected)
    {
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var missing = expectedSet.Where(name => !actualSet.Contains(name)).ToArray();
        var unexpected = actualSet.Where(name => !expectedSet.Contains(name)).ToArray();
        var duplicates = actual.GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        if (missing.Length > 0 || unexpected.Length > 0 || duplicates.Length > 0 || actual.Count != expectedSet.Count)
            throw new InvalidDataException($"{modelName} output names are incompatible. Missing: [{string.Join(", ", missing)}]; unexpected: [{string.Join(", ", unexpected)}]; duplicates: [{string.Join(", ", duplicates)}]. Expected: [{string.Join(", ", expected)}].");
    }

    internal static void ValidateOutputValues(
        string modelName,
        ModelTensorMetadata actual,
        int[] expectedDimensions,
        IReadOnlyList<float> values)
    {
        if (actual.ElementType != typeof(float) || !actual.Dimensions.SequenceEqual(expectedDimensions))
            throw new InvalidDataException($"{modelName} inference output '{actual.Name}' has type {actual.ElementType.Name}, rank {actual.Dimensions.Count}, shape {FormatShape(actual.Dimensions)}; expected float32 shape {FormatShape(expectedDimensions)}.");

        var expectedLength = expectedDimensions.Aggregate(1, (length, dimension) => checked(length * dimension));
        if (values.Count != expectedLength)
            throw new InvalidDataException($"{modelName} inference output '{actual.Name}' contains {values.Count} values; expected {expectedLength} for shape {FormatShape(expectedDimensions)}.");
        if (values.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException($"{modelName} inference output '{actual.Name}' contains a non-finite value.");
    }

    internal static float RequireProbability(float value, string meaning, string outputName)
    {
        if (!float.IsFinite(value) || value is < 0 or > 1)
            throw new InvalidDataException($"Hand pose output '{outputName}' contains an invalid {meaning} score; expected a finite value in [0, 1].");
        return value;
    }

    private static IReadOnlyList<ModelTensorMetadata> ReadOutputMetadata(InferenceSession session) => session.OutputMetadata
        .Select(item => new ModelTensorMetadata(item.Key, item.Value.ElementType, item.Value.Dimensions))
        .ToArray();

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

    private static bool ShapeMatches(IReadOnlyList<int> actual, IReadOnlyList<int> expected) =>
        actual.Count == expected.Count && actual.Zip(expected).All(pair => pair.First < 0 || pair.First == pair.Second);

    private static string FormatShape(IEnumerable<int> dimensions) => $"[{string.Join(",", dimensions)}]";

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

    private sealed class InvalidPalmCropException(string message) : Exception(message) { }

    private readonly record struct PalmDetection(RectF Box, Vector2[] Landmarks, float Score);

    private readonly record struct Vector2(float X, float Y);

    private readonly record struct Vector3(float X, float Y, float Z);

    private readonly record struct RectF(float X, float Y, float Width, float Height)
    {
        public float Right => X + Width;
        public float Bottom => Y + Height;
    }
}

internal sealed record ModelTensorMetadata(string Name, Type ElementType, IReadOnlyList<int> Dimensions);
