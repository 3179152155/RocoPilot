using System.Diagnostics;
using System.Security;

using Microsoft.Extensions.Logging;

using RocoPilot.Contracts.Services;
using RocoPilot.Configuration;
using RocoPilot.Models;
using RocoPilot.Models.Hotkeys;

using InterceptionInput = InputInterceptorNS.InputInterceptor;
using InterceptionMouseFilter = InputInterceptorNS.MouseFilter;
using InterceptionMouseHook = InputInterceptorNS.MouseHook;

namespace RocoPilot.Services;

public sealed class CameraSweepService : IDisposable
{
    private readonly IHotkeyService _hotkeyService;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly IGameWindowService _gameWindowService;
    private readonly IKeyboardInputService _keyboardInputService;
    private readonly IAppNotificationService _appNotificationService;
    private readonly ILogger<CameraSweepService> _logger;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _settingsGate = new(1, 1);

    private CameraSweepSettings _settings = CameraSweepSettings.CreateDefault();
    private CancellationTokenSource? _sweepCancellationTokenSource;
    private Task? _sweepTask;
    private InterceptionMouseHook? _mouseHook;
    private bool _isEnabled;
    private bool _isDisposed;

    public event EventHandler? SettingsChanged;

    public CameraSweepSettings Settings
    {
        get
        {
            lock (_stateLock)
            {
                return _settings.Clone();
            }
        }
    }

    public bool IsEnabled
    {
        get
        {
            lock (_stateLock)
            {
                return _isEnabled;
            }
        }
    }

    public CameraSweepService(
        IHotkeyService hotkeyService,
        ILocalSettingsService localSettingsService,
        IGameWindowService gameWindowService,
        IKeyboardInputService keyboardInputService,
        IAppNotificationService appNotificationService,
        ILogger<CameraSweepService> logger)
    {
        _hotkeyService = hotkeyService;
        _localSettingsService = localSettingsService;
        _gameWindowService = gameWindowService;
        _keyboardInputService = keyboardInputService;
        _appNotificationService = appNotificationService;
        _logger = logger;
        _hotkeyService.HotkeyTriggered += HotkeyService_HotkeyTriggered;
    }

