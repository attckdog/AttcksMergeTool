using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using AttcksMergeTool.Models;

namespace AttcksMergeTool.Tests.Support;

/// <summary>
/// A scratch folder that plays the part of the Input folder, plus the helpers for seeding it.
/// </summary>
/// <remarks>
/// Real files on disk rather than an abstracted filesystem: the scanning and classification
/// under test is all about how files are named next to each other, so faking the filesystem
/// would fake away the thing being tested.
/// </remarks>
internal sealed class TempWorkspace : IDisposable
{
    private static readonly JsonSerializerOptions ScriptOptions = new() {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public TempWorkspace() {
        Root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "AttcksMergeTool.Tests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Path(string relativeName) => System.IO.Path.Combine(Root, relativeName);

    /// <summary>
    /// Options pointing every configurable path, the outputs included, at this workspace, so
    /// nothing a test writes escapes <see cref="Root"/> or collides with another test.
    /// </summary>
    /// <param name="ffmpegPath">
    /// Only worth passing when the test is about which executable gets launched; the fake
    /// runner never starts anything either way.
    /// </param>
    /// <param name="skipUnscriptedVideos">
    /// Leaves a video with no funscript out of the merge. Off unless a test is about that,
    /// matching <see cref="MergeOptions.SkipVideosWithoutScripts"/>.
    /// </param>
    public MergeOptions Options(
        string outputName, string? ffmpegPath = null, bool skipUnscriptedVideos = false) => new() {
        OutputName = outputName,
        InputFolder = Root,
        OutputFolder = Root,
        TempFolder = Path("TempTS"),
        ConcatListFile = Path("filelist.txt"),
        ChapterMetadataFile = Path("ffmetadata.txt"),
        FfmpegPath = ffmpegPath ?? AppSettings.DefaultFfmpegPath,
        SkipVideosWithoutScripts = skipUnscriptedVideos
    };

    /// <summary>Writes a funscript and returns its path.</summary>
    public string WriteScript(string fileName, Funscript script) {
        string path = Path(fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(script, ScriptOptions), new UTF8Encoding(false));
        return path;
    }

    /// <summary>
    /// Creates a stand-in video file. The content never matters because durations come from
    /// <see cref="FakeMediaProbe"/>; only the name does.
    /// </summary>
    public string WriteVideo(string fileName) {
        string path = Path(fileName);
        File.WriteAllText(path, "not really a video");
        return path;
    }

    public string ReadText(string fileName) => File.ReadAllText(Path(fileName));

    public bool Exists(string fileName) => File.Exists(Path(fileName));

    public void Dispose() {
        try {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        } catch (IOException) {
            // A scratch file that outlives the run is noise, not a test failure.
        }
    }
}

/// <summary>Shorthand for building the small funscripts the merge tests operate on.</summary>
internal static class ScriptBuilder
{
    /// <summary>A script whose keyframes are given as (millisecond, position) pairs.</summary>
    public static Funscript Basic(params (int At, int Pos)[] actions) => new() {
        Actions = [.. actions.Select(a => new ActionPoint { At = a.At, Pos = a.Pos })]
    };

    public static Funscript WithAxis(this Funscript script, string axisId, params (int At, int Pos)[] actions) {
        script.Axes ??= [];
        script.Axes.Add(new FunscriptAxis {
            Id = axisId,
            Actions = [.. actions.Select(a => new ActionPoint { At = a.At, Pos = a.Pos })]
        });

        return script;
    }

    public static Funscript WithMetadata(this Funscript script, FunscriptMetadata metadata) {
        script.Metadata = metadata;
        return script;
    }
}
