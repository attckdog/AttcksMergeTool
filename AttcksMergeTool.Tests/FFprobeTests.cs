using AttcksMergeTool.Services;
using AttcksMergeTool.Tests.Support;

namespace AttcksMergeTool.Tests;

/// <summary>Covers how ffprobe's output is read, with the executable faked out.</summary>
public class FFprobeTests
{
    [Theory]
    [InlineData("1\r\n", true)]
    [InlineData("1\r\n2\r\n", true)]
    [InlineData("", false)]
    [InlineData("\r\n", false)]
    public async Task Audio_is_present_when_ffprobe_lists_an_audio_stream(string output, bool expected) {
        var probe = new FFprobe(new FakeProcessRunner { Respond = _ => output });

        Assert.Equal(expected, await probe.HasAudioAsync(@"C:\in\Scene.mp4"));
    }

    /// <remarks>
    /// An unreadable file is assumed to have audio, so the encode fails on it with ffmpeg's
    /// own error instead of quietly producing a mute segment.
    /// </remarks>
    [Fact]
    public async Task A_failed_probe_assumes_audio() {
        var probe = new FFprobe(new FakeProcessRunner {
            Respond = _ => throw new ExternalToolException("ffprobe", 1, "Invalid data")
        });

        Assert.True(await probe.HasAudioAsync(@"C:\in\Scene.mp4"));
    }
}
