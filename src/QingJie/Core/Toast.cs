using System.Windows;
using System.Windows.Media.Animation;

namespace QingJie.Core;

/// <summary>轻提示（右下角浮层，自动淡出）。</summary>
internal static class Toast
{
    private static ToastWindow? _window;

    public static void Show(string message)
    {
        try
        {
            _window?.Close();
            _window = new ToastWindow(message);
            _window.Closed += (_, _) => _window = null;
            _window.Show();
        }
        catch (Exception ex)
        {
            Logger.Warn($"显示提示失败：{ex.Message}");
        }
    }
}

public partial class ToastWindow : Window
{
    public ToastWindow(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
        Opacity = 0;

        Loaded += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            Left = work.Left + (work.Width - ActualWidth) / 2;
            Top = work.Bottom - ActualHeight - 90;

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140));
            BeginAnimation(OpacityProperty, fadeIn);

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500),
            };

            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320));
                fadeOut.Completed += (_, _) => Close();
                BeginAnimation(OpacityProperty, fadeOut);
            };

            timer.Start();
        };
    }
}
