using AttcksMergeTool.Models;

namespace AttcksMergeTool.Services;

/// <summary>
/// Concatenates every scene's funscript onto a single timeline.
/// </summary>
/// <remarks>
/// Scenes are laid end to end in the order <see cref="TimelinePlan"/> fixed, which is the
/// order the videos are concatenated in. Each scene advances the running offset by its
/// video's duration when it has one and by its own last keyframe otherwise, so the merged
/// script stays aligned with the merged video - including across a video that has no script
/// at all, which contributes its length as silence. Auxiliary axes - whether
/// embedded in the document or supplied as <c>{scene}.{axis}.funscript</c> siblings -
/// are accumulated per axis id and emitted together at the end. Each input's descriptive
/// metadata is unioned into the merged document's own metadata block.
/// </remarks>
public sealed class FunscriptMerger
{
    private const string BasicScriptType = "basic";
    private const string MultiAxisScriptType = "multiaxis";

    private readonly IJobLogger _logger;
    private readonly MergeOptions _options;
    private readonly TrimLookup _trims;
    private readonly IMediaProbe _probe;

    public FunscriptMerger(IJobLogger logger, MergeOptions options, TrimLookup trims, IMediaProbe? probe = null) {
        _logger = logger;
        _options = options;
        _trims = trims;
        _probe = probe ?? FFprobe.Default;
    }

    /// <summary>
    /// Merges <paramref name="entries"/> and writes
    /// <see cref="MergeOptions.OutputScriptPath"/>. Returns <c>null</c> when there was
    /// nothing to merge.
    /// </summary>
    public async Task<FunscriptMergeResult?> MergeAsync(
        IReadOnlyList<TimelineEntry> entries,
        IProgress<MergeProgress>? progress = null,
        CancellationToken cancellationToken = default) {
        _logger.LogSection(" Step 1: Merging Funscripts");

        if (entries.Count == 0) {
            _logger.Log("No .funscript files found. Skipping script merge.", LogLevel.Warning);
            return null;
        }

        var state = new MergeState();
        int completed = 0;

        foreach (TimelineEntry entry in entries) {
            cancellationToken.ThrowIfCancellationRequested();

            await MergeEntryAsync(entry, state, cancellationToken);

            progress?.Report(new MergeProgress(++completed, entries.Count));
        }

        Funscript merged = BuildDocument(state);
        await new ScriptFileWriter(_logger, _options).WriteAsync(merged, cancellationToken);

        return new FunscriptMergeResult(merged, state.Spans, state.CurrentOffsetMs);
    }

    private async Task MergeEntryAsync(TimelineEntry entry, MergeState state, CancellationToken cancellationToken) {
        _logger.Log($"Processing Scene: {entry.Name}");

        (int sceneDurationMs, bool videoFound, TrimWindow trim) =
            await ResolveSceneTimingAsync(entry, cancellationToken);

        int sceneStartMs = state.CurrentOffsetMs;

        // Measured against the keyframes actually appended, which are rebased onto the trimmed
        // timeline. Taking it from the source timestamps instead would overshoot by the trim's
        // start offset on any scene that has to fall back to it.
        int lastKeyframeMs = 0;
        SceneScripts? scene = entry.Scripts;

        if (scene?.MainScriptPath is not null) {
            Funscript script = await ScriptReader.ReadAsync(scene.MainScriptPath, cancellationToken);
            state.Metadata.Add(script.Metadata);

            lastKeyframeMs = Math.Max(
                lastKeyframeMs,
                AppendActions(FunscriptAxisMap.RootAxisId, script.Actions, state, state.RootActions, trim));

            if (script.Axes is { Count: > 0 }) {
                state.ScriptType = MultiAxisScriptType;

                foreach (FunscriptAxis axis in script.Axes) {
                    string axisId = FunscriptAxisMap.Resolve(axis.Id, axis.Id);

                    // Register the axis even when empty, matching the original output shape.
                    List<ActionPoint> target = state.AxisActions(axisId);

                    lastKeyframeMs = Math.Max(
                        lastKeyframeMs, AppendActions(axisId, axis.Actions, state, target, trim));
                }
            }
        }

        // Emitted for every entry, including a video that has no script at all, so the merged
        // script's markers and the output's chapters describe exactly the same scenes.
        state.Bookmarks.Add(new Bookmark { Name = entry.Name, Time = sceneStartMs });

        if (scene is not null) {
            int siblingKeyframeMs = await MergeSiblingScriptsAsync(scene, state, trim, cancellationToken);
            lastKeyframeMs = Math.Max(lastKeyframeMs, siblingKeyframeMs);
        }

        // A real video duration wins, because it accounts for silent tails the script omits.
        int sceneLengthMs = videoFound && sceneDurationMs > 0 ? sceneDurationMs : lastKeyframeMs;

        state.Spans.Add(new SceneSpan(entry.Name, sceneStartMs, sceneLengthMs));
        state.CurrentOffsetMs = sceneStartMs + sceneLengthMs;
    }

