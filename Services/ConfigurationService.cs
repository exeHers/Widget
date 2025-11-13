using System;
using Microsoft.Extensions.Configuration;
using SideHUD.Models;
using System.IO;

namespace SideHUD.Services;

public class ConfigurationService
{
    private readonly IConfiguration _configuration;
    private AppConfig? _config;

    public ConfigurationService()
    {
        try
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

            _configuration = builder.Build();
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("Failed to load configuration, using defaults", ex);
            // Create empty configuration - will use defaults
            var builder = new ConfigurationBuilder();
            _configuration = builder.Build();
        }
    }

    public AppConfig GetConfig()
    {
        if (_config == null)
        {
            _config = new AppConfig();
            try
            {
                _configuration.Bind(_config);
            }
            catch (Exception ex)
            {
                ErrorLogger.Log("Failed to bind configuration, using defaults", ex);
                // Use default values already set in AppConfig
            }
            
            // Validate and clamp values to safe ranges
            _config.Opacity = Math.Max(0.1, Math.Min(1.0, _config.Opacity));
            _config.FontSize = Math.Max(8, Math.Min(32, _config.FontSize));
            _config.RightMarginPx = Math.Max(0, Math.Min(200, _config.RightMarginPx));
            _config.TopMarginPx = Math.Max(0, Math.Min(500, _config.TopMarginPx));
            _config.UpdateIntervalMs = Math.Max(100, Math.Min(5000, _config.UpdateIntervalMs));
            _config.TempWarn = Math.Max(0, Math.Min(150, _config.TempWarn));
            _config.TempHot = Math.Max(_config.TempWarn, Math.Min(150, _config.TempHot));
            _config.OverheadWatts = Math.Max(0, Math.Min(500, _config.OverheadWatts));
            _config.FpsMinThreshold = Math.Max(1, Math.Min(100, _config.FpsMinThreshold));
            _config.FpsOverlayPositionX = Math.Max(0, Math.Min(2000, _config.FpsOverlayPositionX));
            _config.FpsOverlayPositionY = Math.Max(0, Math.Min(2000, _config.FpsOverlayPositionY));
            _config.AutoDebugThreshold = Math.Max(1, Math.Min(100, _config.AutoDebugThreshold));
        }
        return _config;
    }

    public void Reload()
    {
        _config = null;
    }
}

