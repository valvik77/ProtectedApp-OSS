using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ProtectedApp.Services;
using System.Runtime.InteropServices;
using WinRT;

namespace ProtectedApp;

public static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            ComWrappersSupport.InitializeComWrappers();
            Application.Start((p) =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Append("crash.log",
                $"{DateTimeOffset.Now:O} [Program.Main Exception]{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            throw;
        }
    }
}
