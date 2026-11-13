using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using QingJie.Core;

namespace QingJie.Views;

public partial class SettingsWindow : Window
{
    private readonly Settings _config;
    private Hotkey _hotkey;

    public SettingsWindow(Settings config)
    {
        InitializeComponent();

        _config = config;
        _hotkey = config.Hotkey ?? Hotkey.Default;

        HotkeyBox.Text = _hotkey.ToString();
        SaveDirBox.Text = config.SaveDirectory;
        CopyCheck.IsChecked = config.CopyToClipboard;
        AskPathCheck.IsChecked = config.AskSavePath;
        MagnifierCheck.IsChecked = config.ShowMagnifier;
        CloseAfterCopyCheck.IsChecked = config.CloseAfterCopy;
        BallCheck.IsChecked = config.ShowFloatingBall;
        StartupCheck.IsChecked = config.StartWithWindows;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"轻截 v{version?.Major}.{version?.Minor}.{version?.Build}";
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        HotkeyBox.SelectAll();
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
        {
            return;
        }

        if (key is Key.Back or Key.Delete)
        {
            _hotkey = new Hotkey();
            HotkeyBox.Text = "点击后按下快捷键";
            HintText.Text = "快捷键不能为空。";
            return;
        }

        var candidate = Hotkey.FromKey(key, Keyboard.Modifiers);
        if (!candidate.IsValid)
        {
            HintText.Text = "请至少使用一个修饰键（Ctrl / Alt / Shift / Win）。";
            return;
        }

        _hotkey = candidate;
        HotkeyBox.Text = candidate.ToString();
        HintText.Text = "提示：滚轮缩放贴图，Ctrl + 滚轮调整透明度，Esc 关闭贴图。";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择截图保存目录",
            InitialDirectory = Directory.Exists(SaveDirBox.Text) ? SaveDirBox.Text : _config.SaveDirectory,
        };

        if (dialog.ShowDialog() == true)
        {
            SaveDirBox.Text = dialog.FolderName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_hotkey.IsValid)
        {
            HintText.Text = "请先设置一个有效的截图快捷键。";
            return;
        }

        _config.Hotkey = _hotkey;
        _config.SaveDirectory = string.IsNullOrWhiteSpace(SaveDirBox.Text)
            ? new Settings().SaveDirectory
            : SaveDirBox.Text.Trim();
        _config.CopyToClipboard = CopyCheck.IsChecked == true;
        _config.AskSavePath = AskPathCheck.IsChecked == true;
        _config.ShowMagnifier = MagnifierCheck.IsChecked == true;
        _config.CloseAfterCopy = CloseAfterCopyCheck.IsChecked == true;
        _config.ShowFloatingBall = BallCheck.IsChecked == true;
        _config.StartWithWindows = StartupCheck.IsChecked == true;

        _config.Save();
        App.CurrentInstance.ApplySettings(_config);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
