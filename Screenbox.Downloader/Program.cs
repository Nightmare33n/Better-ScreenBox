using Screenbox.Core.Downloads.Contracts;
using Windows.Storage;

namespace Screenbox.Downloader;

/// <summary>
/// Entry point of the full-trust download helper.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Starts the helper. Only one instance runs per package; later launches exit right away because the
    /// running instance already watches the commands folder.
    /// </summary>
    /// <param name="args"><c>--root &lt;folder&gt;</c> overrides the state folder when running unpackaged.</param>
    /// <returns>The process exit code.</returns>
    /// <remarks>
    /// Main stays synchronous on purpose: a mutex must be released by the thread that acquired it, and an
    /// <c>await</c> here would resume on a thread-pool thread and crash the process on exit.
    /// </remarks>
    private static int Main(string[] args)
    {
        string root = ResolveRoot(args);
        Directory.CreateDirectory(root);

        // The mutex name includes the root so a packaged and an unpackaged test instance do not block each other.
        string mutexName = @"Local\Screenbox.Downloader." + StableHash(root);
        using Mutex mutex = new(initiallyOwned: false, mutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // The previous instance crashed; the mutex is now ours.
            owned = true;
        }

        if (!owned) return 0;

        Log log = new(Path.Combine(root, "helper.log"));
        try
        {
            log.Write($"Helper started (root: {root})");
            DownloaderHost host = new(root, AppContext.BaseDirectory, log);
            host.RunAsync().GetAwaiter().GetResult();
            log.Write("Helper exited normally");
            return 0;
        }
        catch (Exception e)
        {
            log.Write($"Helper crashed: {e}");
            return 1;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// Finds the folder shared with the app: the package LocalState when packaged, or the --root argument.
    /// </summary>
    private static string ResolveRoot(string[] args)
    {
        int index = Array.IndexOf(args, "--root");
        if (index >= 0 && index + 1 < args.Length)
        {
            return Path.GetFullPath(args[index + 1]);
        }

        return Path.Combine(GetLocalStatePath(), DownloaderPaths.RootFolderName);
    }

    /// <summary>
    /// Reads the LocalState path of the package this process belongs to. Throws when the helper is not packaged.
    /// </summary>
    private static string GetLocalStatePath() => ApplicationData.Current.LocalFolder.Path;

    private static string StableHash(string value)
    {
        // string.GetHashCode is randomized per process, which would defeat a cross-process mutex name.
        uint hash = 2166136261;
        foreach (char c in value.ToUpperInvariant())
        {
            hash = (hash ^ c) * 16777619;
        }

        return hash.ToString("x8");
    }
}
