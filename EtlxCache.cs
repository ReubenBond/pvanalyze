using Microsoft.Diagnostics.Tracing.Etlx;
using System.Diagnostics;

namespace PVAnalyze;

/// <summary>
/// Caches the .nettrace → .etlx conversion so repeated commands on the same trace
/// don't re-parse the file. The .etlx is kept alongside the .nettrace and reused
/// if it's newer than the source.
/// </summary>
public static class EtlxCache
{
    private const string CacheSuffix = ".pvanalyze.etlx";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private const int LockRetryDelayMs = 50;

    public static async Task<string> GetOrCreateEtlxAsync(string nettraceFilePath, CancellationToken cancellationToken = default)
    {
        string etlxPath = nettraceFilePath + CacheSuffix;
        string lockPath = etlxPath + ".lock";

        if (IsFreshCache(nettraceFilePath, etlxPath))
            return etlxPath;

        await using var lockStream = await AcquireLockAsync(lockPath, cancellationToken).ConfigureAwait(false);
        if (IsFreshCache(nettraceFilePath, etlxPath))
            return etlxPath;

        string tempPath = $"{etlxPath}.tmp.{Environment.ProcessId}.{Guid.NewGuid():N}";
        try
        {
            // CreateFromEventPipeDataFile doesn't accept a cancellation token, so once it starts
            // it runs to completion. Check before we begin so a late cancel still bails out cheaply;
            // the Task.Run token only covers the queued-but-not-started window.
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => TraceLog.CreateFromEventPipeDataFile(nettraceFilePath, tempPath), cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(tempPath, etlxPath, overwrite: true);
                return etlxPath;
            }
            catch (IOException publishEx)
            {
                if (!IsFreshCache(nettraceFilePath, etlxPath))
                    throw new IOException($"Failed to publish ETLX cache '{etlxPath}'.", publishEx);

                // Another writer published a fresh cache first. We've already succeeded, so clean
                // up our temp file and use the published cache rather than leaking it on cancel.
                TryDeleteTemp(tempPath);
                return etlxPath;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                TryDeleteTemp(tempPath);
            }
            catch (Exception cleanupEx)
            {
                throw new IOException(
                    $"Failed to clean temporary ETLX cache file '{tempPath}' after conversion failure.",
                    new AggregateException(ex, cleanupEx));
            }
            throw;
        }
    }

    private static bool IsFreshCache(string nettraceFilePath, string etlxPath)
    {
        if (!File.Exists(etlxPath))
            return false;

        var nettraceTime = File.GetLastWriteTimeUtc(nettraceFilePath);
        var etlxTime = File.GetLastWriteTimeUtc(etlxPath);
        return etlxTime >= nettraceTime;
    }

    private static async Task<FileStream> AcquireLockAsync(string lockPath, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (sw.Elapsed < LockTimeout)
            {
                await Task.Delay(LockRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (sw.Elapsed < LockTimeout)
            {
                await Task.Delay(LockRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        if (File.Exists(tempPath))
            File.Delete(tempPath);
    }
}
