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

If a channel has no tagged spots, it plays straight through. Breaks cut into a program and the next segment of that program starts where the break began.

## Building a channel

On the Airtime plugin page:

- **By hand** — add time blocks (minutes from midnight, 0 to 1440). Search your library and pin movies or whole shows. A pinned show plays its episodes in season order. If you pin nothing, the block uses the genres you select.
- **Automatic** — use Morning cartoons, House sitcoms, Primetime, Night movies, or Full day. Those presets fill genre names (Animation, Comedy, Drama, Action). Change the names if your library uses different ones.
- Set how many minutes of show run between breaks, how long a break is, and which spot tags are allowed.
- Leave **Transcode the channel to H.264 + AAC** on unless every file is already the same codec.

Save. Playback reads the library again about every 45 seconds, so a save shows up without another restart.

## What playback does

Tuning a channel starts an ffmpeg process from Jellyfin's own ffmpeg. It stitches the files for the next several hours into one MPEG-TS stream, beginning at the current time of day. The same channel builds the same day every time, so two people who press play land on the same moment.
