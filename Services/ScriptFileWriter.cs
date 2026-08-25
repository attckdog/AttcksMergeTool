using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using AttcksMergeTool.Models;

namespace AttcksMergeTool.Services;

/// <summary>
/// Writes the merged document to <see cref="MergeOptions.OutputScriptPath"/>.
/// </summary>
/// <remarks>
/// Its own service because the document is written twice: once by the script merge, so that a
/// failed encode still leaves a usable script, and again after the encode has retimed it onto
/// the measured segment lengths.
/// </remarks>
public sealed class ScriptFileWriter(IJobLogger logger, MergeOptions options)
{
    private static readonly JsonSerializerOptions WriteOptions = new() {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task WriteAsync(Funscript document, CancellationToken cancellationToken = default) {
        string json = JsonSerializer.Serialize(document, WriteOptions);

        // No BOM: some funscript players reject it.
        await File.WriteAllTextAsync(options.OutputScriptPath, json, new UTF8Encoding(false), cancellationToken);

        logger.Log($"Success! Saved script to {options.OutputScriptPath}", LogLevel.Success);
    }
}
