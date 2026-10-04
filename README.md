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
- **Injected voice audio**: mix random voice clips from your own audio packs over any
  video's audio, with separate volumes for the voices and the original sound.
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
5. **Inject audio (optional).** Select a video, click **Choose Folders...** in the
   **Injected Audio** group, tick the voice folders to draw from, set the volumes and the
   gap between clips, and click **Apply to Selected** or **Apply to All**. See
   [Injected audio](#injected-audio).
6. **Name the output.** Enter an **Output Name**.
7. **Merge.** Click **Start Merge**. The log shows each step: the scripts are merged
   first, then each video is encoded, then everything is joined. Encoding is the slow
   part; longer videos take longer.
8. **Collect the results.** When it finishes, **Open Export Folder** takes you to
   `{Output Name}.mp4` and `{Output Name}.funscript`.

You can **Cancel** a run at any time; temporary files are cleaned up either way.

## Injected audio

The app doesn't ship with any voice clips. Download a voice pack and put it in the `Audio`
folder next to the app.

### Getting the OpenNSFW voice pack

The [OpenNSFW Voice Pack](https://opennsfw.carrd.co/) is free, including for commercial work, as
long as you credit the pack and its performers (see [Credits](#credits)).

1. Download the pack from [opennsfw.carrd.co](https://opennsfw.carrd.co/). For questions, ask on
   the [OpenNSFW Discord](https://discord.gg/K53FpG4CBF) or message
   [@OpenNSFWSP on X](https://x.com/OpenNSFWSP).
2. Extract it so the `OpenNSFW VA` folder sits inside `Audio`, next to the app:

   ```
   AttcksMergeTool.exe
   Audio/
     OpenNSFW VA/
       Female/
         728Kaya [Kaya]/...
       Male/...
       README - OpenNSFW Voice Pack Terms.pdf
   ```

3. Read `README - OpenNSFW Voice Pack Terms.pdf`. You agree to its terms by using the clips.
4. Open the app. The library is scanned on launch. If the app was already open, click
   **Rescan** in the folder picker.

Other packs work the same way: give each pack its own folder under `Audio` and keep its folder
layout. The app indexes every `.mp3`, `.wav`, `.ogg`, `.flac`, `.m4a`, `.opus`, `.aac` and
`.wma` file in that folder and below it, and ignores everything else (PDFs, shortcuts,
readmes).

### Sorting a pack by sound type

The pack is organized by performer, and every performer names their folders differently. To
pick clips by type instead, for example "high-intensity female moans" across every performer,
sort it:

1. Open **Options... > Audio Sorting**. The pack and destination are filled in for you:
   `Audio\OpenNSFW VA` and `Audio\Sorted`.
2. Click **Preview** to see how many clips go in each folder. Nothing is written.
3. Click **Sort Pack** and confirm.
4. Click **Rescan** in the folder picker, then choose folders under `Sorted`.

The sorted folder is laid out as `Voice/Category/Intensity`:

```
Audio/
  Sorted/
    Female/
      Moaning/
        1-Low/
        2-Medium/
        3-High/
        4-Extreme/
      Muffled Moaning/...
      Breathing/...
      Orgasm/...
      Oral/...
      Dialogue/...
    Male/...
    Femboy/...
    Creature/
      Orc/...
      Werewolf/...
```

- **Voices**: Female, Male, Femboy, and Creature (split into Orc, Troll, Werewolf, Goblin,
  Monster and Alien).
- **Categories**: Moaning, Muffled Moaning (closed mouth, gagged, clenched teeth), Breathing,
  Orgasm, Post-Orgasm, Oral, Kissing, Dialogue, Laughing, Pain & Struggle, Growls & Roars,
  Sound Effects, Long Loops and Misc.
- **Intensity** comes from words like "high intensity", "soft", "rough" or "fast". Pitch
  ("High Pitch", "Deep voice") describes the voice, so it isn't counted as intensity. Clips
  with no intensity clue sit directly in their category folder. Choosing a category folder
  includes every level below it.
- Each clip keeps its performer in its name, such as `728Kaya [Kaya] - open mouth high 1.mp3`,
  so it can always be credited.

The pack itself isn't changed. The sorted folder holds hard links to the original clips, so it
takes no extra disk space. If the destination is on another drive, the clips are copied
instead. Raw takes that also come in a processed version, and exact duplicates, are left out.
Sorting again only adds clips that aren't there yet. `_manifest.csv` in the sorted folder
records where every clip went and why any were skipped.

The rules are tuned for the OpenNSFW pack. Other packs still sort, but expect more clips in
Misc or without an intensity.

### Mixing clips into a video

For each video you choose one or more folders. A folder includes everything below it, so
you can pick a whole pack, one voice actor, or one category. During the merge, clips are
drawn at random from the chosen folders and placed one after another with a random gap
between them, until the video (after trimming) runs out of room:

- **Voice %**: how loud the clips play (100 = their own level).
- **Original %**: how loud the video's own audio plays underneath. Lower it to let the
  voices stand out.
- **Gap min / max (s)**: the range the silence between two clips is picked from.

Only whole clips are placed, so none are cut off at the end of a scene. A clip doesn't
repeat until every clip in the chosen folders has played once. The log lists every clip
that was used and when it starts. Videos with no folders chosen keep their audio exactly as
it is.

Clip lengths are measured the first time a clip is picked and saved in `audio-index.json`
next to the app, so later runs don't measure them again. The library is scanned on launch.
After adding packs, use **Rescan** in the folder picker. The library folder can be changed
under **Options... > Paths & Tools**.

Per-video audio choices, like trims, last until the app is closed.

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
| Paths & Tools | Input, temp, output and audio library folders; whether to scan subfolders of the input folder; locations of `ffmpeg` and `ffprobe` if they aren't on your `PATH` |
| Encoding | NVENC on/off, AV1 or H.264, quality and presets, target resolution (default 1920x1080), frame rate (default 60), number of parallel encodes, audio bitrate/channels/sample rate |
| Merge | Default output name, transition gaps on/off, maximum axis speed, whether to skip videos without a funscript, which file extensions count as video |
| Application | Remember window size, scan on launch, warn before overwriting an existing output, log font size |
| Audio Sorting | Sort a voice pack into Voice / Category / Intensity folders (a tool, not a setting) |
| Credits & License | Version, credits (including every voice pack performer), and the full license text |

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

## Credits

Voice clips come from the [OpenNSFW Voice Pack](https://opennsfw.carrd.co/), used under
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). [CREDITS.md](CREDITS.md) lists every
performer, along with the pack's attribution rules. If you publish anything made with these
clips, credit the performers as **VA Pack** with their X handles, for example
`[Character Moans]: @chiyo1000nights OpenNSFW VoicePack`. Don't use the clips to train AI or
imitate a performer's voice.

## License

Copyright (C) 2025 attckdog. Licensed under the
[GNU General Public License v3.0](LICENSE) or (at your option) any later version.

FFmpeg is not bundled. It is installed separately and licensed by the FFmpeg developers.
