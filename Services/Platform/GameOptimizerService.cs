using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Zapret2UI.Controls;

namespace Zapret2UI.Services.Platform;

/// <summary>
/// Monitors for running games (Direct3D/Vulkan fullscreen, borderless windows, known game processes).
/// When a game is active:
/// 1. Pauses the 3D particle background wave to eliminate GPU/CPU load.
/// 2. Drastically trims RAM working set from ~250 MB down to ~15-25 MB.
/// 3. Shows a toast notification ONCE per game launch (if enabled in settings).
/// </summary>
public sealed class GameOptimizerService : IDisposable
{
    private readonly Func<bool> _isNotificationsEnabled;
    private readonly Action<string, string> _notify;
    private readonly CancellationTokenSource _cts = new();
    private Task? _monitorTask;

    private bool _isGameRunning;
    private int _currentGamePid = 0;
    private string _currentGameName = "";
    private bool _hasNotifiedThisSession = false;
    private DateTime _lastNotificationTime = DateTime.MinValue;
    private DateTime _lastTrimTime = DateTime.MinValue;
    private int _negativeChecksCount = 0;

    public bool IsEnabled { get; set; } = true;
    public bool IsGameRunning => _isGameRunning;

    public event Action<bool>? GameModeChanged;

    public GameOptimizerService(Func<bool> isNotificationsEnabled, Action<string, string> notify)
    {
        _isNotificationsEnabled = isNotificationsEnabled;
        _notify = notify;
    }

    public void Start()
    {
        if (_monitorTask != null) return;
        _monitorTask = Task.Run(MonitorLoopAsync);
    }

