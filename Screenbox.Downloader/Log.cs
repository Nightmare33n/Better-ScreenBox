namespace Screenbox.Downloader;

/// <summary>
/// Minimal append-only log. The helper has no console, so this file is the only way to see what happened.
/// </summary>
internal sealed class Log
{
    private const long MaxBytes = 1024 * 1024;
    private readonly string _path;
    private readonly Lock _lock = new();

    public Log(string path)
    {
        _path = path;
        try
        {
            // Keep one previous log around instead of growing without bound.
            FileInfo info = new(path);
            if (info.Exists && info.Length > MaxBytes)
            {
                File.Move(path, path + ".old", overwrite: true);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Appends a timestamped line. Logging failures are ignored on purpose: they must never stop a download.
    /// </summary>
    public void Write(string message)
    {
        lock (_lock)
        {
            try
            {
                File.AppendAllText(_path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