    public async Task LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        CameraSweepSettings settings;
        try
        {
            await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var savedSettings = await _localSettingsService
                    .ReadSettingAsync<CameraSweepSettings>(SettingsKeys.CameraSweepSettings)
                    .ConfigureAwait(false);
                settings = CameraSweepSettings.Normalize(savedSettings);
                lock (_stateLock)
                {
                    _settings = settings;
                }
            }
            finally
            {
                _settingsGate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "读取视角巡航设置失败，将使用默认值");
            settings = CameraSweepSettings.CreateDefault();
            lock (_stateLock)
            {
                _settings = settings;
            }
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveSettingsAsync(
        CameraSweepSettings settings,
        CancellationToken cancellationToken = default)
    {
        var normalized = CameraSweepSettings.Normalize(settings);
        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _localSettingsService
                .SaveSettingAsync(SettingsKeys.CameraSweepSettings, normalized)
                .ConfigureAwait(false);
            lock (_stateLock)
            {
                _settings = normalized;
            }
        }
        finally
        {
            _settingsGate.Release();
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        Task? sweepTask;
        lock (_stateLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            sweepTask = StopCore();
        }

        _hotkeyService.HotkeyTriggered -= HotkeyService_HotkeyTriggered;
        try
        {
            sweepTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        lock (InterceptionSynchronization.Gate)
        {
            _mouseHook?.Dispose();
            _mouseHook = null;
        }

        _settingsGate.Dispose();
    }

    public async Task StopAsync()
    {
        Task? sweepTask;
        lock (_stateLock)
        {
            sweepTask = StopCore();
        }

        if (sweepTask is not null)
        {
            await sweepTask.ConfigureAwait(false);
        }
    }

    private void HotkeyService_HotkeyTriggered(object? sender, HotkeyTriggeredEventArgs e)
    {
        if (e.Action == HotkeyAction.ToggleCameraSweep)
        {
            Toggle();
        }
    }

    private void Toggle()
    {
        var started = false;
        var stopped = false;

        lock (_stateLock)
        {
            if (_isDisposed)
            {
                return;
            }

            if (_sweepCancellationTokenSource is not null)
            {
                _ = StopCore();
                stopped = true;
            }
            else
            {
                var cancellationTokenSource = new CancellationTokenSource();
                _sweepCancellationTokenSource = cancellationTokenSource;
                _sweepTask = Task.Run(() => RunSweepAsync(cancellationTokenSource));
                _isEnabled = true;
                started = true;
            }
        }

        if (stopped)
        {
            _logger.LogInformation("视角巡航已关闭");
        }
        else if (started)
        {
            _logger.LogInformation("视角巡航已开启");
        }
    }

    private Task? StopCore()
    {
        var cancellationTokenSource = _sweepCancellationTokenSource;
        if (cancellationTokenSource is null)
        {
            return null;
        }

        var sweepTask = _sweepTask;
        cancellationTokenSource.Cancel();
        _sweepCancellationTokenSource = null;
        _sweepTask = null;
        _isEnabled = false;
        return sweepTask;
    }

    private async Task RunSweepAsync(CancellationTokenSource ownerCancellationTokenSource)
    {
        var cancellationToken = ownerCancellationTokenSource.Token;

        try
        {
            var targetWindow = _gameWindowService.FindGameWindow();

            _ = GetMouseHook();

            var direction = (int)Settings.InitialDirection;
            var directionStartedAt = Stopwatch.GetTimestamp();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (targetWindow is null
                    || !_keyboardInputService.IsWindowAvailable(targetWindow.Hwnd)
                    || !_keyboardInputService.IsWindowForeground(targetWindow.Hwnd))
                {
                    targetWindow = _gameWindowService.FindGameWindow();
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                    directionStartedAt = Stopwatch.GetTimestamp();
                    continue;
                }

                var settings = Settings;
                if (Stopwatch.GetElapsedTime(directionStartedAt)
                    >= TimeSpan.FromSeconds(settings.DirectionDurationSeconds))
                {
                    direction = -direction;
                    directionStartedAt = Stopwatch.GetTimestamp();
                }

                if (!MoveCamera(direction * settings.PixelsPerTick))
                {
                    throw new InvalidOperationException("Interception 无法发送鼠标位移，视角巡航已停止。");
                }

                await Task.Delay(settings.MovementIntervalMs, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "视角巡航运行失败：{Message}", ex.Message);
            ShowFailureNotification(ex.Message);
        }
        finally
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_sweepCancellationTokenSource, ownerCancellationTokenSource))
                {
                    _sweepCancellationTokenSource = null;
                    _sweepTask = null;
                    _isEnabled = false;
                }
            }

            ownerCancellationTokenSource.Dispose();
        }
    }

    private bool MoveCamera(int horizontalMovement)
    {
        lock (InterceptionSynchronization.Gate)
        {
            return GetMouseHook().MoveCursorBy(horizontalMovement, 0, useWinAPI: false);
        }
    }

    private InterceptionMouseHook GetMouseHook()
    {
        lock (InterceptionSynchronization.Gate)
        {
            if (_mouseHook?.CanSimulateInput == true)
            {
                return _mouseHook;
            }

            _mouseHook?.Dispose();
            _mouseHook = null;

            if (!InterceptionInput.CheckDriverInstalled())
            {
                throw new InvalidOperationException(
                    "Interception 驱动未安装，无法启动视角巡航。");
            }

            if (InterceptionInput.Disposed && !InterceptionInput.Initialize())
            {
                throw new InvalidOperationException(
                    "Interception 初始化失败，请重启 RocoPilot 后重试。");
            }

            var mouseHook = new InterceptionMouseHook(InterceptionMouseFilter.None);
            if (!mouseHook.CanSimulateInput)
            {
                mouseHook.Dispose();
                throw new InvalidOperationException(
                    "Interception 鼠标设备不可用。");
            }

            _mouseHook = mouseHook;
            return mouseHook;
        }
    }

    private void ShowFailureNotification(string message)
    {
        try
        {
            var escapedMessage = SecurityElement.Escape(message) ?? "未知错误";
            _ = _appNotificationService.Show(
                $"<toast><visual><binding template='ToastGeneric'><text>视角巡航错误</text><text>{escapedMessage}</text></binding></visual></toast>");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "显示巡航错误通知失败");
        }
    }
}
