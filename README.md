# Airtime for Jellyfin 12.1

Airtime builds constant channels from movies and shows already in your Jellyfin library. A channel stays on the server clock, so everyone who tunes in is at the same moment. Commercials pause a program and return to the same point.

This build targets Jellyfin **12.1** (`targetAbi` 12.1.0.0). It will not load on Jellyfin 12.0.

## Install from a repository

This is the path Jellyfin uses to install and update the plugin.

1. Dashboard → Plugins → Repositories.
2. Add a repository named **Airtime**.
3. Repository URL:

   `https://raw.githubusercontent.com/thehearthz/jellyfin-plugin-airtime/main/manifest.json`

4. Save, open the catalog, and install Airtime. Restart Jellyfin when it asks.

The catalog entry is for Jellyfin 12.1. A 12.0 server will not offer it.

## Install by hand

1. Confirm the server version under Dashboard → General. It should be 12.1.x.
2. Stop Jellyfin, or just restart it after the copy. If you installed the 12.0 build, delete that Airtime folder first.
3. Unzip this file. You should see a folder named `Airtime` containing:
   - `Jellyfin.Plugin.Airtime.dll`
   - `meta.json`
4. Copy that `Airtime` folder into the server's plugins directory:
   - Docker: the config volume, `plugins/Airtime`
   - Linux package: `/var/lib/jellyfin/plugins/Airtime`
   - Windows: `%ProgramData%\Jellyfin\Server\plugins\Airtime` or `%LOCALAPPDATA%\jellyfin\plugins\Airtime`
5. Start Jellyfin.
6. Dashboard → Plugins. Airtime should be listed. Open it.

The channel is not a Live TV tuner and it does not use HDHomeRun. After you save a lineup, open the home page, go to **Channels**, open **Airtime**, and play a numbered channel.

## Commercials

Tag short videos in your library, then tick those types on the channel:

| Type | Tag |
| --- | --- |
| Network | `airtime-network` |
| Local | `airtime-local` |
| PSA | `airtime-psa` |
| Promo | `airtime-promo` |

Breaks play the commercials you add on the plugin page. Pick videos already in the library, or paste a direct file address. Tagged videos are used only when that list is empty.

If a channel has no commercials, it plays straight through. Breaks cut into a program and the next segment of that program starts where the break began.

## Building a channel

On the Airtime plugin page:

- **By hand** — add time blocks with a 24-hour start and end, such as 06:00 and 18:30. Add or remove genres. Search your library and pin movies or whole shows, and remove any you do not want. A pinned show plays its episodes in season order. If you remove every title, the block uses the genres.
- **Build from my library** — Airtime looks at what you own and fills the day: night movies, morning, daytime, afternoon, primetime, and a late movie. Each block keeps a genre. After you save, that channel plays different shows and movies on each calendar day. Everyone watching on the same day still sees the same lineup. A channel you build by hand stays on the titles you pinned.
- Add commercials from the library or from an online file address. Set how many minutes of show run between breaks, and how long a break is.
- Leave **Transcode the channel to H.264 + AAC** off unless the files do not already match. Off means ffmpeg only copies, which uses almost no CPU. On is one shared 480p, 24fps encode on a single thread, and it stops when the last person leaves.

Save. The lineup is kept in memory and the library is not scanned again until you save, or twelve hours pass.

## What playback does

The first person to tune a channel starts one ffmpeg process from Jellyfin's own ffmpeg. Anyone who joins that channel attaches to the same process instead of starting another encode. It reads each file only as fast as the picture plays, and it does not write a playlist to disk.
