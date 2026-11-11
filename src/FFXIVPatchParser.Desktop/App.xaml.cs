using System.IO;
using System.Windows;

namespace FFXIVPatchParser.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var temporary = Path.Combine(Workspace.Temp, "temp");
        Directory.CreateDirectory(temporary);
        Environment.SetEnvironmentVariable("TEMP", temporary);
        Environment.SetEnvironmentVariable("TMP", temporary);
        base.OnStartup(e);
    }
}