    /// <summary>
    /// Folds in the per-axis <c>{scene}.{axis}.funscript</c> files and reports the latest
    /// keyframe appended across them.
    /// </summary>
    private async Task<int> MergeSiblingScriptsAsync(
        SceneScripts scene,
        MergeState state,
        TrimWindow trim,
        CancellationToken cancellationToken) {
        int lastKeyframeMs = 0;

        foreach (string siblingPath in scene.SiblingScriptPaths) {
            Funscript sibling = await ScriptReader.ReadAsync(siblingPath, cancellationToken);
            state.Metadata.Add(sibling.Metadata);
            state.ScriptType = MultiAxisScriptType;

            // "Scene.twist.funscript" -> "twist" -> "R0"
            string axisAlias = SceneScriptIndex.AxisAliasOf(scene, siblingPath);
            string axisId = FunscriptAxisMap.Resolve(axisAlias, axisAlias);

            if (sibling.Actions is { Count: > 0 }) {
                lastKeyframeMs = Math.Max(
                    lastKeyframeMs,
                    AppendActions(axisId, sibling.Actions, state, state.AxisActions(axisId), trim));
            }
        }

        return lastKeyframeMs;
    }

    /// <summary>
    /// Determines how long this scene occupies on the merged timeline, honouring any
    /// trim the user configured for its companion video.
    /// </summary>
    private async Task<(int DurationMs, bool VideoFound, TrimWindow Trim)> ResolveSceneTimingAsync(
        TimelineEntry entry,
        CancellationToken cancellationToken) {
        int durationMs = 0;
        bool videoFound = false;

        string? videoPath = entry.VideoPath;

        if (videoPath is null) {
            _logger.Log("  -> No Video Found", LogLevel.Warning);
        } else {
            int? probedMs = await _probe.GetDurationMsAsync(videoPath, cancellationToken);

            if (probedMs is > 0) {
                videoFound = true;
                durationMs = probedMs.Value;
                _logger.Log($"  -> Original Video: {durationMs}ms", LogLevel.Success);
            } else {
                // Not the same as having no video: falling back to the last keyframe drops the
                // scene's silent tail and shifts every later scene, so say so rather than
                // letting it look like a deliberate script-only scene.
                _logger.Log(
                    $"  -> Could not read the duration of {Path.GetFileName(videoPath)}. Using the "
                    + "last keyframe instead; later scenes may be offset.",
                    LogLevel.Warning);
            }
        }

        TrimWindow trim = _trims.WindowFor(entry.Name);

        if (trim != TrimWindow.None && videoFound && durationMs > 0) {
            int endMs = trim.EndMs > 0 ? Math.Min(trim.EndMs, durationMs) : durationMs;
            durationMs = Math.Max(0, endMs - trim.StartMs);
            _logger.Log($"  -> Trimmed Scene Duration: {durationMs}ms", LogLevel.Success);
        }

        return (durationMs, videoFound, trim);
    }

    /// <summary>
    /// Appends one axis' keyframes to the merged timeline at the current scene offset,
    /// smoothing the seam with the previous scene. Returns the latest timestamp appended,
    /// relative to the start of this scene.
    /// </summary>
    private int AppendActions(
        string axisId,
        List<ActionPoint>? actions,
        MergeState state,
        List<ActionPoint> target,
        TrimWindow trim) {
        if (actions is not { Count: > 0 }) return 0;

        List<ActionPoint> scoped = ApplyTrim(actions, trim);
        if (scoped.Count == 0) return 0;

        int offsetMs = state.CurrentOffsetMs;
        int lastKeyframeMs = 0;

        foreach (ActionPoint action in scoped) {
            lastKeyframeMs = Math.Max(lastKeyframeMs, action.At);
        }

        if (state.LastPositions.TryGetValue(axisId, out int previousPos)) {
            // Anchor the seam at the previous scene's final position, then collapse every
            // keyframe inside the transition window down to a single point. Without this the
            // device would snap from wherever the last scene ended to wherever this one opens.
            target.Add(new ActionPoint { At = offsetMs, Pos = previousPos });

            int transitionEndMs = Math.Min(_options.TransitionMs, scoped[^1].At);
            int collapsedPos = -1;
            bool anyCollapsed = false;

            foreach (ActionPoint action in scoped) {
                if (action.At < transitionEndMs) {
                    collapsedPos = action.Pos;
                    anyCollapsed = true;
                    continue;
                }

                if (anyCollapsed) {
                    if (action.At != transitionEndMs) {
                        target.Add(new ActionPoint { At = offsetMs + transitionEndMs, Pos = collapsedPos });
                    }
                    anyCollapsed = false;
                }

                target.Add(new ActionPoint { At = offsetMs + action.At, Pos = action.Pos });
            }

            // Everything in this axis fell inside the transition window.
            if (anyCollapsed) {
                target.Add(new ActionPoint { At = offsetMs + transitionEndMs, Pos = collapsedPos });
            }
        } else {
            // First scene on this axis: nothing to transition from.
            foreach (ActionPoint action in scoped) {
                target.Add(new ActionPoint { At = offsetMs + action.At, Pos = action.Pos });
            }
        }

        state.LastPositions[axisId] = scoped[^1].Pos;

        return lastKeyframeMs;
    }

