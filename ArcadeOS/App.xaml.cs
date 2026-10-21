using System.Windows;
using ArcadeOS.Services;
namespace ArcadeOS;
public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) => MetadataDiagnostics.Record(AutomaticMetadataService.DefaultCacheDirectory, e.Exception, "Unhandled UI exception");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        { if (e.ExceptionObject is Exception error) MetadataDiagnostics.Record(AutomaticMetadataService.DefaultCacheDirectory, error, "Fatal application exception"); };
        TaskScheduler.UnobservedTaskException += (_, e) => MetadataDiagnostics.Record(AutomaticMetadataService.DefaultCacheDirectory, e.Exception, "Unobserved background exception");
    }
}
