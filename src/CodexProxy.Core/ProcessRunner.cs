using System.ComponentModel;
using System.Diagnostics;

namespace CodexProxy.Core;

internal static class ProcessRunner
{
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(ProcessStartInfo info, TimeSpan timeout,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start process.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error).WaitAsync(deadline.Token).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            cancellation.ThrowIfCancellationRequested();
            throw new TimeoutException();
        }
    }
}
