using System.Text;

using AttcksMergeTool.Models;

namespace AttcksMergeTool.Services;

/// <summary>
/// Writes chapter spans out as an FFMETADATA file, which the concat step feeds back into
/// ffmpeg so the output video carries navigable chapters.
/// </summary>
/// <remarks>
/// Writing is all this does; the file outlives the step that produced it, so
/// <see cref="MergeCoordinator"/> owns deleting it.
/// </remarks>
public sealed class ChapterFileWriter
{
    private readonly IJobLogger _logger;
    private readonly MergeOptions _options;

    public ChapterFileWriter(IJobLogger logger, MergeOptions options) {
        _logger = logger;
        _options = options;
    }

    /// <summary>
    /// Writes <see cref="MergeOptions.ChapterMetadataFile"/>. No-op when there is nothing
    /// to write.
    /// </summary>
    /// <remarks>
    /// Chapter titles are not escaped. Of the format's special characters only a literal
    /// backslash is consumed by ffmpeg's parser, and titles come from
    /// <see cref="Path.GetFileNameWithoutExtension(string)"/>, so they can never contain one.
    /// </remarks>
    public void Write(IReadOnlyList<Chapter> chapters) {
        if (chapters.Count == 0) return;

        _logger.Log("Generating Video Chapters metadata...", LogLevel.Heading);

        var metadata = new StringBuilder();
        metadata.AppendLine(";FFMETADATA1");
        metadata.AppendLine($"title={_options.OutputName}");

        foreach (Chapter chapter in chapters) {
            // Defensive: a caller-supplied empty or inverted span would make ffmpeg reject
            // the whole metadata file, taking the chapters of every other scene with it.
            if (chapter.EndMs <= chapter.StartMs) continue;

            metadata.AppendLine("[CHAPTER]");
            metadata.AppendLine("TIMEBASE=1/1000");
            metadata.AppendLine($"START={chapter.StartMs}");
            metadata.AppendLine($"END={chapter.EndMs}");
            metadata.AppendLine($"title={chapter.Name}");
        }

        File.WriteAllText(_options.ChapterMetadataFile, metadata.ToString(), new UTF8Encoding(false));
    }
}
