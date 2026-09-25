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
/// at all, which contributes its length as silence.
/// <para>
/// Between scenes sits a gap: black that the video stage generates, sized by
/// <see cref="TransitionGap"/> so no axis has to travel faster than the configured limit to
/// reach the position the next scene opens at. Each scene is therefore read in full before
/// the gap in front of it can be measured, which is why merging one is two passes - prepare,
/// then append - rather than a single walk.
/// </para>
/// Auxiliary axes - whether
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

        // Keyframes past the end of the video would land on top of the scenes after this one,
        // so a scene with a known length is held to it. Script-only scenes have no such limit.
        int? endMs = videoFound && sceneDurationMs > 0 ? sceneDurationMs : null;

        // Read before anything is emitted: the gap in front of this scene is sized by where
        // its axes open, which is not known until its files have been read and trimmed.
        PreparedScene prepared = await PrepareSceneAsync(entry.Scripts, state, trim, endMs, cancellationToken);

        if (prepared.DroppedPastEnd > 0) {
            _logger.Log(
                $"  -> Dropped {prepared.DroppedPastEnd} keyframe(s) past the end of the video "
                + $"({sceneDurationMs}ms). The script runs longer than its scene.",
                LogLevel.Warning);
        }

        int leadInMs = InsertGap(prepared, state);
        int sceneStartMs = state.CurrentOffsetMs;

        foreach (SceneTrack track in prepared.Tracks) Append(track, state);

        // Emitted for every entry, including a video that has no script at all, so the merged
        // script's markers and the output's chapters describe exactly the same scenes. It
        // marks the first real frame, past the lead-in rather than at the start of it.
        state.Bookmarks.Add(new Bookmark { Name = entry.Name, Time = sceneStartMs });

        // A real video duration wins, because it accounts for silent tails the script omits.
        int sceneLengthMs = videoFound && sceneDurationMs > 0 ? sceneDurationMs : prepared.LastKeyframeMs;

        state.Spans.Add(new SceneSpan(entry.Name, sceneStartMs, sceneLengthMs, leadInMs));
        state.CurrentOffsetMs = sceneStartMs + sceneLengthMs;
    }

    /// <summary>
    /// Reads everything one scene contributes - its main script, the axes embedded in it, and
    /// its <c>{scene}.{axis}.funscript</c> siblings - and trims each onto a zero-based
    /// timeline, without committing any of it to the merge.
    /// </summary>
    /// <remarks>
    /// Nothing here touches the running offset, because the offset is not settled yet: the gap
    /// this scene sits behind depends on what these tracks turn out to open at. Only the
    /// accumulators that are order-independent - metadata, the script type, and the
    /// registration of embedded axes - are written through to <paramref name="state"/>.
    /// </remarks>
    private async Task<PreparedScene> PrepareSceneAsync(
        SceneScripts? scene,
        MergeState state,
        TrimWindow trim,
        int? endMs,
        CancellationToken cancellationToken) {
        var prepared = new PreparedScene(endMs);

        if (scene is null) return prepared;

        if (scene.MainScriptPath is not null) {
            Funscript script = await ScriptReader.ReadAsync(scene.MainScriptPath, cancellationToken);
            state.Metadata.Add(script.Metadata);

            prepared.Add(FunscriptAxisMap.RootAxisId, script.Actions, trim);

            if (script.Axes is { Count: > 0 }) {
                state.ScriptType = MultiAxisScriptType;

                foreach (FunscriptAxis axis in script.Axes) {
                    string axisId = FunscriptAxisMap.Resolve(axis.Id, axis.Id);

                    // Register the axis even when empty, matching the original output shape.
                    // The root track does not live in that list, so it is left out of it.
                    if (axisId != FunscriptAxisMap.RootAxisId) state.AxisActions(axisId);

                    prepared.Add(axisId, axis.Actions, trim);
                }
            }
        }

        foreach (string siblingPath in scene.SiblingScriptPaths) {
            Funscript sibling = await ScriptReader.ReadAsync(siblingPath, cancellationToken);
            state.Metadata.Add(sibling.Metadata);
            state.ScriptType = MultiAxisScriptType;

            // "Scene.twist.funscript" -> "twist" -> "R0"
            string axisAlias = SceneScriptIndex.AxisAliasOf(scene, siblingPath);

            prepared.Add(FunscriptAxisMap.Resolve(axisAlias, axisAlias), sibling.Actions, trim);
        }

        return prepared;
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
    /// Opens a stretch of black in front of the scene about to be appended, long enough for
    /// every axis to make the trip at no more than the configured speed, and parks each one at
    /// the halfway position in the middle of it. Returns the gap's length, or zero when none
    /// was inserted.
    /// </summary>
    /// <remarks>
    /// One keyframe per axis is the whole transition. Every player interpolates between
    /// points, so the two legs - out of the previous scene's final position over the first
    /// half of the gap, into the next scene's opening position over the second - draw
    /// themselves. Further points would only re-specify a line the player already draws.
    /// <para>
    /// There is deliberately no anchor keyframe at the seam: the previous scene's own last
    /// keyframe is already sitting there, and a duplicate of it would say nothing.
    /// </para>
    /// </remarks>
    private int InsertGap(PreparedScene prepared, MergeState state) {
        // Never in front of the first scene. Nothing has moved yet for the gap to slow down,
        // and a merge that opens on black reads as a fault rather than as a transition.
        if (!_options.InsertTransitionGaps || state.Spans.Count == 0) return 0;

        int gapMs = TransitionGap.DurationMs(
            state.LastPositions, prepared.FirstPositions, _options.MaxAxisSpeed, _options.TargetFps);

        if (gapMs == 0) return 0;

        int middleMs = state.CurrentOffsetMs + (gapMs / 2);

        foreach (string axisId in TransitionGap.ActiveAxes(state.LastPositions, prepared.FirstPositions)) {
            state.TargetFor(axisId).Add(
                new ActionPoint { At = middleMs, Pos = TransitionGap.MidPosition });

            // An axis this scene does not script stays parked here rather than where the last
            // one left it, and that is what the next seam measures its travel from.
            state.LastPositions[axisId] = TransitionGap.MidPosition;
        }

        _logger.Log($"  -> {gapMs}ms transition gap before this scene", LogLevel.Success);

        state.CurrentOffsetMs += gapMs;

        return gapMs;
    }

    /// <summary>Appends one prepared axis' keyframes at the current scene offset, unaltered.</summary>
    /// <remarks>
    /// Nothing is collapsed or dropped here. The seam was smoothed by the gap in front of the
    /// scene, which bought the device the time it needed; touching the scene's own opening
    /// keyframes on top of that would throw away motion the script asked for.
    /// </remarks>
    private static void Append(SceneTrack track, MergeState state) {
        List<ActionPoint> target = state.TargetFor(track.AxisId);
        int offsetMs = state.CurrentOffsetMs;

        foreach (ActionPoint action in track.Actions) {
            target.Add(new ActionPoint { At = offsetMs + action.At, Pos = action.Pos });
        }

        state.LastPositions[track.AxisId] = track.Actions[^1].Pos;
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

    /// <summary>One axis' keyframes from a single scene, trimmed and rebased to zero.</summary>
    private sealed record SceneTrack(string AxisId, List<ActionPoint> Actions);

    /// <summary>
    /// Everything one scene has to contribute, read and trimmed but not yet placed on the
    /// merged timeline.
    /// </summary>
    /// <remarks>
    /// It exists so the gap in front of a scene can be sized before the scene is committed:
    /// that needs <see cref="FirstPositions"/>, which is only knowable once every file has
    /// been read. Holding the result means nothing is read twice.
    /// </remarks>
    private sealed class PreparedScene(int? endMs)
    {
        private readonly List<SceneTrack> _tracks = [];
        private readonly Dictionary<string, int> _firstPositions = new(StringComparer.Ordinal);

        public IReadOnlyList<SceneTrack> Tracks => _tracks;

        /// <summary>Keyframes discarded for falling after the end of the scene's video.</summary>
        public int DroppedPastEnd { get; private set; }

        /// <summary>Where each axis opens, which is what the preceding gap has to reach.</summary>
        public IReadOnlyDictionary<string, int> FirstPositions => _firstPositions;

        /// <summary>Latest keyframe across every track, relative to the start of the scene.</summary>
        public int LastKeyframeMs { get; private set; }

        /// <summary>
        /// Trims one axis onto a zero-based timeline, cuts it at the end of the scene, and keeps
        /// it in time order, unless nothing survives.
        /// </summary>
        /// <remarks>
        /// Sorted because the input is not required to be, and the merged axis has to be: every
        /// player assumes it, and so does the retime, which clamps anything out of order.
        /// Stable, so keyframes sharing a timestamp keep the order the script gave them.
        /// </remarks>
        public void Add(string axisId, List<ActionPoint>? actions, TrimWindow trim) {
            if (actions is not { Count: > 0 }) return;

            List<ActionPoint> scoped = [.. ApplyTrim(actions, trim).OrderBy(action => action.At)];

            if (endMs is int end) {
                DroppedPastEnd += scoped.RemoveAll(action => action.At > end);
            }

            if (scoped.Count == 0) return;

            _tracks.Add(new SceneTrack(axisId, scoped));

            // First writer wins, for a scene that supplies one axis twice - embedded in the
            // main script and again as a sibling file. The two are appended in that order, so
            // the embedded one is what the seam actually has to arrive at.
            _firstPositions.TryAdd(axisId, scoped[0].Pos);

            LastKeyframeMs = Math.Max(LastKeyframeMs, scoped[^1].At);
        }
    }

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

        /// <summary>
        /// Where each axis was left, which is what the next seam measures its travel from. A
        /// gap parks every axis it covers at the halfway position, so this is not always the
        /// last position some scene scripted.
        /// </summary>
        public Dictionary<string, int> LastPositions { get; } = [];

        /// <summary>Start of the scene currently being merged, in milliseconds.</summary>
        public int CurrentOffsetMs { get; set; }

        public string ScriptType { get; set; } = BasicScriptType;

        /// <summary>
        /// Where keyframes for <paramref name="axisId"/> go, treating the stroke axis like any
        /// other so a caller walking a scene's axes does not have to special-case it. L0 lives
        /// at the document root rather than in the axis list.
        /// </summary>
        public List<ActionPoint> TargetFor(string axisId) =>
            axisId == FunscriptAxisMap.RootAxisId ? RootActions : AxisActions(axisId);

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
