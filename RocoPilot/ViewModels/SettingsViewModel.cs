using System.Diagnostics;
using System.IO;
using System.Reflection;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RocoPilot.Contracts.Services;
using RocoPilot.Helpers;
using RocoPilot.Models;
using RocoPilot.Services;
using RocoPilot.Settings;

using Windows.ApplicationModel;

namespace RocoPilot.ViewModels;

public partial class SettingsViewModel : ObservableRecipient
{
    private readonly IThemeSelectorService _themeSelectorService;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly IUpdateService _updateService;
    private readonly CameraSweepService _cameraSweepService;
    private readonly ILogger<SettingsViewModel> _logger;

    private bool _suppressThemeChange;
    private bool _suppressCameraSweepSettingsChange;

    public ThemeOption[] ThemeOptions { get; } =
    {
        new ThemeOption { ThemeKey = "System", Name = "跟随系统" },
        new ThemeOption { ThemeKey = "Light", Name = "浅色" },
        new ThemeOption { ThemeKey = "Dark", Name = "深色" },
    };

    [ObservableProperty]
    public partial ThemeOption? SelectedThemeOption { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateCheckEnabled))]
    [NotifyPropertyChangedFor(nameof(UpdateButtonText))]
    public partial bool IsCheckingUpdate { get; set; }

    [ObservableProperty]
    public partial string UpdateStatusText { get; set; }

    [ObservableProperty]
    public partial double CameraSweepPixelsPerTick { get; set; } = CameraSweepSettings.DefaultPixelsPerTick;

    [ObservableProperty]
    public partial double CameraSweepMovementIntervalMs { get; set; } = CameraSweepSettings.DefaultMovementIntervalMs;

    [ObservableProperty]
    public partial double CameraSweepDirectionDurationSeconds { get; set; } = CameraSweepSettings.DefaultDirectionDurationSeconds;

    [ObservableProperty]
    public partial bool CameraSweepStartsRight { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCameraSweepSaveEnabled))]
    public partial bool IsSavingCameraSweepSettings { get; set; }

    [ObservableProperty]
    public partial string CameraSweepStatusText { get; set; } = "使用默认巡航参数";

    public bool IsUpdateCheckEnabled => !IsCheckingUpdate;

    public bool IsCameraSweepSaveEnabled => !IsSavingCameraSweepSettings;

    public string UpdateButtonText => IsCheckingUpdate ? "正在检查" : "检查更新";

    public string AppVersion { get; }

    public SettingsViewModel(
        IThemeSelectorService themeSelectorService,
        ILocalSettingsService localSettingsService,
        IUpdateService updateService,
        CameraSweepService cameraSweepService,
        ILogger<SettingsViewModel> logger)
    {
        _themeSelectorService = themeSelectorService;
        _localSettingsService = localSettingsService;
        _updateService = updateService;
        _cameraSweepService = cameraSweepService;
        _logger = logger;
        AppVersion = GetAppVersionText();
        UpdateStatusText = "从 GitHub Releases 获取最新版本信息";
    }

    public async Task LoadAsync()
    {
        _suppressThemeChange = true;
        try
        {
            var key = KeyFromElementTheme(_themeSelectorService.Theme);
            SelectedThemeOption = ThemeOptions.FirstOrDefault(t => t.ThemeKey == key) ?? ThemeOptions[0];

            await _cameraSweepService.LoadSettingsAsync();
            ApplyCameraSweepSettings(_cameraSweepService.Settings);
        }
        finally
        {
            _suppressThemeChange = false;
        }
    }

    partial void OnSelectedThemeOptionChanged(ThemeOption? value)
    {
        if (_suppressThemeChange || value == null)
        {
            return;
        }

        _ = ApplyThemeAsync(value);
    }

    private async Task ApplyThemeAsync(ThemeOption option)
    {
        await _themeSelectorService.SetThemeAsync(ElementThemeFromKey(option.ThemeKey));
    }

    [RelayCommand]
    private async Task SaveCameraSweepSettingsAsync()
    {
        if (IsSavingCameraSweepSettings)
        {
            return;
        }

        IsSavingCameraSweepSettings = true;
        try
        {
            await _cameraSweepService.SaveSettingsAsync(BuildCameraSweepSettings());
            ApplyCameraSweepSettings(_cameraSweepService.Settings);
            CameraSweepStatusText = "巡航参数已保存";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存视角巡航设置失败");
            CameraSweepStatusText = "保存失败，请稍后重试";
        }
        finally
        {
            IsSavingCameraSweepSettings = false;
        }
    }

    private CameraSweepSettings BuildCameraSweepSettings()
    {
        var current = _cameraSweepService.Settings;
        return CameraSweepSettings.Normalize(new CameraSweepSettings
        {
            PixelsPerTick = ToInt(
                CameraSweepPixelsPerTick,
                current.PixelsPerTick),
            MovementIntervalMs = ToInt(
                CameraSweepMovementIntervalMs,
                current.MovementIntervalMs),
            DirectionDurationSeconds = ToInt(
                CameraSweepDirectionDurationSeconds,
                current.DirectionDurationSeconds),
            InitialDirection = CameraSweepStartsRight
                ? CameraSweepDirection.Right
                : CameraSweepDirection.Left
        });
    }

    private void ApplyCameraSweepSettings(CameraSweepSettings settings)
    {
        _suppressCameraSweepSettingsChange = true;
        try
        {
            CameraSweepPixelsPerTick = settings.PixelsPerTick;
            CameraSweepMovementIntervalMs = settings.MovementIntervalMs;
            CameraSweepDirectionDurationSeconds = settings.DirectionDurationSeconds;
            CameraSweepStartsRight = settings.InitialDirection == CameraSweepDirection.Right;
        }
        finally
        {
            _suppressCameraSweepSettingsChange = false;
        }
    }

    private static int ToInt(double value, int fallback)
    {
        return double.IsFinite(value) ? (int)Math.Round(value) : fallback;
    }

    partial void OnCameraSweepPixelsPerTickChanged(double value) => MarkCameraSweepSettingsDirty();

    partial void OnCameraSweepMovementIntervalMsChanged(double value) => MarkCameraSweepSettingsDirty();

    partial void OnCameraSweepDirectionDurationSecondsChanged(double value) => MarkCameraSweepSettingsDirty();

    partial void OnCameraSweepStartsRightChanged(bool value) => MarkCameraSweepSettingsDirty();

    private void MarkCameraSweepSettingsDirty()
    {
        if (!_suppressCameraSweepSettingsChange && !IsSavingCameraSweepSettings)
        {
            CameraSweepStatusText = "参数已修改，点击保存后生效";
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(LoggingHelper.LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = LoggingHelper.LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开日志目录失败");
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingUpdate)
        {
            return;
        }

        IsCheckingUpdate = true;
        UpdateStatusText = "正在连接 GitHub Releases...";

        try
        {
            var result = await _updateService.CheckUpdateAsync(new UpdateOption { Trigger = UpdateTrigger.Manual });
            UpdateStatusText = BuildUpdateStatusText(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检查更新失败");
            UpdateStatusText = "检查更新失败，请检查网络连接后重试。";
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    [RelayCommand]
    private async Task ResetSettingsAsync()
    {
        var xamlRoot = (App.MainWindow.Content as FrameworkElement)?.XamlRoot;
        if (xamlRoot == null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "重置设置",
            Content = "将清除所有用户偏好，并立即关闭应用。是否继续？",
            PrimaryButtonText = "重置并退出",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        await _localSettingsService.ResetAllAsync();
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    private static ElementTheme ElementThemeFromKey(string themeKey) => themeKey switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private static string KeyFromElementTheme(ElementTheme theme) => theme switch
    {
        ElementTheme.Light => "Light",
        ElementTheme.Dark => "Dark",
        _ => "System",
    };

    private static string BuildUpdateStatusText(UpdateCheckResult result)
    {
        var checkedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        return result.Status switch
        {
            UpdateCheckStatus.UpToDate => $"当前已是最新版本 · 上次检查：{checkedAt}",
            UpdateCheckStatus.UpdateAvailable when !string.IsNullOrWhiteSpace(result.Message) =>
                $"{result.Message} · 上次检查：{checkedAt}",
            UpdateCheckStatus.UpdateAvailable when result.Release != null =>
                $"发现新版本 {result.Release.TagName} · 上次检查：{checkedAt}",
            UpdateCheckStatus.Failed when !string.IsNullOrWhiteSpace(result.Message) =>
                $"{result.Message} · 上次检查：{checkedAt}",
            _ => $"上次检查：{checkedAt}",
        };
    }

    private static string GetAppVersionText()
    {
        Version version;

        if (RuntimeHelper.IsMSIX)
        {
            var packageVersion = Package.Current.Id.Version;

            version = new(packageVersion.Major, packageVersion.Minor, packageVersion.Build, packageVersion.Revision);
        }
        else
        {
            version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
        }

        return FormatAppVersion(version);
    }

    internal static string FormatAppVersion(Version version)
    {
        return $"v{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}.{Math.Max(0, version.Revision)}";
    }
}
