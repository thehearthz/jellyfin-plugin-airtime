using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Airtime.Channel;
using Jellyfin.Plugin.Airtime.Scheduling;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Airtime.Api;

/// <summary>
/// Serves Airtime over a small middleware instead of an MVC controller.
/// A controller is part of endpoint startup, and a failure there stops the whole server.
/// </summary>
public sealed class AirtimeStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (context, nextDelegate) =>
            {
                if (!IsAirtime(context.Request.Path))
                {
                    await nextDelegate().ConfigureAwait(false);
                    return;
                }

                try
                {
                    await AirtimeHttp.Handle(context).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    if (!context.Response.HasStarted)
                    {
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    }
                }
            });

            next(app);
        };
    }

    private static bool IsAirtime(PathString path)
    {
        var parts = (path.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => string.Equals(part, "Airtime", StringComparison.OrdinalIgnoreCase));
    }
}

internal static class AirtimeHttp
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task Handle(HttpContext context)
    {
        var parts = (context.Request.Path.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var at = Array.FindIndex(parts, part => string.Equals(part, "Airtime", StringComparison.OrdinalIgnoreCase));
        var action = at >= 0 && at + 1 < parts.Length ? parts[at + 1] : string.Empty;
        var tail = at >= 0 && at + 2 < parts.Length ? parts[at + 2] : string.Empty;

        if (string.Equals(action, "Tune", StringComparison.OrdinalIgnoreCase))
        {
            await Tune(context, tail).ConfigureAwait(false);
            return;
        }

        var auth = await context.AuthenticateAsync().ConfigureAwait(false);
        if (!auth.Succeeded)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (string.Equals(action, "Genres", StringComparison.OrdinalIgnoreCase))
        {
            var library = context.RequestServices.GetRequiredService<ILibraryManager>();
            await WriteJson(context, new LibraryCatalog(library).Genres()).ConfigureAwait(false);
            return;
        }

        if (string.Equals(action, "Library", StringComparison.OrdinalIgnoreCase))
        {
            var library = context.RequestServices.GetRequiredService<ILibraryManager>();
            var spots = string.Equals(context.Request.Query["kind"], "spot", StringComparison.OrdinalIgnoreCase);
            var hits = new LibraryCatalog(library).Search(context.Request.Query["q"], spots);
            await WriteJson(context, hits.Select(HitJson)).ConfigureAwait(false);
            return;
        }

        if (string.Equals(action, "Lookup", StringComparison.OrdinalIgnoreCase))
        {
            var library = context.RequestServices.GetRequiredService<ILibraryManager>();
            var hits = new LibraryCatalog(library).Lookup(LibraryCatalog.Split(context.Request.Query["ids"]));
            await WriteJson(context, hits.Select(HitJson)).ConfigureAwait(false);
            return;
        }

        if (string.Equals(action, "Tagged", StringComparison.OrdinalIgnoreCase))
        {
            var library = context.RequestServices.GetRequiredService<ILibraryManager>();
            var hits = new LibraryCatalog(library).TaggedSpots();
            await WriteJson(context, hits.Select(HitJson)).ConfigureAwait(false);
            return;
        }

        if (string.Equals(action, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            var library = context.RequestServices.GetRequiredService<ILibraryManager>();
            await WriteJson(context, new LibraryCatalog(library).Suggest()).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static async Task Tune(HttpContext context, string channelId)
    {
        var expected = AirtimeChannel.StreamKey();
        var key = context.Request.Query["key"].ToString();
        if (string.IsNullOrEmpty(expected) || !string.Equals(expected, key, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var options = Plugin.Instance?.Configuration.Channels.FirstOrDefault(channel =>
            string.Equals(channel.Id, channelId, StringComparison.OrdinalIgnoreCase));
        if (options is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("That channel is not on the desk.").ConfigureAwait(false);
            return;
        }

        var library = context.RequestServices.GetRequiredService<ILibraryManager>();
        if (!BroadcastHub.IsLive(options.Id))
        {
            var slots = LibraryCatalog.Lineup(library, options);
            if (ScheduleBuilder.Playback(slots, AirtimeChannel.SecondsNow(), 8 * 60 * 60).Count == 0)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync("Nothing in this lineup has a playable file. Pick shows, or tag spots.").ConfigureAwait(false);
                return;
            }
        }

        var ffmpeg = context.RequestServices.GetRequiredService<IMediaEncoder>().EncoderPath;
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("Jellyfin's ffmpeg was not found.").ConfigureAwait(false);
            return;
        }

        var transcode = Plugin.Instance?.Configuration.Transcode ?? false;
        context.Response.ContentType = "video/mp2t";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

        await BroadcastHub.Play(
            options.Id,
            _ =>
            {
                var slots = LibraryCatalog.Lineup(library, options);
                var plan = ScheduleBuilder.Playback(slots, AirtimeChannel.SecondsNow(), 8 * 60 * 60);
                if (plan.Count == 0)
                {
                    throw new InvalidOperationException("Nothing in this lineup has a playable file.");
                }

                return StartEncode(ffmpeg, ConcatScript(plan), transcode);
            },
            context.Response.Body,
            context.RequestAborted).ConfigureAwait(false);
    }

    private static Task<RunningEncode> StartEncode(string ffmpeg, string concat, bool transcode)
    {
        var path = Path.Combine(Path.GetTempPath(), "airtime-" + Guid.NewGuid().ToString("n") + ".ffconcat");
        File.WriteAllText(path, concat);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = BroadcastHub.EncodeArguments(path, transcode),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        try
        {
            process.Start();
        }
        catch
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // The next boot can remove a leftover list.
            }

            process.Dispose();
            throw;
        }

        return Task.FromResult(new RunningEncode { Process = process, ListPath = path });
    }

    internal static string WriteConcatFile(string channelId, IReadOnlyList<(string Path, double InPoint, double Length, string Title)> plan)
    {
        var cache = Plugin.Instance?.Paths.CachePath;
        if (string.IsNullOrWhiteSpace(cache))
        {
            cache = Path.GetTempPath();
        }

        var dir = Path.Combine(cache, "concat");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, channelId + ".concat");
        File.WriteAllText(path, ConcatScript(plan));
        return path;
    }

    internal static string ConcatScript(IReadOnlyList<(string Path, double InPoint, double Length, string Title)> plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine("ffconcat version 1.0");
        foreach (var item in plan)
        {
            builder.Append("file '").Append(ConcatPath(item.Path)).AppendLine("'");
            builder.Append("inpoint ").AppendLine(item.InPoint.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append("outpoint ").AppendLine((item.InPoint + item.Length).ToString("0.###", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string ConcatPath(string path)
    {
        var text = path.Replace('\\', '/').Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
        return text.Replace("'", "'\\''", StringComparison.Ordinal);
    }

    private static object HitJson(LibraryHit hit)
    {
        return new
        {
            id = hit.Id,
            name = hit.Name,
            kind = hit.IsSeries ? "Series" : hit.Kind,
            year = hit.Year,
        };
    }

    private static Task WriteJson(HttpContext context, object value)
    {
        context.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(context.Response.Body, value, Json);
    }
}
