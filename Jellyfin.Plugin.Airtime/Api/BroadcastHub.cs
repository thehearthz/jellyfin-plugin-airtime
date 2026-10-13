using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace Jellyfin.Plugin.Airtime.Api;

/// <summary>
/// One slice of a channel, already trimmed to the clock.
/// </summary>
internal readonly record struct PlayClip(string Path, double InPoint, double Length);

/// <summary>
/// One ffmpeg process per channel. Extra viewers attach to it, and it stops shortly after the last one leaves.
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
        Func<IReadOnlyList<PlayClip>> plan,
        string ffmpeg,
        bool small,
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
            broadcast.Start(plan, ffmpeg, small);
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

    private sealed class Broadcast
    {
        private readonly string _channelId;
        private readonly object _gate = new();
        private readonly List<System.Threading.Channels.Channel<byte[]>> _subscribers = new();
        private readonly CancellationTokenSource _shutdown = new();
        private CancellationTokenSource? _idle;
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
                    return _state != 2;
                }
            }
        }

        public ChannelReader<byte[]> Subscribe()
        {
            var channel = System.Threading.Channels.Channel.CreateBounded<byte[]>(new BoundedChannelOptions(48)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });
            lock (_gate)
            {
                _idle?.Cancel();
                _idle = null;
                _subscribers.Add(channel);
            }

            return channel.Reader;
        }

        public void Start(Func<IReadOnlyList<PlayClip>> plan, string ffmpeg, bool small)
        {
            lock (_gate)
            {
                if (_subscribers.Count == 0)
                {
                    Die();
                    Remove();
                    return;
                }

                _state = 1;
            }

            _ = Task.Run(() => Run(plan, ffmpeg, small));
        }

        public void Unsubscribe(ChannelReader<byte[]> reader)
        {
            CancellationToken idle;
            lock (_gate)
            {
                _subscribers.RemoveAll(channel => channel.Reader == reader);
                if (_subscribers.Count > 0 || _state == 2)
                {
                    return;
                }

                _idle?.Cancel();
                _idle = new CancellationTokenSource();
                idle = _idle.Token;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), idle).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                lock (_gate)
                {
                    if (_subscribers.Count > 0 || _state == 2)
                    {
                        return;
                    }
                }

                _shutdown.Cancel();
            });
        }

        private async Task Run(Func<IReadOnlyList<PlayClip>> plan, string ffmpeg, bool small)
        {
            try
            {
                var offset = 0d;
                var misses = 0;
                while (!_shutdown.IsCancellationRequested)
                {
                    IReadOnlyList<PlayClip> clips;
                    try
                    {
                        clips = plan();
                    }
                    catch (Exception)
                    {
                        break;
                    }

                    if (clips.Count == 0)
                    {
                        break;
                    }

                    foreach (var clip in clips)
                    {
                        if (_shutdown.IsCancellationRequested)
                        {
                            return;
                        }

                        var produced = await RunClip(ffmpeg, clip, offset, small, _shutdown.Token).ConfigureAwait(false);
                        if (produced)
                        {
                            offset += clip.Length;
                            misses = 0;
                        }
                        else if (++misses >= 3)
                        {
                            return;
                        }
                    }
                }
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

                Remove();
            }
        }

        private async Task<bool> RunClip(string ffmpeg, PlayClip clip, double offset, bool small, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(clip.Path) || clip.Length < 1)
            {
                return false;
            }

            var remote = clip.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || clip.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (!remote && !File.Exists(clip.Path))
            {
                return false;
            }

            using var process = Start(ffmpeg, Arguments(clip, offset, small, remote));
            var stderr = Drain(process);
            var buffer = new byte[32 * 1024];
            var produced = false;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    using var first = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    if (!produced)
                    {
                        first.CancelAfter(TimeSpan.FromSeconds(20));
                    }

                    int read;
                    try
                    {
                        read = await process.StandardOutput.BaseStream.ReadAsync(buffer, first.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    produced = true;
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
            catch (OperationCanceledException)
            {
                // The channel is stopping.
            }
            finally
            {
                Stop(process);
                try
                {
                    await stderr.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Already stopping.
                }
            }

            return produced;
        }

        private static List<string> Arguments(PlayClip clip, double offset, bool small, bool remote)
        {
            var height = small ? 480 : 720;
            var rate = small ? "700k" : "1400k";
            var cap = small ? "900k" : "1600k";
            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin",
            };
            if (clip.InPoint >= 0.25)
            {
                args.Add("-ss");
                args.Add(clip.InPoint.ToString("0.###", CultureInfo.InvariantCulture));
            }

            // A short burst, then realtime, so the player gets a picture without reading the whole file.
            args.Add("-readrate");
            args.Add("1");
            args.Add("-readrate_initial_burst");
            args.Add("4");
            if (remote)
            {
                args.Add("-user_agent");
                args.Add("Jellyfin-Airtime");
                args.Add("-reconnect");
                args.Add("1");
                args.Add("-reconnect_streamed");
                args.Add("1");
                args.Add("-reconnect_delay_max");
                args.Add("2");
            }

            args.Add("-i");
            args.Add(clip.Path);
            args.Add("-t");
            args.Add(clip.Length.ToString("0.###", CultureInfo.InvariantCulture));
            args.Add("-map");
            args.Add("0:v:0");
            args.Add("-map");
            args.Add("0:a:0?");
            args.Add("-vf");
            args.Add("fps=24,scale=-2:min(" + height.ToString(CultureInfo.InvariantCulture) + "\\,ih)");
            args.Add("-c:v");
            args.Add("libx264");
            args.Add("-preset");
            args.Add("ultrafast");
            args.Add("-tune");
            args.Add("zerolatency");
            args.Add("-pix_fmt");
            args.Add("yuv420p");
            args.Add("-profile:v");
            args.Add("baseline");
            args.Add("-level");
            args.Add("3.1");
            args.Add("-bf");
            args.Add("0");
            args.Add("-g");
            args.Add("24");
            args.Add("-keyint_min");
            args.Add("24");
            args.Add("-sc_threshold");
            args.Add("0");
            args.Add("-b:v");
            args.Add(rate);
            args.Add("-maxrate");
            args.Add(cap);
            args.Add("-bufsize");
            args.Add("800k");
            args.Add("-threads");
            args.Add("1");
            args.Add("-filter_threads");
            args.Add("1");
            args.Add("-c:a");
            args.Add("aac");
            args.Add("-ac");
            args.Add("2");
            args.Add("-ar");
            args.Add("44100");
            args.Add("-b:a");
            args.Add("96k");
            args.Add("-muxdelay");
            args.Add("0");
            args.Add("-muxpreload");
            args.Add("0");
            args.Add("-output_ts_offset");
            args.Add(Math.Max(0, offset).ToString("0.###", CultureInfo.InvariantCulture));
            args.Add("-max_muxing_queue_size");
            args.Add("1024");
            args.Add("-f");
            args.Add("mpegts");
            args.Add("-mpegts_flags");
            args.Add("+resend_headers");
            args.Add("-pat_period");
            args.Add("0.3");
            args.Add("pipe:1");
            return args;
        }

        private static Process Start(string ffmpeg, IReadOnlyList<string> args)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            foreach (var arg in args)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }

            process.Start();
            return process;
        }

        private static Task Drain(Process process)
        {
            return Task.Run(async () =>
            {
                try
                {
                    while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null)
                    {
                        // Discard. A full stderr pipe would stall the picture.
                    }
                }
                catch (Exception)
                {
                    // The process is already going away.
                }
            });
        }

        private static void Stop(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // Already gone.
            }
        }

        private void Die()
        {
            _state = 2;
            _idle?.Cancel();
        }

        private void Remove()
        {
            lock (Gate)
            {
                if (Live.TryGetValue(_channelId, out var current) && ReferenceEquals(current, this))
                {
                    Live.Remove(_channelId);
                }
            }
        }
    }
}
