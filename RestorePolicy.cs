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
        // Serialize indistinguishable sessions; independent projects/apps can start together.
        string Key(SavedWindow w) => System.Text.Json.JsonSerializer.Serialize(new[]
        {
            w.Path.TrimEnd('\\', '/').Replace('/', '\\').ToUpperInvariant(),
            (string.IsNullOrWhiteSpace(w.ProjectPath) ? "" : w.ProjectPath).TrimEnd('\\', '/').Replace('/', '\\').ToUpperInvariant(),
            !string.IsNullOrWhiteSpace(w.ProjectPath) ? "" : System.Text.Json.JsonSerializer.Serialize(w.OpenFiles.Select(f => f.Replace('/', '\\').ToUpperInvariant()).Distinct().OrderBy(f => f))
        });
        using var slots = new SemaphoreSlim(4);
        async Task RunGroup(IEnumerable<SavedWindow> group)
        {
            foreach (var target in group)
            {
                await slots.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    await restore(target);
                }
                finally { slots.Release(); }
            }
        }
        await Task.WhenAll(targets.GroupBy(Key).Select(RunGroup));
    }
}
