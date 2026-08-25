namespace AttcksMergeTool.Models;

/// <summary>
/// A progress tick from a merge step. <see cref="IsIndeterminate"/> covers phases
/// whose completion cannot be measured (the final concat), which the UI renders as
/// a marquee rather than a percentage.
/// </summary>
public readonly record struct MergeProgress(int Completed, int Total, bool IsIndeterminate = false)
{
    public static MergeProgress Indeterminate { get; } = new(0, 100, true);
}
