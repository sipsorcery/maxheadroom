using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace demo;

/// <summary>
/// Small server-side client for the cluster-internal Codex code-agent controller.
/// The controller bearer token never reaches the browser.
/// </summary>
internal sealed class CodeAgentClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _repositoryGate = new(1, 1);
    private bool _repositoryReady;

    public bool IsConfigured { get; }
    public string Repository { get; }

    public CodeAgentClient(string endpoint, string apiToken, string repository)
    {
        Repository = repository?.Trim() ?? string.Empty;
        IsConfigured =
            Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var baseAddress) &&
            !string.IsNullOrWhiteSpace(apiToken) &&
            !string.IsNullOrWhiteSpace(Repository);

        _httpClient = new HttpClient
        {
            BaseAddress = baseAddress,
            // The first repository registration may need to clone over SSH.
            Timeout = TimeSpan.FromMinutes(6),
        };

        if (!string.IsNullOrWhiteSpace(apiToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiToken.Trim());
        }
    }

    public async Task<CodeAgentHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        // /healthz is deliberately unauthenticated for Kubernetes probes. Check a
        // small authenticated endpoint too so the UI does not report "Connected"
        // when Max has the wrong controller bearer token or GitHub identity.
        await SendAsync<JsonElement>(
            HttpMethod.Get,
            "github/ssh-public-key",
            null,
            cancellationToken);
        return await SendAsync<CodeAgentHealth>(
            HttpMethod.Get,
            "healthz",
            null,
            cancellationToken);
    }

    public async Task<CodeAgentTaskSnapshot> CreateTaskAsync(
        string message,
        CancellationToken cancellationToken)
    {
        await EnsureRepositoryAsync(cancellationToken);

        return EnsureAssignedRepository(await SendAsync<CodeAgentTaskSnapshot>(
            HttpMethod.Post,
            "tasks",
            new
            {
                repository = Repository,
                prompt = BuildDeliveryPrompt(message),
            },
            cancellationToken));
    }

    public async Task<CodeAgentTaskSnapshot> GetTaskAsync(
        string taskId,
        CancellationToken cancellationToken) =>
        EnsureAssignedRepository(await SendAsync<CodeAgentTaskSnapshot>(
            HttpMethod.Get,
            $"tasks/{Uri.EscapeDataString(taskId)}",
            null,
            cancellationToken));

    public async Task<CodeAgentTaskSnapshot> AddFeedbackAsync(
        string taskId,
        string message,
        CancellationToken cancellationToken) =>
        EnsureAssignedRepository(await SendAsync<CodeAgentTaskSnapshot>(
            HttpMethod.Post,
            $"tasks/{Uri.EscapeDataString(taskId)}/feedback",
            new { message },
            cancellationToken));

    private async Task EnsureRepositoryAsync(CancellationToken cancellationToken)
    {
        if (_repositoryReady)
        {
            return;
        }

        await _repositoryGate.WaitAsync(cancellationToken);
        try
        {
            if (_repositoryReady)
            {
                return;
            }

            await SendAsync<JsonElement>(
                HttpMethod.Post,
                "repositories",
                new { repository = Repository },
                cancellationToken);
            _repositoryReady = true;
        }
        finally
        {
            _repositoryGate.Release();
        }
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        object body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body != null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await ReadErrorAsync(response, cancellationToken);
            throw new CodeAgentRequestException(response.StatusCode, detail);
        }

        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        return result ?? throw new CodeAgentRequestException(
            HttpStatusCode.BadGateway,
            "Code agent returned an empty response.");
    }

    private static async Task<string> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return $"Code agent returned HTTP {(int)response.StatusCode}.";
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                return error.GetString() ?? content;
            }
        }
        catch (JsonException)
        {
            // Preserve a non-JSON error response below.
        }

        return content.Length <= 1000 ? content : content[..1000];
    }

    private static string BuildDeliveryPrompt(string message) => $"""
        Implement the following request in the Max Headroom repository:

        {message.Trim()}

        Deliver the result through GitHub:
        - Inspect the repository status and fetch staging from origin.
        - Create a new, descriptively named branch from origin/staging; never commit directly to staging or master.
        - Implement the smallest complete change that satisfies the request.
        - Run focused validation appropriate to the change.
        - Commit the intended files and push the branch using the configured GitHub identity.
        - Create a ready-for-review pull request targeting staging with a useful title and description; do not use draft mode.
        - Finish by reporting the pull request URL, validation performed, and any remaining risks.
        """;

    private CodeAgentTaskSnapshot EnsureAssignedRepository(CodeAgentTaskSnapshot task)
    {
        if (!string.Equals(task.Repository, Repository, StringComparison.OrdinalIgnoreCase))
        {
            throw new CodeAgentRequestException(
                HttpStatusCode.NotFound,
                "Task is not assigned to the configured repository.");
        }

        return task;
    }

    public void Dispose()
    {
        _repositoryGate.Dispose();
        _httpClient.Dispose();
    }
}

internal sealed record CodeAgentHealth(string Status, int ActiveTasks, int QueuedTasks);

internal sealed record CodeAgentTaskEvent(string At, string Type, string Summary);

internal sealed record CodeAgentTaskSnapshot(
    string Id,
    string Repository,
    string Prompt,
    string Status,
    string CreatedAt,
    string UpdatedAt,
    string FinalResponse,
    string Error,
    IReadOnlyList<CodeAgentTaskEvent> Events);

internal sealed record CodeAgentChatRequest(string Message);

internal sealed class CodeAgentRequestException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public CodeAgentRequestException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }
}
