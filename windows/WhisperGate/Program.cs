using System;
using System.Windows.Forms;

namespace WhisperGate;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        using var app = new TrayApp();
        Application.Run();
    }
}
