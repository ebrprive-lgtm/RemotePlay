using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace RemotePlay;

/// <summary>
/// A transparent overlay window that renders on top of LibVLC video using Win32 layering.
/// </summary>
public class VideoTextOverlay : IDisposable
{
    private Window? _overlayWindow;
    private TextBlock? _textBlock;
    private Border? _banner;
    private bool _isDisposed;

    public void Initialize(Window parentWindow)
    {
        if (_overlayWindow != null)
            return;

        _overlayWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            IsHitTestVisible = false,
            Focusable = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            // Don't set initial size - let UpdatePosition set it
            Visibility = Visibility.Hidden  // Start hidden
            // Owner not set - overlay is independent and topmost
        };

        // Apply WS_EX_TOOLWINDOW to hide from Task Manager and Alt+Tab
        _overlayWindow.SourceInitialized += (s, e) =>
        {
            var hwnd = new WindowInteropHelper(_overlayWindow).Handle;
            if (hwnd != IntPtr.Zero)
            {
                const int GWL_EXSTYLE = -20;
                const int WS_EX_TOOLWINDOW = 0x00000080;

                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
            }
        };

        var grid = new Grid
        {
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
        };

        _banner = new Border
        {
            VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(180, 0, 0, 0)),
            Padding = new Thickness(32, 14, 32, 14),
            Margin = new Thickness(0, 0, 0, 60),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(200,200,200,200)),
            BorderThickness= new Thickness(2.0),
            CornerRadius = new CornerRadius(8),
            Opacity = 1
        };

        _textBlock = new TextBlock
        {
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Effect = new DropShadowEffect
            {
                Color = System.Windows.Media.Colors.Black,
                BlurRadius = 10,
                ShadowDepth = 3,
                Opacity = 1
            }
        };

        _banner.Child = _textBlock;
        grid.Children.Add(_banner);
        _overlayWindow.Content = grid;
    }

    public void ShowText(string text)
    {
        if (_textBlock != null && _banner != null && _overlayWindow != null)
        {
            _textBlock.Text = text;

            // Ensure the window is shown first
            if (!_overlayWindow.IsVisible)
            {
                _overlayWindow.Show();
            }

            // Force on top
            BringToTop();

            // Animate the banner in with a smooth fade
            _banner.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(300))
                {
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase()
                });
        }
    }

    public void HideText()
    {
        if (_banner != null)
        {
            // Animate the banner out with a smooth fade
            _banner.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(500))
                {
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase()
                });
        }
    }

    public void UpdatePosition(Window parentWindow)
    {
        if (_overlayWindow != null && parentWindow != null)
        {
            // Get the parent window's handle
            var parentHwnd = new WindowInteropHelper(parentWindow).Handle;
            if (parentHwnd == IntPtr.Zero)
                return;

            // Get the parent window's physical screen bounds using Win32
            if (GetWindowRect(parentHwnd, out RECT parentRect))
            {
                // Ensure overlay window has a handle
                if (!_overlayWindow.IsLoaded)
                {
                    _overlayWindow.Show();
                    _overlayWindow.Hide();
                }

                var overlayHwnd = new WindowInteropHelper(_overlayWindow).Handle;
                if (overlayHwnd != IntPtr.Zero)
                {
                    // Position overlay using the same physical pixel coordinates as the parent
                    int left = parentRect.Left;
                    int top = parentRect.Top;
                    int width = parentRect.Right - parentRect.Left;
                    int height = parentRect.Bottom - parentRect.Top;

                    // Use SetWindowPos with physical coordinates
                    SetWindowPos(overlayHwnd, IntPtr.Zero, left, top, width, height,
                        0x0004 | 0x0010); // SWP_NOZORDER | SWP_NOACTIVATE

                    // Force layout update
                    _overlayWindow.UpdateLayout();
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    public void Show()
    {
        if (_overlayWindow != null)
        {
            _overlayWindow.Visibility = Visibility.Visible;
            if (!_overlayWindow.IsVisible)
                _overlayWindow.Show();

            // Force the overlay to stay on top using Win32
            BringToTop();
        }
    }

    public void Hide()
    {
        if (_overlayWindow != null)
        {
            _overlayWindow.Visibility = Visibility.Hidden;
        }
    }

    private void BringToTop()
    {
        if (_overlayWindow != null && _overlayWindow.IsLoaded)
        {
            var hwnd = new WindowInteropHelper(_overlayWindow).Handle;
            if (hwnd != IntPtr.Zero)
            {
                // HWND_TOPMOST = -1, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE
                SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _overlayWindow?.Close();
            _overlayWindow =null;
            _isDisposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
