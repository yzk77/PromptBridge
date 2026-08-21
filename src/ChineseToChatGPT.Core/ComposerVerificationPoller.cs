namespace ChineseToChatGPT.Core;

public static class ComposerVerificationPoller
{
    public static readonly TimeSpan[] DefaultDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(350),
        TimeSpan.FromMilliseconds(500)
    ];

    public static async Task<(bool Success, int Attempts)> VerifyAsync(
        Func<int, CancellationToken, Task<bool>> verifyAttempt,
        CancellationToken cancellationToken,
        IReadOnlyList<TimeSpan>? delays = null)
    {
        var schedule = delays ?? DefaultDelays;
        for (var index = 0; index < schedule.Count; index++)
        {
            await Task.Delay(schedule[index], cancellationToken).ConfigureAwait(false);
            if (await verifyAttempt(index + 1, cancellationToken).ConfigureAwait(false))
            {
                return (true, index + 1);
            }
        }

        return (false, schedule.Count);
    }
}
