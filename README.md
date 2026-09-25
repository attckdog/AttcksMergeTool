# Attck's Funscript & Video Merger

A Windows desktop app that joins a folder of videos and their funscripts into one video and
one funscript, with chapters, bookmarks and multi-axis scripts kept in sync.

Drop your scenes into a folder, arrange them in the order you want, hit **Start Merge**, and
you get a single video with a matching script that stays aligned from the first scene to
the last.

## Features

- **Video and script merged together**: every scene is re-encoded to a common format and
  joined into one video. The merged script is placed against the real encoded lengths, so
  it stays in sync no matter how many scenes there are.
- **Chapters and bookmarks**: each scene becomes a chapter in the video and a bookmark in
  the funscript, named after its file.
- **Multi-axis support**: axes embedded in the main funscript and separate
  `{scene}.{axis}.funscript` files are both merged.
- **Transition gaps**: an optional short stretch of black between scenes, just long enough
  for the device to travel from where one scene ends to where the next starts without
  going faster than a speed limit you set.
- **Merge order control**: reorder scenes by hand, sort them alphabetically, or shuffle
  them.
- **Per-video trimming**: cut the start or end off any video. Its script is trimmed to
  match.
- **Hardware encoding**: NVIDIA NVENC for AV1 or H.264, with software encoders as a
  fallback. Several scenes are encoded in parallel.
- **Metadata kept**: creators, performers, tags and notes from every source script carry
  over into the merged one.

## Requirements

- Windows 10 or 11
- [FFmpeg](https://ffmpeg.org/) (both `ffmpeg` and `ffprobe`). The easiest way to get it
  is to open a command prompt and run:

  ```
  winget install Gyan.FFmpeg
  ```

  If the app says it can't find ffmpeg afterwards, restart your PC so the new `PATH`
  takes effect, or point the app straight at `ffmpeg.exe` and `ffprobe.exe` in
  **Options**.
- An NVIDIA GPU is optional but strongly recommended. It makes encoding much faster.

## Installation

1. Download `AttcksMergeTool.exe` from the latest
   [release](https://github.com/attckdog/AttcksMergeTool/releases).
2. Put it in a folder of its own. Its `Input` folder, settings and output are created
   next to it.
3. Run it.

The release is a single self-contained executable, so you don't need to install .NET.

## How to use

1. **Add your files.** Put the videos and their funscripts into the `Input` folder next to
   the app. Use **Open Input Folder** to jump there; it's created if it doesn't exist yet.
   A video and its script must share a file name:

   ```
   Input/
     SceneOne.mp4
     SceneOne.funscript
     SceneOne.roll.funscript     <- optional extra axis
     SceneTwo.mkv
     SceneTwo.funscript
   ```

2. **Refresh.** Click **Refresh Files**. Every detected video is listed with its length
   and how many axes its scripts have.
3. **Arrange.** Set the merge order with **Move Up** / **Move Down**, **Alphabetical** or
   **Random**.
4. **Trim (optional).** Select a video, tick **Enable Trimming**, enter the start and end
   times in seconds (an end of `0` keeps the rest of the video), and click
   **Apply to Selected Video**.
5. **Name the output.** Enter an **Output Name**.
6. **Merge.** Click **Start Merge**. The log shows each step: the scripts are merged
   first, then each video is encoded, then everything is joined. Encoding is the slow
   part; longer videos take longer.
7. **Collect the results.** When it finishes, **Open Export Folder** takes you to
   `{Output Name}.mp4` and `{Output Name}.funscript`.

You can **Cancel** a run at any time; temporary files are cleaned up either way.

## Multi-axis scripts

Extra axes can be supplied in either of two ways:

- **Embedded** in the main funscript's `axes` list.
- **As separate files** named `{scene}.{axis}.funscript`, for example
  `SceneOne.twist.funscript`.

These axis names are recognised and mapped to their standard codes:

| Name  | Code |
|-------|------|
| surge | L1   |
| sway  | L2   |
| twist | R0   |
| roll  | R1   |
| pitch | R2   |

Any other axis name is passed through unchanged.

## Options

Open **Options...** to change:

| Tab | Settings |
|---|---|
| Paths & Tools | Input, temp and output folders; whether to scan subfolders of the input folder; locations of `ffmpeg` and `ffprobe` if they aren't on your `PATH` |
| Encoding | NVENC on/off, AV1 or H.264, quality and presets, target resolution (default 1920x1080), frame rate (default 60), number of parallel encodes, audio bitrate/channels/sample rate |
| Merge | Default output name, transition gaps on/off, maximum axis speed, whether to skip videos without a funscript, which file extensions count as video |
| Application | Remember window size, scan on launch, warn before overwriting an existing output, log font size |

AV1 gives smaller files. H.264 plays on more devices. Videos are letterboxed to the target
resolution rather than cropped or stretched.

Settings are saved to `settings.json` next to the app.

## Tips and troubleshooting

- **Videos without a script** are skipped by default. Turn off *Skip videos with no
  funscript* to include them as unscripted stretches.
- **Multiple scripts for one video** (other than per-axis files) need to be combined into
  one funscript before merging.
- **Keyframes past the end of a video** are dropped from the merge, and the log warns about
  it. This usually means the script has leftover points from editing.
- **Encoding errors with NVENC** usually mean the GPU or driver doesn't support the chosen
  codec. Older NVIDIA cards can't encode AV1, so switch to H.264 or turn NVENC off.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build -c Release
dotnet test AttcksMergeTool.Tests
```

To produce the self-contained single-file build used for releases, publish with the
included profile:

```
dotnet publish -p:PublishProfile=FolderProfile
```

## Feedback

Found a bug or have a request? [Open an issue](https://github.com/attckdog/AttcksMergeTool/issues).
