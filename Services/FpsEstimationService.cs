using System;
using System.Diagnostics;

namespace SideHUD.Services;

/// <summary>
/// Fallback FPS estimation service that estimates FPS based on frame timing
/// when RTSS is not available or not providing data.
/// </summary>
public class FpsEstimationService
{
    private readonly Stopwatch _frameTimer = Stopwatch.StartNew();
    private readonly System.Collections.Generic.Queue<double> _frameTimes = new System.Collections.Generic.Queue<double>();
    private const int MAX_FRAME_SAMPLES = 60; // Keep last 60 frames
    private double _lastFrameTime = 0;
    private int _frameCount = 0;

    public double EstimateFps()
    {
        try
        {
            var currentTime = _frameTimer.Elapsed.TotalSeconds;
            
            if (_lastFrameTime > 0)
            {
                var frameTime = currentTime - _lastFrameTime;
                
                // Only accept reasonable frame times (1ms to 1 second)
                if (frameTime >= 0.001 && frameTime <= 1.0)
                {
                    _frameTimes.Enqueue(frameTime);
                    
                    // Keep only last N samples
                    while (_frameTimes.Count > MAX_FRAME_SAMPLES)
                    {
                        _frameTimes.Dequeue();
                    }
                    
                    _frameCount++;
                }
            }
            
            _lastFrameTime = currentTime;
            
            // Calculate average FPS from frame times
            if (_frameTimes.Count >= 10) // Need at least 10 samples for accuracy
            {
                double totalFrameTime = 0;
                foreach (var ft in _frameTimes)
                {
                    totalFrameTime += ft;
                }
                
                double avgFrameTime = totalFrameTime / _frameTimes.Count;
                double estimatedFps = 1.0 / avgFrameTime;
                
                // Validate FPS is in reasonable range
                if (estimatedFps >= 1 && estimatedFps <= 10000)
                {
                    return estimatedFps;
                }
            }
        }
        catch (Exception ex)
        {
            ErrorLogger.Log("FPS estimation error", ex);
        }
        
        return 0;
    }
    
    public void Reset()
    {
        _frameTimes.Clear();
        _lastFrameTime = 0;
        _frameCount = 0;
        _frameTimer.Restart();
    }
}

