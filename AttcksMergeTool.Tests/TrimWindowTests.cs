using AttcksMergeTool.Models;

namespace AttcksMergeTool.Tests;

public class TrimWindowTests
{
    [Fact]
    public void None_keeps_every_keyframe_where_it_is() {
        Assert.False(TrimWindow.None.Excludes(0));
        Assert.False(TrimWindow.None.Excludes(999_999));
        Assert.Equal(1234, TrimWindow.None.Rebase(1234));
    }

    [Fact]
    public void FromSeconds_converts_to_milliseconds() {
        TrimWindow window = TrimWindow.FromSeconds(1.5, 10.25);

        Assert.Equal(1500, window.StartMs);
        Assert.Equal(10_250, window.EndMs);
    }

    [Fact]
    public void Keyframes_before_the_start_are_excluded() {
        var window = new TrimWindow(2000, 5000);

        Assert.True(window.Excludes(1999));
        Assert.False(window.Excludes(2000));
    }

    [Fact]
    public void Keyframes_after_the_end_are_excluded() {
        var window = new TrimWindow(2000, 5000);

        Assert.False(window.Excludes(5000));
        Assert.True(window.Excludes(5001));
    }

    [Fact]
    public void An_end_of_zero_means_run_to_the_end_of_the_source() {
        var window = new TrimWindow(2000, 0);

        Assert.False(window.Excludes(999_999));
    }

    /// <remarks>
    /// Matches <see cref="Services.FFmpegArguments.BuildEncode"/>, which only emits <c>-to</c>
    /// when the end is past the start. The script and the video have to agree about what an
    /// impossible window means, and both read it as "no end bound".
    /// </remarks>
    [Fact]
    public void An_end_before_the_start_is_ignored_rather_than_emptying_the_scene() {
        var window = new TrimWindow(5000, 2000);

        Assert.False(window.Excludes(10_000));
        Assert.True(window.Excludes(4999));
    }

    [Fact]
    public void Rebase_moves_the_survivors_onto_a_zero_based_timeline() {
        var window = new TrimWindow(2000, 5000);

        Assert.Equal(0, window.Rebase(2000));
        Assert.Equal(1000, window.Rebase(3000));
    }

    [Fact]
    public void Rebase_never_goes_negative() {
        Assert.Equal(0, new TrimWindow(2000, 5000).Rebase(1500));
    }
}
