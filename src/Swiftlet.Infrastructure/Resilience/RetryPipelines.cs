using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Swiftlet.Application.Abstractions;

namespace Swiftlet.Infrastructure.Resilience;

/// <summary>
/// Counts the consecutive failures of an endlessly retried operation, so its delay can start over once it works again.
/// </summary>
internal sealed class FailureStreak
{
    /// <summary>
    /// Gets the number of failures since the last success.
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// Records a failure.
    /// </summary>
    public void Add()
    {
        Count++;
    }

    /// <summary>
    /// Records that the operation works again, so the next failure waits the shortest delay.
    /// </summary>
    public void Reset()
    {
        Count = 0;
    }
}

/// <summary>
/// The retry pipelines of the application, so every delay and limit is defined in one place.
/// </summary>
internal static class RetryPipelines
{
    /// <summary>
    /// The first delay of the background operations.
    /// </summary>
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest delay of the endlessly retried operations.
    /// </summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The delay after a sign-in that is no longer valid, so a revoked token is not hammered.
    /// </summary>
    public static readonly TimeSpan ReauthenticationDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The first delay between attempts of a token refresh, which a user may be waiting for.
    /// </summary>
    public static readonly TimeSpan TokenRefreshDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The key under which an execution carries its failure streak.
    /// </summary>
    private static readonly ResiliencePropertyKey<FailureStreak> StreakKey = new("Swiftlet.FailureStreak");

    /// <summary>
    /// Builds a pipeline that repeats an operation for as long as it fails, doubling the delay from <see cref="InitialDelay"/>
    /// up to <see cref="MaxDelay"/>. An expired sign-in waits <see cref="ReauthenticationDelay"/> without growing the delay.
    /// Execute it with a context from <see cref="Rent"/>.
    /// </summary>
    /// <param name="timeProvider">The clock used for the delays.</param>
    /// <param name="shouldRetry">Which failures are retried; the others are thrown to the caller.</param>
    /// <param name="onRetry">Told about every failure and the delay before the next attempt.</param>
    /// <returns>The pipeline.</returns>
    public static ResiliencePipeline Endless(
        TimeProvider timeProvider,
        Func<Exception, bool> shouldRetry,
        Action<Exception, TimeSpan> onRetry)
    {
        return Build(
            timeProvider,
            new RetryStrategyOptions
            {
                MaxRetryAttempts = int.MaxValue,
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is { } error
                    && !args.Context.CancellationToken.IsCancellationRequested
                    && shouldRetry(error)),
                DelayGenerator = args =>
                {
                    if (args.Outcome.Exception is OAuthReauthenticationRequiredException)
                    {
                        return ValueTask.FromResult<TimeSpan?>(ReauthenticationDelay);
                    }

                    var streak = args.Context.Properties.GetValue(StreakKey, new FailureStreak());

                    // The exponent is capped well beyond the point where MaxDelay applies, so it can never overflow.
                    var delay = InitialDelay * Math.Pow(2, Math.Min(streak.Count, 16));
                    streak.Add();
                    return ValueTask.FromResult<TimeSpan?>(delay < MaxDelay ? delay : MaxDelay);
                },
                OnRetry = args =>
                {
                    onRetry(args.Outcome.Exception!, args.RetryDelay);
                    return ValueTask.CompletedTask;
                },
            });
    }

    /// <summary>
    /// Builds a pipeline that repeats an operation after transient failures a limited number of times, doubling the delay.
    /// </summary>
    /// <param name="timeProvider">The clock used for the delays.</param>
    /// <param name="retries">How many times the operation is repeated.</param>
    /// <param name="firstDelay">The delay before the first repetition.</param>
    /// <param name="logger">Receives a warning per repetition.</param>
    /// <param name="operation">What is being attempted, for the log.</param>
    /// <returns>The pipeline.</returns>
    public static ResiliencePipeline Transient(
        TimeProvider timeProvider,
        int retries,
        TimeSpan firstDelay,
        ILogger? logger,
        string operation)
    {
        return Build(
            timeProvider,
            new RetryStrategyOptions
            {
                MaxRetryAttempts = retries,
                BackoffType = DelayBackoffType.Exponential,
                Delay = firstDelay,
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is { } error
                    && !args.Context.CancellationToken.IsCancellationRequested
                    && TransientErrors.IsTransient(error)),
                OnRetry = args =>
                {
                    logger?.LogWarning(
                        args.Outcome.Exception,
                        "{Operation} failed (attempt {Attempt}); retrying in {Delay}.",
                        operation,
                        args.AttemptNumber + 1,
                        args.RetryDelay);
                    return ValueTask.CompletedTask;
                },
            });
    }

    /// <summary>
    /// Rents a context for <see cref="Endless"/> that carries the failure streak; return it with <see cref="ResilienceContextPool"/>.
    /// </summary>
    /// <param name="streak">The streak the caller resets whenever the operation works.</param>
    /// <param name="cancellationToken">The token that ends the retries.</param>
    /// <returns>The context.</returns>
    public static ResilienceContext Rent(FailureStreak streak, CancellationToken cancellationToken)
    {
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        context.Properties.Set(StreakKey, streak);
        return context;
    }

    /// <summary>
    /// Builds a pipeline of one retry strategy on the given clock.
    /// </summary>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="options">The retry options.</param>
    /// <returns>The pipeline.</returns>
    private static ResiliencePipeline Build(TimeProvider timeProvider, RetryStrategyOptions options)
    {
        return new ResiliencePipelineBuilder { TimeProvider = timeProvider }
            .AddRetry(options)
            .Build();
    }
}
