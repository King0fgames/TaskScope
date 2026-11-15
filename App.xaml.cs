using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace TaskScope;

public partial class App : Application
{
    void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.AppendAllText(Path.Combine(Settings.Folder, "error.log"), $"[{DateTime.Now:u}] {e.Exception}\n\n");
        }
        catch { }
        MessageBox.Show(e.Exception.Message, "TaskScope error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
