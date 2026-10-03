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
            var hits = new LibraryCatalog(library).Search(context.Request.Query["q"]);
            await WriteJson(context, hits.Select(hit => new
            {
                id = hit.Id,
                name = hit.Name,
                kind = hit.IsSeries ? "Series" : hit.Kind,
                year = hit.Year,
            })).ConfigureAwait(false);
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

        var transcode = Plugin.Instance?.Configuration.Transcode ?? true;
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
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = BroadcastHub.EncodeArguments(transcode),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.Start();
        try
        {
            process.StandardInput.Write(concat);
            process.StandardInput.Close();
        }
        catch
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

            process.Dispose();
            throw;
        }

        return Task.FromResult(new RunningEncode { Process = process, ListPath = string.Empty });
    }

    private static string ConcatScript(IReadOnlyList<(string Path, double InPoint, double Length, string Title)> plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine("ffconcat version 1.0");
        foreach (var item in plan)
        {
            builder.Append("file '").Append(item.Path.Replace("'", "'\\''", StringComparison.Ordinal)).AppendLine("'");
            builder.Append("inpoint ").AppendLine(item.InPoint.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append("outpoint ").AppendLine((item.InPoint + item.Length).ToString("0.###", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static Task WriteJson(HttpContext context, object value)
    {
        context.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(context.Response.Body, value, Json);
    }
}
