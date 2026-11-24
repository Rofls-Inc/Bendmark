using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Bendmark.Coordinator;

public sealed record RunExplanationSource(Guid RunId, string? ScenarioName, DateTimeOffset? FinishedAt, string Summary);

public sealed class RunEmbeddingSearch(HttpClient client, RunStore store, ILogger<RunEmbeddingSearch> logger)
{
    private const string Model = "text-embedding-3-small";
    private const int Dimensions = 256;
    private const int CandidateLimit = 40;
    private const int MatchLimit = 3;
    private const double MinimumSimilarity = 0.65;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    internal sealed record Match(RunExplanationSource Source, JsonElement Report);

    internal async Task<IReadOnlyList<Match>> FindAsync(Run run, string apiKey, CancellationToken cancellationToken)
    {
        if (run.Analysis?.Status is not (AnalysisStatus.Found or AnalysisStatus.NotFound) || CreateDocument(run) is not { } query)
            return [];

        try
        {
            var candidates = store.ListPastReports(run.Id, run.Analysis, CandidateLimit)
                .Select(CreateDocument)
                .OfType<ReportDocument>()
                .DistinctBy(document => document.Fingerprint)
                .ToArray();
            if (candidates.Length == 0)
                return [];

            var documents = candidates.Prepend(query).DistinctBy(document => document.Fingerprint).ToArray();
            var embeddings = store.GetReportEmbeddings(documents.Select(document => document.Fingerprint));
            var missing = documents.Where(document => !embeddings.ContainsKey(document.Fingerprint)).ToArray();
            if (missing.Length > 0)
            {
                var generated = await EmbedAsync(missing, apiKey, cancellationToken);
                store.SaveReportEmbeddings(generated);
                foreach (var (fingerprint, embedding) in generated)
                    embeddings.Add(fingerprint, embedding);
            }

            return candidates
                .Select(candidate => (Candidate: candidate, Similarity: Cosine(embeddings[query.Fingerprint], embeddings[candidate.Fingerprint])))
                .Where(match => match.Similarity >= MinimumSimilarity)
                .OrderByDescending(match => match.Similarity)
                .Take(MatchLimit)
                .Select(match => new Match(match.Candidate.Source, match.Candidate.Report))
                .ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Поиск похожих разборов не ответил вовремя для прогона {RunId}", run.Id);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or SqliteException)
        {
            logger.LogWarning("Поиск похожих разборов недоступен для прогона {RunId} ({ErrorType}, HTTP {StatusCode})",
                run.Id, e.GetType().Name, (e as HttpRequestException)?.StatusCode);
        }
        return [];
    }

    private async Task<Dictionary<string, float[]>> EmbedAsync(
        ReportDocument[] documents, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/embeddings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new
        {
            model = Model,
            input = documents.Select(document => document.Report.GetRawText()).ToArray(),
            dimensions = Dimensions,
            encoding_format = "float",
        });
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(Json, cancellationToken);
        if (result?.Data is not { } data || data.Length != documents.Length)
            throw new JsonException("Некорректное число эмбеддингов");

        var embeddings = new Dictionary<string, float[]>();
        foreach (var item in data)
        {
            if (item?.Index is not int index || index < 0 || index >= documents.Length ||
                item.Embedding is not { Length: Dimensions } vector ||
                !vector.All(float.IsFinite) || !vector.Any(value => value != 0) ||
                !embeddings.TryAdd(documents[index].Fingerprint, vector))
                throw new JsonException("Некорректный эмбеддинг отчёта");
        }
        return embeddings;
    }

    private static ReportDocument? CreateDocument(Run run)
    {
        var analysis = run.Analysis!;
        var steps = run.Steps.Take(analysis.AnalyzedSteps)
            .Where(step => step.RequestCount > 0 && step.SkippedCount == 0).ToArray();
        if (steps.Length == 0)
            return null;

        var failed = Array.FindIndex(steps, step => step.Index == analysis.FailedStepIndex);
        var lastStable = analysis.Status == AnalysisStatus.NotFound ? steps[^1]
            : failed > 0 ? steps[failed - 1] : null;
        var firstFailure = failed < 0 ? null : steps[failed];
        var confirmation = failed >= 0 && analysis.LimitConfirmed == true && failed + 1 < steps.Length &&
            steps[failed + 1].Index == steps[failed].Index + 1 ? steps[failed + 1] : null;
        var report = JsonSerializer.SerializeToElement(new
        {
            analysis,
            first_step = steps[0],
            last_stable_step = lastStable,
            first_failure = firstFailure,
            confirmation_step = confirmation,
        }, Json);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Model}/{Dimensions}\n{report.GetRawText()}")));
        var source = new RunExplanationSource(run.Id, run.Scenario.Name, run.FinishedAt,
            analysis.Diagnosis?.Explanation ?? analysis.Message);
        return new ReportDocument(source, report, fingerprint);
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }
        return dot / Math.Sqrt(normA * normB);
    }

    private sealed record ReportDocument(RunExplanationSource Source, JsonElement Report, string Fingerprint);
    private sealed record EmbeddingResponse(EmbeddingItem?[]? Data);
    private sealed record EmbeddingItem(int? Index, float[]? Embedding);
}
