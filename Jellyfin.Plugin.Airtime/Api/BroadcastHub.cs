using System.Diagnostics;
using System.Threading.Channels;

namespace Jellyfin.Plugin.Airtime.Api;

/// <summary>
/// One ffmpeg process per channel. Extra viewers attach to it, and it stops when the last one leaves.
/// </summary>
internal static class BroadcastHub
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Broadcast> Live = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsLive(string channelId)
    {
        lock (Gate)
        {
            return Live.TryGetValue(channelId, out var broadcast) && broadcast.IsAlive;
        }
    }

    public static async Task Play(
        string channelId,
        Func<CancellationToken, Task<RunningEncode>> start,
        Stream output,
        CancellationToken cancellationToken)
    {
        Broadcast broadcast;
        ChannelReader<byte[]> reader;
        var created = false;
        lock (Gate)
        {
            if (!Live.TryGetValue(channelId, out broadcast!) || !broadcast.IsAlive)
            {
                broadcast = new Broadcast(channelId);
                Live[channelId] = broadcast;
                created = true;
            }

            reader = broadcast.Subscribe();
        }

        if (created)
        {
            try
            {
                await broadcast.Start(start, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                broadcast.Fail();
                throw;
            }
        }

        try
        {
            await foreach (var chunk in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await output.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The player stopped.
        }
        finally
        {
            broadcast.Unsubscribe(reader);
        }
    }

    internal static string EncodeArguments(string listPath, bool transcode)
    {
        // 480p, 24fps, one thread. A realtime encode then stays near idle.
        var codec = transcode
            ? "-vf fps=24,scale=-2:min(480\\,ih) -c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p -g 48 -b:v 700k -maxrate 800k -bufsize 800k -threads 1 -filter_threads 1 -c:a aac -ac 2 -ar 44100 -b:a 64k"
            : "-c copy";
        var quoted = "\"" + listPath.Replace("\"", string.Empty, StringComparison.Ordinal) + "\"";
        // The concat list has to be a real file. A pipe makes ffmpeg open every episode as pipe:/path and exit.
        return $"-hide_banner -loglevel error -probesize 1048576 -analyzeduration 2000000 -readrate 1 -readrate_initial_burst 3 -protocol_whitelist file,crypto,http,https,tcp,tls,pipe,fd -f concat -safe 0 -i {quoted} -map 0:v:0? -map 0:a:0? {codec} -avoid_negative_ts make_zero -max_muxing_queue_size 1024 -f mpegts -mpegts_flags +resend_headers -pat_period 0.3 -muxdelay 0 -muxpreload 0 pipe:1";
    }

    private sealed class Broadcast
    {
        private readonly string _channelId;
        private readonly object _gate = new();
        private readonly List<System.Threading.Channels.Channel<byte[]>> _subscribers = new();
        private Process? _process;
        private string? _listPath;
        private int _state; // 0 starting, 1 running, 2 dead

        public Broadcast(string channelId)
        {
            _channelId = channelId;
        }

        public bool IsAlive
        {
            get
            {
                lock (_gate)
                {
                    return _state != 2 && (_process is null || !_process.HasExited);
                }
            }
        }

        public ChannelReader<byte[]> Subscribe()
        {
            var channel = System.Threading.Channels.Channel.CreateBounded<byte[]>(new BoundedChannelOptions(24)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });
            lock (_gate)
            {
                _subscribers.Add(channel);
            }

            return channel.Reader;
        }

        public void Fail()
        {
            List<System.Threading.Channels.Channel<byte[]>> targets;
            lock (_gate)
            {
                targets = _subscribers.ToList();
                _subscribers.Clear();
                Die();
            }

            foreach (var target in targets)
            {
                target.Writer.TryComplete();
            }

            lock (Gate)
            {
                if (Live.TryGetValue(_channelId, out var current) && ReferenceEquals(current, this))
                {
                    Live.Remove(_channelId);
                }
            }
        }

        public async Task Start(Func<CancellationToken, Task<RunningEncode>> start, CancellationToken cancellationToken)
        {
            var encode = await start(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_subscribers.Count == 0)
                {
                    encode.Dispose();
                    Die();
                    return;
                }

                _process = encode.Process;
                _listPath = encode.ListPath;
                _state = 1;
            }

            _ = Task.Run(() => Pump(encode.Process, encode.ListPath));
        }

        public void Unsubscribe(ChannelReader<byte[]> reader)
        {
            Process? process = null;
            string? listPath = null;
            var stop = false;
            lock (_gate)
            {
                _subscribers.RemoveAll(channel => channel.Reader == reader);
                if (_subscribers.Count == 0)
                {
                    stop = true;
                    process = _process;
                    listPath = _listPath;
                    Die();
                }
            }

            if (!stop)
            {
                return;
            }

            StopProcess(process, listPath);
            lock (Gate)
            {
                if (Live.TryGetValue(_channelId, out var current) && ReferenceEquals(current, this))
                {
                    Live.Remove(_channelId);
                }
            }
        }

        private async Task Pump(Process process, string listPath)
        {
            var stderr = Task.Run(async () =>
            {
                try
                {
                    while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null)
                    {
                        // Discard. A quiet ffmpeg is the point.
                    }
                }
                catch (Exception)
                {
                    // The process is already going away.
                }
            });

            var buffer = new byte[32 * 1024];
            try
            {
                while (true)
                {
                    var read = await process.StandardOutput.BaseStream.ReadAsync(buffer).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    var copy = new byte[read];
                    Buffer.BlockCopy(buffer, 0, copy, 0, read);
                    List<System.Threading.Channels.Channel<byte[]>> targets;
                    lock (_gate)
                    {
                        targets = _subscribers.ToList();
                    }

                    foreach (var target in targets)
                    {
                        target.Writer.TryWrite(copy);
                    }
                }
            }
            catch (Exception)
            {
                // The viewer hung up, or ffmpeg was stopped.
            }
            finally
            {
                List<System.Threading.Channels.Channel<byte[]>> targets;
                lock (_gate)
                {
                    targets = _subscribers.ToList();
                    _subscribers.Clear();
                    Die();
                }

                foreach (var target in targets)
                {
                    target.Writer.TryComplete();
                }

                StopProcess(process, listPath);
                lock (Gate)
                {
                    if (Live.TryGetValue(_channelId, out var current) && ReferenceEquals(current, this))
                    {
                        Live.Remove(_channelId);
                    }
                }

                try
                {
                    await stderr.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Already stopping.
                }
            }
        }

        private void Die()
        {
            _state = 2;
        }

        private static void StopProcess(Process? process, string? listPath)
        {
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // Already gone.
            }

            try
            {
                process?.Dispose();
            }
            catch (Exception)
            {
                // Already gone.
            }

            if (string.IsNullOrEmpty(listPath))
            {
                return;
            }

            try
            {
                File.Delete(listPath);
            }
            catch (IOException)
            {
                // Temp cleanup can take the file later.
            }
        }
    }
}

internal sealed class RunningEncode : IDisposable
{
    public required Process Process { get; init; }

    public required string ListPath { get; init; }

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Already gone.
        }

        Process.Dispose();
        if (string.IsNullOrEmpty(ListPath))
        {
            return;
        }

        try
        {
            File.Delete(ListPath);
        }
        catch (IOException)
        {
            // Temp cleanup can take the file later.
        }
    }
}
