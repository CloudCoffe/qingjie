using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using QingJie.Core;

namespace QingJie.Ocr;

/// <summary>识别结果：<see cref="Error"/> 为 null 表示成功（可能识别出空字符串）。</summary>
internal sealed record OcrOutcome(string Text, string? Error)
{
    public static OcrOutcome Fail(string error) => new(string.Empty, error);
}

/// <summary>
/// 取字（OCR）：调用 Windows 自带的 Windows.Media.Ocr 引擎，离线、无需下载模型。
///
/// 为什么走 PowerShell 子进程而不是直接引用：WinRT 的 C# 投影（Microsoft.Windows.SDK.NET.dll）
/// 会让单文件发布体积从 300 KB 涨到 25 MB，与「轻量」定位冲突。Windows PowerShell 5.1 自带
/// .NET Framework 的 WinRT 互操作能力，用它跑一次识别既零依赖、也几乎不增体积。
/// </summary>
internal static class OcrService
{
    /// <summary>识别脚本：结果以 Base64(UTF-8) 输出，避开控制台代码页把中文弄乱。</summary>
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        Add-Type -AssemblyName System.Runtime.WindowsRuntime

        $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
            $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
        })[0]

        function Await($op, $type) {
            $task = $asTask.MakeGenericMethod($type).Invoke($null, @($op))
            $task.Wait(-1) | Out-Null
            $task.Result
        }

        $null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
        $null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime]
        $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]

        $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
        if ($null -eq $engine) { Write-Error 'QJ_NO_ENGINE'; exit 2 }

        $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($env:QJ_OCR_IMAGE)) ([Windows.Storage.StorageFile])
        $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
        $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])

        $result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
        [Console]::Out.WriteLine([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($result.Text)))
        $bitmap.Dispose()
        $stream.Dispose()
        """;

    private static readonly Lazy<string> EncodedCommand = new(() =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)));

    /// <summary>两个汉字之间的水平空白（不含换行），OCR 逐字分词时会插进来。</summary>
    private static readonly Regex CjkSpace = new(
        @"(?<=[\u4e00-\u9fff])[ \t\u00a0\u3000]+(?=[\u4e00-\u9fff])",
        RegexOptions.Compiled);

    /// <summary>识别图片里的文字。合成图是原图（不含标注），避免标注笔画干扰。</summary>
    public static Task<OcrOutcome> RecognizeAsync(BitmapSource image) => Task.Run(() =>
    {
        string path = Path.Combine(AppPaths.TempDirectory, $"qingjie_ocr_{Guid.NewGuid():N}.png");
        try
        {
            ImageIo.SavePng(image, path);
            return RunScript(path);
        }
        catch (Exception ex)
        {
            Logger.Error("取字失败", ex);
            return OcrOutcome.Fail("识别失败：" + ex.Message);
        }
        finally
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    break;
                }
                catch (IOException)
                {
                    // 引擎可能还持着文件句柄，稍等重试
                    Thread.Sleep(60);
                }
                catch
                {
                    break;
                }
            }
        }
    });

    private static OcrOutcome RunScript(string imagePath)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + EncodedCommand.Value,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.Environment["QJ_OCR_IMAGE"] = imagePath;

        using var process = Process.Start(startInfo);
        if (process is null) return OcrOutcome.Fail("无法启动系统 OCR");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(20000))
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
                // ignored
            }

            return OcrOutcome.Fail("识别超时");
        }

        string output = stdout.GetAwaiter().GetResult().Trim();
        string error = stderr.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            if (error.Contains("QJ_NO_ENGINE") || process.ExitCode == 2)
            {
                return OcrOutcome.Fail("系统没有可用的 OCR 语言包");
            }

            Logger.Warn($"取字失败 exit={process.ExitCode} {error.Trim()}");
            return OcrOutcome.Fail("识别失败，请重试");
        }

        string payload = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? string.Empty;
        try
        {
            return new OcrOutcome(Normalize(Encoding.UTF8.GetString(Convert.FromBase64String(payload))), null);
        }
        catch (FormatException)
        {
            Logger.Warn($"取字返回无法解析：{output}");
            return OcrOutcome.Fail("识别失败，请重试");
        }
    }

    /// <summary>Windows OCR 把中文逐字当一个词，输出会变成「轻 截 取 字」，这里把汉字之间的空格去掉。</summary>
    private static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return CjkSpace.Replace(text, string.Empty);
    }
}
