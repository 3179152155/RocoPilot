using System.Runtime.InteropServices;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

using RocoPilot.Helpers;
using RocoPilot.Models.Capture;
using RocoPilot.Models.Overlay;

using Windows.Graphics;
using Windows.UI;

namespace RocoPilot.Views.Windows;

public sealed partial class InfoOverlayWindow : WindowEx
{
    private const double CompactIslandHeight = 32d;
    private const double DetailedIslandHeight = 44d;
    private const string StatusDetailSeparator = " - ";
    private const int OverlayWidth = 400;
    private const int OverlayHeight = 48;
    private const int MinOverlayWidth = 156;
    private const int MinOverlayHeight = 36;
    private const int DefaultMargin = 0;

    private static readonly TimeSpan FollowInterval = TimeSpan.FromMilliseconds(250);
    private static readonly Color ActiveTaskIndicatorForeground = Color.FromArgb(0xFF, 0x34, 0xD3, 0x99);
    private static readonly Color DisabledIndicatorForeground = Color.FromArgb(0xFF, 0x8B, 0x95, 0xA1);

    private readonly CaptureTargetWindow _targetWindow;
    private readonly DispatcherQueueTimer _followTimer;
    private readonly IntPtr _hwnd;

    private IDisposable? _messageHook;
    private RectInt32 _currentClientBounds;
    private RectInt32 _currentOverlayBounds;
    private WindowPoint _dragStartCursorPosition;
    private RectInt32 _dragStartOverlayBounds;
    private int _overlayOffsetX;
    private int _overlayOffsetY;
    private DateTimeOffset? _lastCounterUpdate;
    private Storyboard? _stateDotPulse;
    private Storyboard? _statusTransition;
    private Storyboard? _islandMorph;
    private Storyboard? _islandBulge;
    private string? _lastStatusText;
    private bool _hasStatusDetail;
    private bool _hasActivated;
    private bool _hasUserPositioned;
    private bool _isLocked;
    private bool _isClosed;
    private bool _isDragging;
    private bool _isOverlayVisible;

    public InfoOverlayWindow(
        CaptureTargetWindow targetWindow,
        bool isLocked,
        bool isEncounterStatisticsEnabled,
        bool isAutoBattleEnabled)
    {
        _targetWindow = targetWindow;
        _isLocked = isLocked;

        InitializeComponent();
        UpdateTaskIndicators(isEncounterStatisticsEnabled, isAutoBattleEnabled);

        SystemBackdrop = new TransparentTintBackdrop(Color.FromArgb(0, 0, 0, 0));
        Title = "RocoPilot Info Overlay";
        AppWindow.Title = Title;
        ConfigurePresenter();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ApplyLockState(show: false);

        _followTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _followTimer.Interval = FollowInterval;
        _followTimer.Tick += FollowTimer_Tick;

        Closed += InfoOverlayWindow_Closed;
    }

    public void ShowOverlay()
    {
        if (_isClosed)
        {
            return;
        }

        if (UpdateOverlayState(forceMove: true))
        {
            ApplyLockState();
        }

        _followTimer.Start();
    }

    public void ResetPosition()
    {
        _hasUserPositioned = false;
        UpdateOverlayState(forceMove: true);
    }

    public void SetLocked(bool isLocked)
    {
        if (_isClosed || _isLocked == isLocked)
        {
            return;
        }

        _isLocked = isLocked;

        if (_isDragging)
        {
            _isDragging = false;
            OverlayRoot.ReleasePointerCaptures();
        }

        ApplyLockState(show: _isOverlayVisible);
        UpdateOverlayState(forceMove: true);
    }

