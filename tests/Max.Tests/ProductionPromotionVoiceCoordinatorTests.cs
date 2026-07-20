using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace demo;

public sealed class ProductionPromotionVoiceCoordinatorTests
{
    [Fact]
    public async Task UnrelatedSpeechIsNotHandled()
    {
        var dispatcher = new RecordingDispatcher();
        var coordinator = CreateCoordinator(dispatcher);

        var result = await coordinator.TryHandleAsync(
            "Tell me about the staging deployment.",
            TestContext.Current.CancellationToken);

        Assert.False(result.Handled);
        Assert.Empty(dispatcher.Approvals);
    }

    [Fact]
    public async Task PromotionRequiresASecondReviewConfirmation()
    {
        var dispatcher = new RecordingDispatcher();
        var coordinator = CreateCoordinator(dispatcher);

        var request = await coordinator.TryHandleAsync(
            "The staging deployment is good. Use it for production.",
            TestContext.Current.CancellationToken);
        var confirmation = await coordinator.TryHandleAsync(
            "Review it.",
            TestContext.Current.CancellationToken);

        Assert.True(request.Handled);
        Assert.Contains("review", request.Reply, StringComparison.OrdinalIgnoreCase);
        Assert.True(confirmation.Handled);
        Assert.Equal([ProductionPromotionApproval.Review], dispatcher.Approvals);
    }

    [Fact]
    public async Task AutoMergeRequiresExplicitProductionPhrase()
    {
        var dispatcher = new RecordingDispatcher();
        var coordinator = CreateCoordinator(dispatcher);

        await coordinator.TryHandleAsync(
            "Promote staging to production.",
            TestContext.Current.CancellationToken);
        var ambiguous = await coordinator.TryHandleAsync(
            "Auto merge it.",
            TestContext.Current.CancellationToken);

        Assert.True(ambiguous.Handled);
        Assert.Empty(dispatcher.Approvals);

        var confirmed = await coordinator.TryHandleAsync(
            "Auto merge production.",
            TestContext.Current.CancellationToken);

        Assert.True(confirmed.Handled);
        Assert.Equal([ProductionPromotionApproval.AutoMerge], dispatcher.Approvals);
    }

    [Fact]
    public async Task CancelClearsThePendingPromotion()
    {
        var dispatcher = new RecordingDispatcher();
        var coordinator = CreateCoordinator(dispatcher);

        await coordinator.TryHandleAsync(
            "Promote staging to production.",
            TestContext.Current.CancellationToken);
        var cancelled = await coordinator.TryHandleAsync(
            "Cancel.",
            TestContext.Current.CancellationToken);
        var laterReview = await coordinator.TryHandleAsync(
            "Review it.",
            TestContext.Current.CancellationToken);

        Assert.True(cancelled.Handled);
        Assert.False(laterReview.Handled);
        Assert.Empty(dispatcher.Approvals);
    }

    [Fact]
    public async Task UnconfiguredDispatcherDoesNotBeginConfirmation()
    {
        var dispatcher = new RecordingDispatcher { IsConfigured = false };
        var coordinator = CreateCoordinator(dispatcher);

        var request = await coordinator.TryHandleAsync(
            "Promote staging to production.",
            TestContext.Current.CancellationToken);
        var laterReview = await coordinator.TryHandleAsync(
            "Review it.",
            TestContext.Current.CancellationToken);

        Assert.True(request.Handled);
        Assert.Contains("not configured", request.Reply, StringComparison.OrdinalIgnoreCase);
        Assert.False(laterReview.Handled);
        Assert.Empty(dispatcher.Approvals);
    }

    private static ProductionPromotionVoiceCoordinator CreateCoordinator(
        RecordingDispatcher dispatcher) =>
        new(dispatcher, NullLogger.Instance);

    private sealed class RecordingDispatcher : IProductionPromotionDispatcher
    {
        public bool IsConfigured { get; set; } = true;

        public List<ProductionPromotionApproval> Approvals { get; } = [];

        public Task DispatchAsync(
            ProductionPromotionApproval approval,
            CancellationToken cancellationToken = default)
        {
            Approvals.Add(approval);
            return Task.CompletedTask;
        }
    }
}
