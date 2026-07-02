using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WordLinkSolver.Models;
using WordLinkSolver.UI;

namespace WordLinkSolver;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private const int AttachParentProcess = -1;

    [STAThread]
    [SupportedOSPlatform("windows10.0.19041.0")]
    private static void Main()
    {
        // Show Console.WriteLine output (dictionary load stats) when launched
        // from a terminal; harmless otherwise.
        AttachConsole(AttachParentProcess);

        ApplicationConfiguration.Initialize();

        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.ToString(), "WordLink Solver — unexpected error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);

        var settings = AppSettings.Load();
        Application.Run(new MainWindow(settings));
    }
}