    public void UpdateSnapshot(InfoOverlaySnapshot snapshot)
    {
        if (snapshot.PendingShinyCapture is null)
        {
            PendingShinyAlert.Visibility = Visibility.Collapsed;
            PendingShinyAlertText.Text = string.Empty;
        }
        else
        {
            PendingShinyAlert.Visibility = Visibility.Visible;
            PendingShinyAlertText.Text = $"{snapshot.PendingShinyCapture.CreatureName} · 等待统计页面确认";
        }

        var statusText = string.IsNullOrWhiteSpace(snapshot.StatusText)
            ? "状态待识别"
            : snapshot.StatusText;

        if (!string.Equals(_lastStatusText, statusText, StringComparison.Ordinal))
        {
            _lastStatusText = statusText;
            var (primaryText, detailText) = SplitStatusText(statusText);
            StatusPrimaryText.Text = primaryText;
            StatusDetailText.Text = detailText ?? string.Empty;
            StatusDetailText.Visibility = detailText is null
                ? Visibility.Collapsed
                : Visibility.Visible;
            MorphIsland(detailText is not null);
            AnimateStatusIn();
        }

        DateTimeOffset? latestCounterUpdate = snapshot.Counters.Count == 0
            ? null
            : snapshot.Counters.Max(counter => counter.LastCountedAt);
        if (_lastCounterUpdate.HasValue
            && latestCounterUpdate.HasValue
            && latestCounterUpdate.Value > _lastCounterUpdate.Value)
        {
            AnimateIslandBulge();
        }

        _lastCounterUpdate = latestCounterUpdate;
    }

    public void UpdateTaskIndicators(bool isEncounterStatisticsEnabled, bool isAutoBattleEnabled)
    {
        var isActive = isEncounterStatisticsEnabled || isAutoBattleEnabled;
        StateDot.Fill = new SolidColorBrush(isActive
            ? ActiveTaskIndicatorForeground
            : DisabledIndicatorForeground);
        SyncStateDotPulse(isActive);
    }

