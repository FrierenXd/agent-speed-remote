using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace AgentSpeed.Core.Remote;

/// <summary>One read-only SSH stream. Authentication and known hosts stay in the system SSH configuration.</summary>
public sealed class RemoteConnection
{
    public static async IAsyncEnumerable<string> Lines(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var line = new MemoryStream();
        var lastFrame = Stopwatch.GetTimestamp();
        while (true)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var remaining = TimeSpan.FromSeconds(30) - Stopwatch.GetElapsedTime(lastFrame);
            if (remaining <= TimeSpan.Zero) throw new IOException("Remote frame timed out.");
            deadline.CancelAfter(remaining);
            var count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
            if (count == 0) yield break;
            var start = 0;
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != '\n') continue;
                line.Write(buffer, start, i - start);
                if (line.Length > RemoteFrame.MaxLineBytes) throw new IOException("Remote frame exceeded size limit.");
                yield return new UTF8Encoding(false, true).GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
                lastFrame = Stopwatch.GetTimestamp();
                line.SetLength(0);
                start = i + 1;
            }
            line.Write(buffer, start, count - start);
            if (line.Length > RemoteFrame.MaxLineBytes) throw new IOException("Remote frame exceeded size limit.");
        }
    }

    public static async Task Run(RemoteHost host, string timeZone, Action<RemoteFrame> receive, Action<string> status,
        CancellationToken cancellationToken, IReadOnlySet<SourceKind>? enabled = null) =>
        await RunCore(() => host.StartInfo(timeZone, enabled), timeZone, receive, status, cancellationToken).ConfigureAwait(false);

    internal static async Task RunCore(Func<ProcessStartInfo> startInfo, string timeZone, Action<RemoteFrame> receive,
        Action<string> status, CancellationToken cancellationToken)
    {
        var delay = 1;
        while (!cancellationToken.IsCancellationRequested)
        {
            status("connecting");
            using var process = new Process { StartInfo = startInfo() };
            var started = false;
            Task? drain = null;
            try
            {
                process.Start();
                started = true;
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var streamToken = lifetime.Token;
                // Killing independently of ReadAsync also unblocks platforms with synchronous anonymous pipes.
                using var abort = lifetime.Token.Register(() => Stop(process));
                lifetime.CancelAfter(TimeSpan.FromSeconds(30));
                // Drain stderr without retaining potentially sensitive diagnostics or allowing an unbounded buffer.
                drain = Task.Run(async () =>
                {
                    var buffer = new char[2048];
                    while (await process.StandardError.ReadAsync(buffer, streamToken).ConfigureAwait(false) != 0) { }
                }, streamToken);
                await foreach (var line in Lines(process.StandardOutput.BaseStream, streamToken))
                {
                    var frame = RemoteFrame.Decode(line);
                    if (frame.TimeZone != timeZone) throw new IOException("Receiver time zone was not respected.");
                    if (frame.Type == "history" && !RemoteSettings.IsId(frame.Generation!))
                        throw new System.Text.Json.JsonException("History generation is missing.");
                    if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - frame.TimestampMs) > 120_000)
                        throw new IOException("Remote clock differs by more than two minutes.");
                    status("connected");
                    receive(frame);
                    lifetime.CancelAfter(TimeSpan.FromSeconds(30));
                    delay = 1;
                }
                status("disconnected");
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or ArgumentException
                or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                status(e is System.Text.Json.JsonException ? "protocolError" : "disconnected");
            }
            finally
            {
                if (drain is not null) _ = drain.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                if (started)
                {
                    Stop(process);
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                    catch (Exception e) when (e is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { }
                }
            }
            if (cancellationToken.IsCancellationRequested) break;
            try { await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            delay = Math.Min(60, delay * 2);
        }
    }

    private static void Stop(Process process)
    {
        try { process.StandardInput.Close(); if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (IOException) { }
    }
}
