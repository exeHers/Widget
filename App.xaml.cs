using System;
using System.Windows;
using System.Windows.Threading;
using SideHUD.Services;

namespace SideHUD;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        
        // Global exception handlers
        this.DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
    }
    
    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLogger.Log("Unhandled UI exception", e.Exception);
        e.Handled = true; // Prevent crash
        
        MessageBox.Show(
            $"An error occurred: {e.Exception.Message}\n\nCheck error log for details.",
            "SideHUD Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error
        );
    }
    
    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            ErrorLogger.Log("Unhandled application exception", ex);
        }
    }
}