    private void ConfigurePresenter()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            presenter = OverlappedPresenter.Create();
            AppWindow.SetPresenter(presenter);
        }

        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.IsShownInSwitchers = false;
    }

    private void ApplyLockState(bool show = true)
    {
        if (_isLocked && _messageHook is null)
        {
            _messageHook = TransparentOverlayWindowHelper.InstallMessageHook(_hwnd);
        }
        else if (!_isLocked && _messageHook is not null)
        {
            _messageHook.Dispose();
            _messageHook = null;
        }

        TransparentOverlayWindowHelper.ApplyTransparentOverlayStyles(_hwnd, topMost: true, passThrough: _isLocked, show);
    }

    private void FollowTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!_isDragging)
        {
            UpdateOverlayState();
        }
    }

    private bool UpdateOverlayState(bool forceMove = false)
    {
        if (_isClosed
            || !TransparentOverlayWindowHelper.IsForegroundWindow(_targetWindow.Hwnd)
            || !TransparentOverlayWindowHelper.TryGetClientScreenBounds(_targetWindow.Hwnd, out var clientBounds)
            || clientBounds.Width <= 0
            || clientBounds.Height <= 0)
        {
            HideOverlay();
            return false;
        }

        _currentClientBounds = clientBounds;
        var overlaySize = GetOverlayPixelSize(clientBounds);
        var nextBounds = _hasUserPositioned
            ? ClampToClient(
                clientBounds,
                clientBounds.X + _overlayOffsetX,
                clientBounds.Y + _overlayOffsetY,
                overlaySize.Width,
                overlaySize.Height)
            : GetDefaultOverlayBounds(clientBounds, overlaySize);

        if (!_hasActivated)
        {
            AppWindow.MoveAndResize(nextBounds);
            Activate();
            _hasActivated = true;
            TransparentOverlayWindowHelper.ApplyTransparentOverlayStyles(_hwnd, topMost: true, passThrough: _isLocked);
        }

        if (forceMove || !SameBounds(_currentOverlayBounds, nextBounds))
        {
            AppWindow.MoveAndResize(nextBounds);
            _currentOverlayBounds = nextBounds;
        }

        TransparentOverlayWindowHelper.MoveTopMostNoActivate(_hwnd, nextBounds);
        _currentOverlayBounds = nextBounds;
        _isOverlayVisible = true;
        return true;
    }

    private SizeInt32 GetOverlayPixelSize(RectInt32 clientBounds)
    {
        var rasterizationScale = OverlayRoot.XamlRoot?.RasterizationScale ?? 1d;
        if (rasterizationScale <= 0)
        {
            rasterizationScale = 1d;
        }

        var width = (int)Math.Ceiling(OverlayWidth * rasterizationScale);
        var height = (int)Math.Ceiling(OverlayHeight * rasterizationScale);
        var availableWidth = Math.Max(120, clientBounds.Width - DefaultMargin * 2);
        var availableHeight = Math.Max(120, clientBounds.Height - DefaultMargin * 2);

        width = Math.Min(width, Math.Max(Math.Min(MinOverlayWidth, availableWidth), availableWidth));
        height = Math.Min(height, Math.Max(Math.Min(MinOverlayHeight, availableHeight), availableHeight));

        return new SizeInt32(width, height);
    }

    private static RectInt32 GetDefaultOverlayBounds(RectInt32 clientBounds, SizeInt32 overlaySize)
    {
        var x = clientBounds.X + (clientBounds.Width - overlaySize.Width) / 2;
        var y = clientBounds.Y + DefaultMargin;
        return ClampToClient(clientBounds, x, y, overlaySize.Width, overlaySize.Height);
    }

    private static RectInt32 ClampToClient(RectInt32 clientBounds, int x, int y, int width, int height)
    {
        var maxX = clientBounds.X + Math.Max(0, clientBounds.Width - width);
        var maxY = clientBounds.Y + Math.Max(0, clientBounds.Height - height);
        var clampedX = Math.Clamp(x, clientBounds.X, maxX);
        var clampedY = Math.Clamp(y, clientBounds.Y, maxY);
        return new RectInt32(clampedX, clampedY, width, height);
    }

    private void HideOverlay()
    {
        if (!_isOverlayVisible)
        {
            return;
        }

        TransparentOverlayWindowHelper.HideWindow(_hwnd);
        _isOverlayVisible = false;
    }

    private void AnimateStatusIn()
    {
        _statusTransition?.Stop();

        var translateAnimation = new DoubleAnimation
        {
            From = 4,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(translateAnimation, StatusShift);
        Storyboard.SetTargetProperty(translateAnimation, nameof(TranslateTransform.Y));

        var fadeAnimation = new DoubleAnimation
        {
            From = 0.25,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fadeAnimation, StatusContent);
        Storyboard.SetTargetProperty(fadeAnimation, nameof(UIElement.Opacity));

        _statusTransition = new Storyboard();
        _statusTransition.Children.Add(translateAnimation);
        _statusTransition.Children.Add(fadeAnimation);
        _statusTransition.Begin();
    }

    private static (string PrimaryText, string? DetailText) SplitStatusText(string statusText)
    {
        var separatorIndex = statusText.IndexOf(StatusDetailSeparator, StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            return (statusText, null);
        }

        var primaryText = statusText[..separatorIndex].Trim();
        var detailText = statusText[(separatorIndex + StatusDetailSeparator.Length)..].Trim();
        return string.IsNullOrWhiteSpace(detailText)
            ? (primaryText, null)
            : (primaryText, detailText);
    }

    private void MorphIsland(bool hasDetail)
    {
        if (_hasStatusDetail == hasDetail)
        {
            return;
        }

        _hasStatusDetail = hasDetail;
        var currentHeight = InfoPanel.ActualHeight > 0
            ? InfoPanel.ActualHeight
            : InfoPanel.Height;
        _islandMorph?.Stop();

        var targetHeight = hasDetail ? DetailedIslandHeight : CompactIslandHeight;
        InfoPanel.Height = targetHeight;
        var heightAnimation = new DoubleAnimation
        {
            From = currentHeight,
            To = targetHeight,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new BackEase
            {
                Amplitude = 0.25,
                EasingMode = EasingMode.EaseOut
            },
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(heightAnimation, InfoPanel);
        Storyboard.SetTargetProperty(heightAnimation, nameof(FrameworkElement.Height));

        InfoPanel.CornerRadius = new CornerRadius(hasDetail ? 22 : 16);
        _islandMorph = new Storyboard();
        _islandMorph.Children.Add(heightAnimation);
        _islandMorph.Begin();
    }

    private void SyncStateDotPulse(bool isActive)
    {
        if (!isActive)
        {
            _stateDotPulse?.Stop();
            _stateDotPulse = null;
            StateDot.Opacity = 1;
            return;
        }

        if (_stateDotPulse is not null)
        {
            return;
        }

        var pulseAnimation = new DoubleAnimation
        {
            From = 1,
            To = 0.3,
            Duration = new Duration(TimeSpan.FromMilliseconds(550)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(pulseAnimation, StateDot);
        Storyboard.SetTargetProperty(pulseAnimation, nameof(UIElement.Opacity));

        _stateDotPulse = new Storyboard();
        _stateDotPulse.Children.Add(pulseAnimation);
        _stateDotPulse.Begin();
    }

    private void AnimateIslandBulge()
    {
        _islandBulge?.Stop();

        var scaleXAnimation = CreateBulgeAnimation(1.035);
        Storyboard.SetTarget(scaleXAnimation, IslandScale);
        Storyboard.SetTargetProperty(scaleXAnimation, nameof(ScaleTransform.ScaleX));

        var scaleYAnimation = CreateBulgeAnimation(1.12);
        Storyboard.SetTarget(scaleYAnimation, IslandScale);
        Storyboard.SetTargetProperty(scaleYAnimation, nameof(ScaleTransform.ScaleY));

        _islandBulge = new Storyboard();
        _islandBulge.Children.Add(scaleXAnimation);
        _islandBulge.Children.Add(scaleYAnimation);
        _islandBulge.Begin();
    }

    private static DoubleAnimationUsingKeyFrames CreateBulgeAnimation(double peak)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            Value = 1,
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero)
        });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            Value = peak,
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            Value = 1,
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(330)),
            EasingFunction = new BackEase
            {
                Amplitude = 0.4,
                EasingMode = EasingMode.EaseOut
            }
        });
        return animation;
    }

    private void OverlayRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isLocked || _isClosed || !GetCursorPos(out _dragStartCursorPosition))
        {
            return;
        }

        _dragStartOverlayBounds = _currentOverlayBounds;
        _isDragging = true;
        OverlayRoot.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OverlayRoot_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging || _isLocked || _isClosed || !GetCursorPos(out var cursorPosition))
        {
            return;
        }

        var x = _dragStartOverlayBounds.X + cursorPosition.X - _dragStartCursorPosition.X;
        var y = _dragStartOverlayBounds.Y + cursorPosition.Y - _dragStartCursorPosition.Y;
        var nextBounds = ClampToClient(
            _currentClientBounds,
            x,
            y,
            _dragStartOverlayBounds.Width,
            _dragStartOverlayBounds.Height);

        _hasUserPositioned = true;
        _overlayOffsetX = nextBounds.X - _currentClientBounds.X;
        _overlayOffsetY = nextBounds.Y - _currentClientBounds.Y;
        _currentOverlayBounds = nextBounds;

        AppWindow.MoveAndResize(nextBounds);
        TransparentOverlayWindowHelper.MoveTopMostNoActivate(_hwnd, nextBounds);
        e.Handled = true;
    }

    private void OverlayRoot_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        OverlayRoot.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void InfoOverlayWindow_Closed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        _followTimer.Stop();
        _stateDotPulse?.Stop();
        _statusTransition?.Stop();
        _islandMorph?.Stop();
        _islandBulge?.Stop();
        _messageHook?.Dispose();
    }

    private static bool SameBounds(RectInt32 left, RectInt32 right)
    {
        return left.X == right.X
            && left.Y == right.Y
            && left.Width == right.Width
            && left.Height == right.Height;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out WindowPoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPoint
    {
        public int X;
        public int Y;
    }
}
