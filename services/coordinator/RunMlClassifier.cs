using System.Text.Json;

namespace Bendmark.Coordinator;

public sealed record MlRunMeasurements(double PeakP99Ms, double ErrorFraction, long PeakQueueLength, double RejectedFraction);

public sealed record MlRunDiagnosis(string Label, MlRunMeasurements Measurements);

internal sealed class RunMlClassifier
{
    private static readonly string[] Labels = ["healthy", "reject", "saturation", "slow"];
    private static readonly string[] FeatureNames =
        ["log_peak_p99_ms", "error_fraction", "log_peak_queue_length", "rejected_fraction"];
    private readonly Model _model = LoadModel();

    public MlRunDiagnosis? Predict(Run run)
    {
        if (run.Status != RunStatus.Completed ||
            !Uri.TryCreate(run.Scenario.Target?.Url, UriKind.Absolute, out var target) ||
            target.Scheme != Uri.UriSchemeHttp || target.Host != "emulator" || target.Port != 8080 ||
            run.Telemetry.Status != TelemetryStatus.Complete || run.Telemetry.Snapshots.Count < 2 ||
            run.Steps.Count == 0 || run.Steps.Any(step => step.SkippedCount > 0 || step.RequestCount == 0))
            return null;

        var samples = run.Telemetry.Snapshots;
        var first = samples[0].Snapshot;
        var last = samples[^1].Snapshot;
        if (first.RejectedTotal is not { } firstRejected || last.RejectedTotal is not { } lastRejected ||
            lastRejected < firstRejected || samples.Any(sample => sample.Snapshot.StartedAt != first.StartedAt))
            return null;

        var attempts = run.Steps.Sum(step => (double)step.RequestCount);
        var measurements = new MlRunMeasurements(
            run.Steps.Max(step => step.LatencyMs.P99),
            run.Steps.Sum(step => (double)step.Errors.Count) / attempts,
            samples.Max(sample => sample.Snapshot.QueueLength ?? 0),
            (lastRejected - firstRejected) / attempts);

        // Тот же порядок и преобразования, что в features_for() из scripts/evaluate_ml.py.
        double[] features = [double.LogP1(measurements.PeakP99Ms), measurements.ErrorFraction,
            double.LogP1(measurements.PeakQueueLength), measurements.RejectedFraction];
        var normalized = features.Select((value, index) => (value - _model.Means[index]) / _model.Scales[index]).ToArray();
        var label = Labels.MinBy(label => normalized.Zip(_model.Centroids[label],
            (value, center) => (value - center) * (value - center)).Sum())!;
        return new MlRunDiagnosis(label, measurements);
    }

    private static Model LoadModel()
    {
        using var stream = typeof(RunMlClassifier).Assembly.GetManifestResourceStream("Bendmark.EmulatorMlModel")
            ?? throw new InvalidOperationException("Модель ML эмулятора не включена в сборку");
        var model = JsonSerializer.Deserialize<Model>(stream, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        });
        if (model is null || model.Method != "nearest_centroid" || !model.Features.SequenceEqual(FeatureNames) ||
            model.Means.Length != FeatureNames.Length || model.Scales.Length != FeatureNames.Length ||
            model.Scales.Any(scale => !double.IsFinite(scale) || scale <= 0) ||
            Labels.Any(label => !model.Centroids.TryGetValue(label, out var center) || center.Length != FeatureNames.Length))
            throw new InvalidOperationException("Неизвестный формат модели ML эмулятора");
        return model;
    }

    private sealed record Model(string Method, string[] Features, double[] Means, double[] Scales,
        Dictionary<string, double[]> Centroids);
}
