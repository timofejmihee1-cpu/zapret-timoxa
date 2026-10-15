using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace Zapret2UI.Services.Platform;

/// <summary>
/// Evaluates system hardware specs (RAM, CPU logical cores, WPF Direct3D rendering tier)
/// to detect if the machine is a low-spec / weak computer.
/// </summary>
public static class HardwareSpecService
{
    private static bool? _cachedIsWeak;
    private static long? _cachedRamMb;

    public static bool IsWeakHardware()
    {
        if (_cachedIsWeak.HasValue) return _cachedIsWeak.Value;

        bool isWeak = DetectWeakHardware();
        _cachedIsWeak = isWeak;
        return isWeak;
    }

    private static bool DetectWeakHardware()
    {
        try
        {
            // 1. Check logical processor cores
            int cores = Environment.ProcessorCount;
            if (cores <= 2) return true; // 1 or 2 logical cores is definitely a weak CPU

            // 2. Check total physical RAM
            long totalRamMb = GetTotalPhysicalMemoryMb();
            if (totalRamMb is > 0 and <= 6144) return true; // <= 6 GB RAM

            // 3. Check WPF Hardware Graphics Acceleration Tier
            // Tier 0 (0x00000000): Software only (no hardware acceleration)
            // Tier 1 (0x00010000): Partial acceleration (DirectX 7-9 era, legacy integrated graphics)
            // Tier 2 (0x00020000): Full hardware acceleration (DirectX 9.0c+ with Pixel Shader 2.0+)
            int renderingTier = RenderCapability.Tier >> 16;
            if (renderingTier < 2) return true;

            // 4. Budget quad-core with low RAM and Tier 1 GPU
            if (cores <= 4 && totalRamMb <= 8192 && renderingTier < 2) return true;
        }
        catch { }

        return false;
    }

    public static string GetHardwareSummary()
    {
        int cores = Environment.ProcessorCount;
        long ramMb = GetTotalPhysicalMemoryMb();
        int tier = RenderCapability.Tier >> 16;
        string ramStr = ramMb > 0 ? $"{Math.Round(ramMb / 1024.0, 1)} ГБ" : "N/A";
        string tierDesc = tier switch
        {
            0 => "Tier 0 (без ускорения)",
            1 => "Tier 1 (базовое)",
            _ => "Tier 2 (полное)"
        };
        return $"{cores} ядер, {ramStr} ОЗУ, Видеокарта: {tierDesc}";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
        public MEMORYSTATUSEX() => dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    public static long GetTotalPhysicalMemoryMb()
    {
        if (_cachedRamMb.HasValue) return _cachedRamMb.Value;
        try
        {
            var status = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(status))
            {
                long mb = (long)(status.ullTotalPhys / (1024 * 1024));
                _cachedRamMb = mb;
                return mb;
            }
        }
        catch { }
        return 0;
    }
}
