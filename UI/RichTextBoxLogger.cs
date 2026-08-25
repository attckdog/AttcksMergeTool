using AttcksMergeTool.Services;

namespace AttcksMergeTool.UI;

/// <summary>
/// Renders job log lines into a <see cref="RichTextBox"/>, colouring each by level.
/// </summary>
/// <remarks>
/// Merge services log from worker threads (notably the parallel encode loop), so every
/// write is marshalled onto the UI thread here rather than at each call site.
/// </remarks>
internal sealed class RichTextBoxLogger : IJobLogger
{
    private readonly RichTextBox _output;

    public RichTextBoxLogger(RichTextBox output) => _output = output;

    public void Log(string message, LogLevel level = LogLevel.Info) {
        if (_output.InvokeRequired) {
            _output.Invoke(() => Log(message, level));
            return;
        }

        if (_output.IsDisposed) return;

        // Append with a colour by collapsing the selection to the end first.
        _output.SelectionStart = _output.TextLength;
        _output.SelectionLength = 0;
        _output.SelectionColor = Theme.ForLogLevel(level);
        _output.AppendText(message + Environment.NewLine);
        _output.ScrollToCaret();
    }
}
