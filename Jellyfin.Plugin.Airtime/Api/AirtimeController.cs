using System.Diagnostics;
using System.Globalization;
using System.Text;
using Jellyfin.Plugin.Airtime.Channel;
using Jellyfin.Plugin.Airtime.Scheduling;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Airtime.Api;

/// <summary>
/// Library search for the dashboard page, and the live tune stream.
/// </summary>
[ApiController]
[Authorize]
[Route("Airtime")]
public sealed class AirtimeController : ControllerBase
{
    private readonly ILibraryManager _library;
    private readonly IMediaEncoder _encoder;
    private readonly ILogger<AirtimeController> _logger;

    public AirtimeController(ILibraryManager library, IMediaEncoder encoder, ILogger<AirtimeController> logger)
    {
        _library = library;
        _encoder = encoder;
        _logger = logger;
    }

    [HttpGet("Genres")]
    public ActionResult<IReadOnlyList<string>> Genres()
    {
        return Ok(new LibraryCatalog(_library).Genres());
    }

    [HttpGet("Library")]
    public ActionResult Library([FromQuery] string? q)
    {
        var hits = new LibraryCatalog(_library).Search(q);
        return Ok(hits.Select(hit => new
        {
            id = hit.Id,
            name = hit.Name,
            kind = hit.IsSeries ? "Series" : hit.Kind,
            year = hit.Year,
        }));
    }

    [AllowAnonymous]
    [HttpGet("Tune/{channelId}")]
    public async Task Tune(string channelId, [FromQuery] string? key, CancellationToken cancellationToken)
    {
        var expected = AirtimeChannel.StreamKey();
        if (string.IsNullOrEmpty(expected) || !string.Equals(expected, key, StringComparison.Ordinal))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var options = Plugin.Instance?.Configuration.Channels.FirstOrDefault(channel =>
            string.Equals(channel.Id, channelId, StringComparison.OrdinalIgnoreCase));
        if (options is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            await Response.WriteAsync("That channel is not on the desk.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var slots = LibraryCatalog.Lineup(_library, options);
        var plan = ScheduleBuilder.Playback(slots, AirtimeChannel.SecondsNow(), 8 * 60 * 60);
        if (plan.Count == 0)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            await Response.WriteAsync("Nothing in this lineup has a playable file. Pick shows, or tag spots.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var ffmpeg = _encoder.EncoderPath;
        if (string.IsNullOrWhiteSpace(ffmpeg) || !System.IO.File.Exists(ffmpeg))
        {
            Response.StatusCode = StatusCodes.Status500InternalServerError;
            await Response.WriteAsync("Jellyfin's ffmpeg was not found.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var listPath = Path.Combine(Path.GetTempPath(), "airtime-" + Guid.NewGuid().ToString("n") + ".ffconcat");
        await System.IO.File.WriteAllTextAsync(listPath, ConcatScript(plan), cancellationToken).ConfigureAwait(false);

        var transcode = Plugin.Instance?.Configuration.Transcode ?? true;
        var codec = transcode
            ? "-c:v libx264 -preset veryfast -tune zerolatency -pix_fmt yuv420p -g 60 -c:a aac -ac 2 -ar 48000 -b:a 160k"
            : "-c copy";
        var args = $"-hide_banner -loglevel error -nostdin -re -f concat -safe 0 -i \"{listPath}\" -map 0:v:0? -map 0:a:0? {codec} -f mpegts -muxdelay 0 -muxpreload 0 pipe:1";

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        Response.ContentType = "video/mp2t";
        Response.Headers.CacheControl = "no-store";
        Response.StatusCode = StatusCodes.Status200OK;
        HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

        process.Start();
        var stderr = new StringBuilder();
        var pump = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (stderr.Length < 4000)
                {
                    stderr.AppendLine(line);
                }
            }
        });

        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(Response.Body, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The player stopped.
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Airtime could not stop ffmpeg");
            }

            process.Dispose();
            try
            {
                System.IO.File.Delete(listPath);
            }
            catch (IOException)
            {
                // The list file can wait for the next temp cleanup.
            }

            await pump.ConfigureAwait(false);
            if (stderr.Length > 0)
            {
                _logger.LogInformation("Airtime ffmpeg: {Error}", stderr.ToString());
            }
        }
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
}
