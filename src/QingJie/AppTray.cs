using System.Drawing;
using System.Windows.Forms;
using QingJie.Core;

namespace QingJie;

/// <summary>托盘图标与托盘菜单。</summary>
internal sealed class AppTray : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _captureItem;
    private readonly ToolStripMenuItem _ballItem;

    public AppTray(App app, Settings config)
    {
        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = new Font("Microsoft YaHei UI", 9f),
        };

        _captureItem = new ToolStripMenuItem($"开始截图（{config.Hotkey}）")
        {
            Font = new Font(menu.Font, System.Drawing.FontStyle.Bold),
        };
        _captureItem.Click += (_, _) => app.CaptureNow();

        var settingsItem = new ToolStripMenuItem("设置…");
        settingsItem.Click += (_, _) => app.OpenSettings();

        _ballItem = new ToolStripMenuItem("显示桌面悬浮球")
        {
            CheckOnClick = false,
            Checked = config.ShowFloatingBall,
        };
        _ballItem.Click += (_, _) => app.ToggleBall();

        var folderItem = new ToolStripMenuItem("打开截图目录");
        folderItem.Click += (_, _) => app.OpenSaveDirectory();

        var exitItem = new ToolStripMenuItem("退出轻截");
        exitItem.Click += (_, _) => app.ExitApp();

        menu.Items.Add(_captureItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_ballItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(folderItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "轻截 · 截图工具",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) app.CaptureNow();
        };

        _icon.DoubleClick += (_, _) => app.CaptureNow();
    }

    public void RefreshHotkeyText(Hotkey hotkey) => _captureItem.Text = $"开始截图（{hotkey}）";

    /// <summary>同步托盘里「显示桌面悬浮球」的勾选状态。</summary>
    public void RefreshBallItem(bool visible) => _ballItem.Checked = visible;

    public void ShowBalloon(string title, string message)
    {
        try
        {
            _icon.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Logger.Warn($"气泡提示失败：{ex.Message}");
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"读取程序图标失败：{ex.Message}");
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
