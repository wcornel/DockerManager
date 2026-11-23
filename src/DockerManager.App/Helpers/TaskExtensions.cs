using DockerManager.App.Services;

namespace DockerManager.App.Helpers;

public static class TaskExtensions
{
    /// <summary>
    /// Safely executes a fire-and-forget task, preventing unobserved task exceptions
    /// and logging failures to debug output (or a custom handler).
    /// </summary>
    public static async void SafeFireAndForget(this Task task, string? context = null, Action<Exception>? onException = null)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected during shutdown or rapid switching
        }
        catch (Exception ex)
        {
            var prefix = string.IsNullOrWhiteSpace(context) ? "SafeFireAndForget" : context;
            AppLogger.LogError(prefix, ex, "Unhandled background exception");
            try
            {
                onException?.Invoke(ex);
            }
            catch (Exception callbackEx)
            {
                AppLogger.LogError(prefix, callbackEx, "Unhandled exception inside SafeFireAndForget error callback");
            }
        }
    }
}
