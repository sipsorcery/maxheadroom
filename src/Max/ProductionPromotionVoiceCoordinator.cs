using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace demo;

internal sealed record ProductionPromotionVoiceResult(bool Handled, string Reply)
{
    public static readonly ProductionPromotionVoiceResult NotHandled = new(false, string.Empty);
}

internal sealed class ProductionPromotionVoiceCoordinator
{
    private static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromMinutes(2);

    private readonly IProductionPromotionDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _confirmationExpiresAt;

    public ProductionPromotionVoiceCoordinator(
        IProductionPromotionDispatcher dispatcher,
        ILogger logger,
        TimeProvider timeProvider = null)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ProductionPromotionVoiceResult> TryHandleAsync(
        string transcript,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(transcript);
        if (string.IsNullOrEmpty(normalized))
        {
            return ProductionPromotionVoiceResult.NotHandled;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (_confirmationExpiresAt <= now)
            {
                _confirmationExpiresAt = null;
            }

            if (_confirmationExpiresAt != null)
            {
                if (ContainsWord(normalized, "cancel") ||
                    normalized.Contains("do not promote", StringComparison.Ordinal))
                {
                    _confirmationExpiresAt = null;
                    return new(true, "Production promotion cancelled. Staging has not been changed.");
                }

                if (ContainsWord(normalized, "review"))
                {
                    return await DispatchAsync(
                        ProductionPromotionApproval.Review,
                        "I've asked GitHub to create a Max release pull request and leave it open for your review. After it is merged, GitHub will verify and propose the exact staged image for production.",
                        cancellationToken).ConfigureAwait(false);
                }

                if (normalized.Contains("auto merge production", StringComparison.Ordinal))
                {
                    return await DispatchAsync(
                        ProductionPromotionApproval.AutoMerge,
                        "I've asked GitHub to create and automatically merge the Max release pull request, then verify and promote the exact staged image through the production deployment workflow.",
                        cancellationToken).ConfigureAwait(false);
                }

                return new(
                    true,
                    "Please say review it, auto merge production, or cancel.");
            }

            if (!IsPromotionRequest(normalized))
            {
                return ProductionPromotionVoiceResult.NotHandled;
            }

            if (!_dispatcher.IsConfigured)
            {
                return new(
                    true,
                    "Production promotion is not configured. The staging deployment has not been changed.");
            }

            _confirmationExpiresAt = now + ConfirmationLifetime;
            return new(
                true,
                "The current staging deployment will be prepared as a Max release. Would you like to review the release pull request, or should I merge the release and deployment pull requests automatically? Say review it, auto merge production, or cancel.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ProductionPromotionVoiceResult> DispatchAsync(
        ProductionPromotionApproval approval,
        string successReply,
        CancellationToken cancellationToken)
    {
        _confirmationExpiresAt = null;
        try
        {
            await _dispatcher.DispatchAsync(approval, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Requested production promotion with approval mode {ApprovalMode}.",
                approval);
            return new(true, successReply);
        }
        catch (Exception excp)
        {
            _logger.LogError(excp, "Failed to request production promotion.");
            return new(
                true,
                "I couldn't start the production promotion. The staging deployment has not been changed.");
        }
    }

    private static bool IsPromotionRequest(string normalized)
    {
        var mentionsRoute =
            ContainsWord(normalized, "staging") &&
            ContainsWord(normalized, "production");
        if (!mentionsRoute)
        {
            return false;
        }

        return ContainsWord(normalized, "promote") ||
            (ContainsWord(normalized, "good") &&
             (ContainsWord(normalized, "use") || ContainsWord(normalized, "used")));
    }

    private static string Normalize(string value)
    {
        var normalized = new StringBuilder(value.Length);
        var previousWasSpace = true;
        foreach (var ch in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                normalized.Append(ch);
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                normalized.Append(' ');
                previousWasSpace = true;
            }
        }
        return normalized.ToString().Trim();
    }

    private static bool ContainsWord(string value, string word) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(word, StringComparer.Ordinal);
}
