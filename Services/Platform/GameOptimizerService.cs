using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Zapret2UI.Controls;

namespace Zapret2UI.Services.Platform;

/// <summary>
/// Monitors for running games (CS2, Dota 2, Steam/Epic games).
/// When a real game is active in foreground:
/// 1. Pauses the 3D particle background wave to eliminate GPU/CPU load.
/// 2. Performs a gentle memory cleanup.
/// 3. Shows a friendly toast notification ONCE per game launch.
/// </summary>
public sealed class GameOptimizerService : IDisposable
{
    private static readonly HashSet<string> KnownGameProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "cs2", "csgo", "dota2", "valorant-win64-shipping", "valorant",
        "r5apex", "fortniteclient-win64-shipping", "gta5", "overwatch",
        "pubg", "tslgame", "rainbowsix", "rainbowsix_vulkan", "rustclient",
        "genshinimpact", "starrail", "deadlock", "helldivers2", "warframe.x64",
        "leagueclientux", "league of legends", "cyberpunk2077", "witcher3",
        "javaw", "minecraft.windows", "cod", "bootstrapper", "rocketleague",
        "robloxplayerbeta"
    };

    private readonly Func<bool> _isNotificationsEnabled;
    private readonly Action<string, string> _notify;
    private readonly CancellationTokenSource _cts = new();
    private Task? _monitorTask;

    private bool _isGameRunning;
    private int _currentGamePid;
    private string _currentGameName = "";
    private bool _hasNotifiedThisSession;
    private DateTime _lastNotificationTime = DateTime.MinValue;
    private int _negativeChecksCount;

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
            catch { procAlive = false; }

            if (!procAlive)
            {
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
        }
        else if (_isGameRunning)
        {
            _negativeChecksCount++;
            // Require 6 consecutive negative checks (18 seconds) before dropping out of game mode
            if (_negativeChecksCount >= 6)
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

        // Pause 3D background wave to completely eliminate GPU rendering while gaming
        try
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                DotWaveBackground.IsPaused = true;
            });
        }
        catch { }

        TrimMemory();

        // Notify once per game launch with a 5-minute cooldown
        if (!_hasNotifiedThisSession && (DateTime.UtcNow - _lastNotificationTime).TotalMinutes > 5)
        {
            _hasNotifiedThisSession = true;
            _lastNotificationTime = DateTime.UtcNow;

            if (_isNotificationsEnabled())
            {
                string friendly = GetFriendlyGameName(name);
                _notify("zapret_timosha", $"Игровой режим: {friendly}");
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
            GC.Collect(1, GCCollectionMode.Optimized, false);
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

            // If foreground window is zapret_timosha, keep current game state if it was running
            if (fgPid == (uint)Environment.ProcessId)
            {
                if (_isGameRunning && _currentGamePid > 0)
                {
                    pid = _currentGamePid;
                    name = _currentGameName;
                    return true;
                }
                return false;
            }

            string procName = "";
            string? procPath = null;
            try
            {
                using var proc = Process.GetProcessById((int)fgPid);
                procName = proc.ProcessName.ToLowerInvariant();
                try { procPath = proc.MainModule?.FileName?.ToLowerInvariant(); } catch { }
            }
            catch { return false; }

            if (string.IsNullOrEmpty(procName)) return false;

            // 1. Check known game executable names directly
            if (KnownGameProcesses.Contains(procName))
            {
                pid = (int)fgPid;
                name = procName;
                return true;
            }

            // 2. Check path of game storefront installations
            if (!string.IsNullOrEmpty(procPath))
            {
                if (procPath.Contains(@"\steamapps\common\") ||
                    procPath.Contains(@"\epic games\") ||
                    procPath.Contains(@"\riot games\") ||
                    procPath.Contains(@"\gog galaxy\games\"))
                {
                    // Exclude launcher wrappers
                    if (procName is not ("steam" or "epicgameslauncher" or "riotclientux" or "riotclientservices" or "galaxyclient"))
                    {
                        pid = (int)fgPid;
                        name = procName;
                        return true;
                    }
                }
            }
        }
        catch { }

        return false;
    }

    private static string GetFriendlyGameName(string procName) => procName.ToLowerInvariant() switch
    {
        "cs2" => "Counter-Strike 2",
        "csgo" => "CS:GO",
        "dota2" => "Dota 2",
        "valorant-win64-shipping" or "valorant" => "Valorant",
        "r5apex" => "Apex Legends",
        "fortniteclient-win64-shipping" => "Fortnite",
        "gta5" => "GTA V",
        "overwatch" => "Overwatch 2",
        "pubg" or "tslgame" => "PUBG",
        "rainbowsix" or "rainbowsix_vulkan" => "Rainbow Six Siege",
        "rustclient" => "Rust",
        "genshinimpact" => "Genshin Impact",
        "starrail" => "Honkai: Star Rail",
        "deadlock" => "Deadlock",
        "helldivers2" => "Helldivers 2",
        "warframe.x64" => "Warframe",
        "cyberpunk2077" => "Cyberpunk 2077",
        "witcher3" => "The Witcher 3",
        "robloxplayerbeta" => "Roblox",
        _ => procName
    };

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        ExitGameMode();
    }

    #region Win32 P/Invoke
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    #endregion
}
