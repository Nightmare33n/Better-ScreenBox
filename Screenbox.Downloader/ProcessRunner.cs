using System.Diagnostics;
using System.Text;

namespace Screenbox.Downloader;

/// <summary>
/// Runs a console tool without a window and streams its output line by line.
/// </summary>
internal static class ProcessRunner
{
    /// <summary>
    /// The result of a finished process.
    /// </summary>
    /// <param name="ExitCode">The process exit code.</param>
    /// <param name="StandardError">Everything written to standard error.</param>
    public sealed record Result(int ExitCode, string StandardError);

    /// <summary>
    /// Starts <paramref name="fileName"/> and waits for it, killing the whole process tree on cancellation.
    /// </summary>
    /// <param name="fileName">The executable to run.</param>
    /// <param name="arguments">Arguments, quoted by the runtime.</param>
    /// <param name="onLine">Called for every line on either stream, from a background thread.</param>
    /// <param name="cancellationToken">Cancels and kills the process.</param>
    /// <returns>The exit code and standard error text.</returns>
    public static async Task<Result> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // yt-dlp is a Python program: force UTF-8 so non-Latin titles and paths survive the pipe.
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";

        using Process process = new() { StartInfo = startInfo };
        StringBuilder standardError = new();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) onLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (standardError)
            {
                standardError.AppendLine(e.Data);
            }

            onLine?.Invoke(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                // yt-dlp spawns ffmpeg and Deno; killing only the parent would leave them writing files.
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        // WaitForExitAsync can return before the redirected streams are drained.
        process.WaitForExit();

        lock (standardError)
        {
            return new Result(process.ExitCode, standardError.ToString());
        }
    }
}
