using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Bendmark.Coordinator;

internal sealed class EmulatorLab(IHttpClientFactory clients, IConfiguration configuration) : IDisposable
{
    public const string ClientName = "EmulatorLab";
    private readonly Uri? _origin = TelemetryOptions.ParseHttpUrl(configuration["Emulator:Origin"]);
    private readonly SemaphoreSlim _gate = new(1);

    public async Task<Run> QueueRunAsync(Scenario scenario, int agentCount, RunStore store, RunQueue queue,
        CancellationToken cancellationToken)
    {
        // Запись о будущем прогоне и смена настроек упорядочены даже при двух вкладках.
        var managed = Manages(scenario.Target?.Url);
        if (managed)
            await _gate.WaitAsync(cancellationToken);
        try
        {
            var run = store.Create(scenario, agentCount);
            queue.Enqueue(run.Id);
            return run;
        }
        finally
        {
            if (managed)
                _gate.Release();
        }
    }

    public Task<IResult> ReadAsync(CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, null, cancellationToken);

    public async Task<IResult> UpdateAsync(HttpRequest request, RunStore store, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType())
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (store.HasPendingTarget(Manages))
                return Results.Problem("У эмулятора есть активный или ожидающий прогон. Дождись его завершения.",
                    statusCode: StatusCodes.Status409Conflict);

            return await SendAsync(HttpMethod.Put, request.Body, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool Manages(string? targetUrl) =>
        _origin is not null && TelemetryOptions.ParseHttpUrl(targetUrl) is { } target &&
        Uri.Compare(target, _origin, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped,
            StringComparison.OrdinalIgnoreCase) == 0;

    private async Task<IResult> SendAsync(HttpMethod method, Stream? body, CancellationToken cancellationToken)
    {
        if (_origin is null)
            return Unavailable();
        using var client = clients.CreateClient(ClientName);
        using var request = new HttpRequestMessage(method, new Uri(_origin, "/config"));
        if (body is not null)
        {
            request.Content = new StreamContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.BadRequest or HttpStatusCode.Conflict))
                return Unavailable();
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var data = payload.RootElement;
            if (response.StatusCode != HttpStatusCode.OK)
                return Results.Json(data.Clone(), statusCode: (int)response.StatusCode);
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("settings", out var settings) ||
                !data.TryGetProperty("capacity_rps", out var capacity) || capacity.ValueKind != JsonValueKind.Number)
                return Unavailable();
            return Results.Ok(new
            {
                target_url = new Uri(_origin, "/work").AbsoluteUri,
                settings = settings.Clone(),
                capacity_rps = capacity.GetDouble(),
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable();
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException)
        {
            return Unavailable();
        }
    }

    private static IResult Unavailable() =>
        Results.Problem("Эмулятор недоступен. В Compose его запускает профиль emulator.",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    public void Dispose() => _gate.Dispose();
}