    /// <summary>Drops keyframes outside the trim window and rebases the survivors to zero.</summary>
    private static List<ActionPoint> ApplyTrim(List<ActionPoint> actions, TrimWindow trim) {
        var scoped = new List<ActionPoint>(actions.Count);

        foreach (ActionPoint action in actions) {
            if (trim.Excludes(action.At)) continue;
            scoped.Add(new ActionPoint { At = trim.Rebase(action.At), Pos = action.Pos });
        }

        return scoped;
    }

    private Funscript BuildDocument(MergeState state) => new() {
        Version = "1.0",
        Inverted = false,
        Range = 100,
        Metadata = BuildMetadata(state),
        Actions = state.RootActions,
        Bookmarks = state.Bookmarks.OrderBy(b => b.Time).ToList(),
        Axes = state.AuxAxes.Select(axis => new FunscriptAxis { Id = axis.Key, Actions = axis.Value }).ToList()
    };

    /// <summary>
    /// Carries the sources' descriptive metadata into the merged script so credits and tags
    /// survive the merge. Fields that hold a single value are comma-joined; description and
    /// notes read as prose, so each source keeps its own line. Anything that collected
    /// nothing stays null and is omitted from the output entirely.
    /// </summary>
    private FunscriptMetadata BuildMetadata(MergeState state) => new() {
        Creator = state.Metadata.Creators.JoinOrNull(", "),
        Description = state.Metadata.Descriptions.JoinOrNull(Environment.NewLine),
        Duration = state.CurrentOffsetMs / 1000,
        License = state.Metadata.Licenses.JoinOrNull(", "),
        Notes = state.Metadata.Notes.JoinOrNull(Environment.NewLine),
        Performers = state.Metadata.Performers.ToListOrNull(),
        Tags = state.Metadata.Tags.ToListOrNull(),
        Title = _options.OutputName,
        Type = state.ScriptType
    };

    /// <summary>Accumulator carried across scenes for the duration of one merge.</summary>
    private sealed class MergeState
    {
        public List<ActionPoint> RootActions { get; } = [];
        public List<Bookmark> Bookmarks { get; } = [];

        /// <summary>Where each scene landed, in merge order, for the post-encode retime.</summary>
        public List<SceneSpan> Spans { get; } = [];
        public Dictionary<string, List<ActionPoint>> AuxAxes { get; } = [];

        /// <summary>Descriptive metadata unioned across every input read so far.</summary>
        public MetadataAccumulator Metadata { get; } = new();

        /// <summary>Final position per axis, used to smooth the seam into the next scene.</summary>
        public Dictionary<string, int> LastPositions { get; } = [];

        /// <summary>Start of the scene currently being merged, in milliseconds.</summary>
        public int CurrentOffsetMs { get; set; }

        public string ScriptType { get; set; } = BasicScriptType;

        public List<ActionPoint> AxisActions(string axisId) {
            if (!AuxAxes.TryGetValue(axisId, out List<ActionPoint>? actions)) {
                actions = [];
                AuxAxes[axisId] = actions;
            }
            return actions;
        }
    }

    /// <summary>
    /// Collects the descriptive metadata of every input, one field at a time. Values are a
    /// union rather than a last-writer-wins overwrite, because every source contributed part
    /// of the merged script and each one's credits should survive.
    /// </summary>
    private sealed class MetadataAccumulator
    {
        public OrderedTextSet Creators { get; } = new();
        public OrderedTextSet Descriptions { get; } = new();
        public OrderedTextSet Licenses { get; } = new();
        public OrderedTextSet Notes { get; } = new();
        public OrderedTextSet Performers { get; } = new();
        public OrderedTextSet Tags { get; } = new();

        public void Add(FunscriptMetadata? metadata) {
            if (metadata is null) return;

            Creators.Add(metadata.Creator);
            Descriptions.Add(metadata.Description);
            Licenses.Add(metadata.License);
            Notes.Add(metadata.Notes);
            Performers.AddRange(metadata.Performers);
            Tags.AddRange(metadata.Tags);
        }
    }

    /// <summary>
    /// Distinct strings in first-seen order, ignoring blanks and case-insensitive repeats.
    /// Order matters here: it makes the merged metadata read in scene order and keeps the
    /// output stable between runs over the same inputs.
    /// </summary>
    private sealed class OrderedTextSet
    {
        private readonly List<string> _values = [];
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string? value) {
            if (string.IsNullOrWhiteSpace(value)) return;

            string trimmed = value.Trim();
            if (_seen.Add(trimmed)) _values.Add(trimmed);
        }

        public void AddRange(IEnumerable<string>? values) {
            if (values is null) return;

            foreach (string value in values) Add(value);
        }

        /// <summary>The collected values, or <c>null</c> when nothing was collected.</summary>
        public List<string>? ToListOrNull() => _values.Count == 0 ? null : [.. _values];

        /// <summary>The values joined by <paramref name="separator"/>, or <c>null</c> when empty.</summary>
        public string? JoinOrNull(string separator) =>
            _values.Count == 0 ? null : string.Join(separator, _values);
    }
}
