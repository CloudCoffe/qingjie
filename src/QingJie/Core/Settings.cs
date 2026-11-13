using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace QingJie.Core;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>全局快捷键（修饰键 + 虚拟键码）。</summary>
public sealed class Hotkey : IEquatable<Hotkey>
{
    public HotkeyModifiers Modifiers { get; set; }
    public uint VirtualKey { get; set; }

    public Hotkey() { }

    public Hotkey(HotkeyModifiers modifiers, uint virtualKey)
    {
        Modifiers = modifiers;
        VirtualKey = virtualKey;
    }

    [JsonIgnore]
    public bool IsValid => VirtualKey != 0 && Modifiers != HotkeyModifiers.None;

    public static Hotkey Default => new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x41 /* A */);

    public static Hotkey FromKey(Key key, ModifierKeys modifiers)
    {
        var mods = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;
        return new Hotkey(mods, (uint)KeyInterop.VirtualKeyFromKey(key));
    }

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = new Hotkey();
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        var mods = HotkeyModifiers.None;
        Key key = Key.None;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= HotkeyModifiers.Control;
                    break;
                case "alt":
                    mods |= HotkeyModifiers.Alt;
                    break;
                case "shift":
                    mods |= HotkeyModifiers.Shift;
                    break;
                case "win":
                case "meta":
                    mods |= HotkeyModifiers.Win;
                    break;
                default:
                    if (!Enum.TryParse(part, true, out key))
                    {
                        // 支持 "0"~"9"、"A"~"Z" 这类单字符
                        if (part.Length == 1 && char.IsDigit(part[0]))
                        {
                            key = Key.D0 + (part[0] - '0');
                        }
                        else
                        {
                            return false;
                        }
                    }

                    break;
            }
        }

        if (key == Key.None) return false;
        hotkey = new Hotkey(mods, (uint)KeyInterop.VirtualKeyFromKey(key));
        return hotkey.IsValid;
    }

    public override string ToString()
    {
        if (VirtualKey == 0) return "未设置";

        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");

        var key = KeyInterop.KeyFromVirtualKey((int)VirtualKey);
        parts.Add(FormatKey(key));
        return string.Join("+", parts);
    }

    private static string FormatKey(Key key)
    {
        var name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) return name[1].ToString();
        if (name.Length == 3 && name.StartsWith("NumPad", StringComparison.Ordinal)) return "Num" + name[^1];
        return name switch
        {
            "Return" => "Enter",
            "Escape" => "Esc",
            "Next" => "PageDown",
            "Prior" => "PageUp",
            "Capital" => "CapsLock",
            "OemMinus" => "-",
            "OemPlus" => "=",
            _ => name,
        };
    }

    public bool Equals(Hotkey? other)
        => other is not null && other.Modifiers == Modifiers && other.VirtualKey == VirtualKey;

    public override bool Equals(object? obj) => Equals(obj as Hotkey);

    public override int GetHashCode() => HashCode.Combine(Modifiers, VirtualKey);
}

/// <summary>用户设置，持久化到 %APPDATA%\QingJie\settings.json。</summary>
public sealed class Settings
{
    /// <summary>唤起截图的热键。</summary>
    public Hotkey Hotkey { get; set; } = Hotkey.Default;

    /// <summary>另存为的默认目录。</summary>
    public string SaveDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "轻截");

    /// <summary>复制时把图片直接写入剪贴板（关闭则仅保存文件）。</summary>
    public bool CopyToClipboard { get; set; } = true;

    /// <summary>保存时弹出路径选择框，否则直接存到默认目录。</summary>
    public bool AskSavePath { get; set; }

    /// <summary>选图时显示放大镜取色镜。</summary>
    public bool ShowMagnifier { get; set; } = true;

    /// <summary>复制完成后自动关闭截图界面。</summary>
    public bool CloseAfterCopy { get; set; } = true;

    /// <summary>开机自启动。</summary>
    public bool StartWithWindows { get; set; }

    [JsonIgnore]
    public string SettingsFilePath => Path.Combine(AppPaths.DataDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Settings Load()
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDirectory, "settings.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<Settings>(json, JsonOptions);
                if (loaded is not null)
                {
                    if (!loaded.Hotkey.IsValid) loaded.Hotkey = Hotkey.Default;
                    if (string.IsNullOrWhiteSpace(loaded.SaveDirectory)) loaded.SaveDirectory = new Settings().SaveDirectory;
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"读取设置失败，使用默认值：{ex.Message}");
        }

        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Logger.Warn($"保存设置失败：{ex.Message}");
        }
    }
}

internal static class AppPaths
{
    public static string DataDirectory { get; } = EnsureDirectory(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QingJie"));

    public static string TempDirectory { get; } = EnsureDirectory(Path.Combine(Path.GetTempPath(), "QingJie"));

    private static string EnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
            // ignored
        }

        return path;
    }
}