    private async Task MonitorLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                if (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    CheckGameStatus();
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* best effort */ }
        }
    }

    private void CheckGameStatus()
    {
        if (!IsEnabled)
        {
            if (_isGameRunning) ExitGameMode();
            return;
        }

        // If we are currently in game mode, first verify if the tracked game process is still alive
        if (_isGameRunning && _currentGamePid > 0)
        {
            bool procAlive = false;
            try
            {
                using var p = Process.GetProcessById(_currentGamePid);
                if (!p.HasExited) procAlive = true;
            }
            catch
            {
                procAlive = false;
            }

            if (!procAlive)
            {
                // Game process has closed
                ExitGameMode();
                return;
            }
        }

        bool gameDetected = DetectGame(out int detectedPid, out string detectedName);

        if (gameDetected)
        {
            _negativeChecksCount = 0;
            if (!_isGameRunning)
            {
                EnterGameMode(detectedPid, detectedName);
            }
            else if ((DateTime.UtcNow - _lastTrimTime).TotalSeconds > 60)
            {
                // Periodic maintenance trim while in game
                TrimMemory();
            }
        }
        else if (_isGameRunning)
        {
            // If the foreground window temporarily switched (e.g. our toast popup, Alt-Tab, or notification)
            // do NOT immediately drop out of game mode! Require 10 consecutive ticks (30 seconds) of no game detected.
            _negativeChecksCount++;
            if (_negativeChecksCount >= 10)
            {
                ExitGameMode();
            }
        }
    }

    private void EnterGameMode(int pid, string name)
    {
        _isGameRunning = true;
        _currentGamePid = pid;
        _currentGameName = name;
        _negativeChecksCount = 0;
        _lastTrimTime = DateTime.UtcNow;

        // 1. Pause background particle wave animation to eliminate GPU/CPU usage
        try
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                DotWaveBackground.IsPaused = true;
            });
        }
        catch { }

        // 2. Trim memory aggressively
        TrimMemory();

        // 3. Fire notification ONCE per game session with cooldown
        if (!_hasNotifiedThisSession && (DateTime.UtcNow - _lastNotificationTime).TotalSeconds > 60)
        {
            _hasNotifiedThisSession = true;
            _lastNotificationTime = DateTime.UtcNow;

            if (_isNotificationsEnabled())
            {
                _notify("zapret_timosha", "Оптимизация включена (ОЗУ освобождено)");
            }
        }

        GameModeChanged?.Invoke(true);
    }

    private void ExitGameMode()
    {
        _isGameRunning = false;
        _currentGamePid = 0;
        _currentGameName = "";
        _hasNotifiedThisSession = false;
        _negativeChecksCount = 0;

        // Resume background animation
        try
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                DotWaveBackground.IsPaused = false;
            });
        }
        catch { }

        GameModeChanged?.Invoke(false);
    }

    public static void TrimMemory()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);

            var process = Process.GetCurrentProcess();
            EmptyWorkingSet(process.Handle);
            SetProcessWorkingSetSize(process.Handle, (IntPtr)(-1), (IntPtr)(-1));
        }
        catch { }
    }

    private bool DetectGame(out int pid, out string name)
    {
        pid = 0;
        name = "";

        try
        {
            var fgHwnd = GetForegroundWindow();
            if (fgHwnd == IntPtr.Zero) return false;

            GetWindowThreadProcessId(fgHwnd, out uint fgPid);
            if (fgPid == 0) return false;

            // If foreground window is our own app (e.g. toast notification), do NOT treat as "game closed"
            if (fgPid == (uint)Environment.ProcessId)
            {
                if (_isGameRunning)
                {
                    pid = _currentGamePid;
                    name = _currentGameName;
                    return true;
                }
                return false;
            }

            string procName = "";
            try
            {
                using var proc = Process.GetProcessById((int)fgPid);
                procName = proc.ProcessName.ToLowerInvariant();
            }
            catch { return false; }

            // System shells
            if (procName is "explorer" or "lockapp" or "shellexperiencehost" or "searchhost" or "startmenuexperiencehost" or "textinputhost" or "taskmgr")
            {
                return false;
            }

            // Check if foreground window is fullscreen
            bool isFullscreen = IsWindowCoveringMonitor(fgHwnd);

            // 1. Direct3D / Vulkan fullscreen state
            if (SHQueryUserNotificationState(out var state) == 0)
            {
                if (state == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN)
                {
                    pid = (int)fgPid;
                    name = procName;
                    return true;
                }
            }

            // 2. Fullscreen window of a game / application
            if (isFullscreen)
            {
                pid = (int)fgPid;
                name = procName;
                return true;
            }

            // 3. Known games running in foreground (e.g. CS2, Dota 2, etc.)
            if (procName is "cs2" or "csgo" or "dota2" or "valorant-win64-shipping" or "r5apex"
                or "fortniteclient-win64-shipping" or "gta5" or "overwatch" or "pubg"
                or "rainbowsix" or "rustclient" or "genshinimpact" or "starrail")
            {
                pid = (int)fgPid;
                name = procName;
                return true;
            }
        }
        catch { }

        return false;
    }

    private static bool IsWindowCoveringMonitor(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT winRect)) return false;

        IntPtr hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hMonitor == IntPtr.Zero) return false;

        MONITORINFO mi = new MONITORINFO();
        mi.cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO));
        if (!GetMonitorInfo(hMonitor, ref mi)) return false;

        // Allow 2px tolerance for multi-monitor borders
        return winRect.Left <= mi.rcMonitor.Left + 2 &&
               winRect.Top <= mi.rcMonitor.Top + 2 &&
               winRect.Right >= mi.rcMonitor.Right - 2 &&
               winRect.Bottom >= mi.rcMonitor.Bottom - 2;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        ExitGameMode();
    }

    #region Win32 P/Invoke
    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE pquns);

    private enum QUERY_USER_NOTIFICATION_STATE
    {
        QUNS_NOT_PRESENT = 1,
        QUNS_BUSY = 2,
        QUNS_RUNNING_D3D_FULL_SCREEN = 3,
        QUNS_PRESENTATION_MODE = 4,
        QUNS_ACCEPTS_NOTIFICATIONS = 5,
        QUNS_QUIET_TIME = 6,
        QUNS_APP = 7
    }

    [DllImport("psapi.dll")]
    private static extern int EmptyWorkingSet(IntPtr hwProc);

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
    #endregion
}
