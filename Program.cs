using AttcksMergeTool.UI;

namespace AttcksMergeTool;

internal static class Program
{
    [STAThread]
    private static void Main() {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
