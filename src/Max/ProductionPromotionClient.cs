using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace demo;

internal enum ProductionPromotionApproval
{
    Review,
    AutoMerge,
}

internal interface IProductionPromotionDispatcher
{
    bool IsConfigured { get; }

    Task DispatchAsync(
        ProductionPromotionApproval approval,
        CancellationToken cancellationToken = default);
}

internal sealed class ProductionPromotionClient : IProductionPromotionDispatcher, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _token;
    private readonly string _repository;

    public ProductionPromotionClient(string token, string repository)
    {
        _token = token?.Trim() ?? string.Empty;
        _repository = string.IsNullOrWhiteSpace(repository)
            ? "sipsorcery/maxheadroom"
            : repository.Trim();
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("max-headroom-production-promotion/1.0");
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_token) &&
        _repository.Count(ch => ch == '/') == 1;

    public async Task DispatchAsync(
        ProductionPromotionApproval approval,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Production promotion is not configured.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"repos/{_repository}/dispatches");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                event_type = "max-production-release-requested",
                client_payload = new
                {
                    approval_mode = approval == ProductionPromotionApproval.AutoMerge
                        ? "auto"
                        : "review",
                    requested_by = "max-voice",
                },
            }),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        if (detail.Length > 1000)
        {
            detail = detail[..1000];
        }
        throw new HttpRequestException(
            $"GitHub dispatch failed with HTTP {(int)response.StatusCode}: {detail}");
    }

    public void Dispose() => _httpClient.Dispose();
}
