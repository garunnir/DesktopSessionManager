namespace DesktopSessionManager;

internal enum RestoreAction { Reuse, Ambiguous, Missing, Launch }
internal static class RestorePolicy
{
    public static RestoreAction Decide(int matchCount, bool layoutOnly) => matchCount switch
    {
        > 1 => RestoreAction.Ambiguous,
        1 => RestoreAction.Reuse,
        _ => layoutOnly ? RestoreAction.Missing : RestoreAction.Launch
    };
    public static async Task RunBatch(IEnumerable<SavedWindow> targets, Func<SavedWindow, Task> restore, CancellationToken token)
    {
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            await restore(target);
            await Task.Yield();
        }
    }
}
