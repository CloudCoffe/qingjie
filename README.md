# 轻截 QingJie

一款轻量级 Windows 截图工具：**划区截图 · 标注 · 贴图钉住 · 右键复制**，交互参考 uTools 的截图插件。

纯原生 WPF + Win32 实现，**零第三方依赖**，发布后单文件约 **300 KB**（框架依赖模式）。

```
Ctrl + Alt + A   →   拖拽划区   →   回车复制 / Ctrl+S 保存 / Ctrl+P 贴图
```

## 功能

### 划区截图

- 多显示器支持，每个屏幕独立覆盖层，任选一屏截图
- 十字光标 + 暗化遮罩，实时显示选区物理像素尺寸
- 放大镜取色：跟随鼠标显示 8× 放大、HEX 颜色值与坐标
- 选区八向控制点自由缩放，选区内部可拖动整体平移
- 方向键微调位置（Shift + 方向键每次 10px），`Ctrl+A` 全屏选区
- 完整 Per-Monitor V2 DPI 感知：在 150% / 200% 缩放下截图依然像素级清晰，不裁错、不模糊

### 标注

| 工具 | 快捷键 | 说明 |
| --- | --- | --- |
| 矩形 | `R` | 框选重点 |
| 椭圆 | `O` | 圈选局部 |
| 箭头 | `A` | 指向说明 |
| 画笔 | `P` | 自由涂画 |
| 文字 | `T` | 点击后直接输入，`Shift+Enter` 换行 |
| 马赛克 | `M` | 块内取平均色，真正的像素化（非高斯模糊） |

- 7 种颜色 + 3 档线条粗细
- `Ctrl+Z` 撤销上一步标注
- 预览与导出使用同一套渲染逻辑，**所见即所得**

### 贴图钉住

- `Ctrl+P` 或点击工具栏图钉按钮，把选区钉在屏幕上
- 无边框置顶窗口，鼠标拖动移动，双击关闭
- 滚轮缩放（20% ~ 400%），`Ctrl+滚轮` 调透明度
- **右键菜单**：复制图片 / 另存为 / 窗口置顶开关 / 恢复原始大小 / 关闭
- 支持同时钉住多张图，位置与截图区域像素级对齐

### 其他

- 全局热键可在设置里自由录制（默认 `Ctrl+Alt+A`）
- 托盘常驻：左键单击立即截图，右键菜单进入设置 / 打开目录 / 退出
- 复制提示浮层、单实例运行、开机自启动开关
- 设置持久化到 `%APPDATA%\QingJie\settings.json`

## 技术实现

| 关注点 | 做法 |
| --- | --- |
| 抓屏 | GDI `Graphics.CopyFromScreen` 一次性抓取整个虚拟桌面（物理像素），作为唯一图源 |
| DPI | 进程声明 `PerMonitorV2`；每块覆盖层用 `SetWindowPos` 铺满显示器物理矩形，再按 `物理宽 / 逻辑宽` 反推缩放比，勾选 `WM_DPICHANGED` 重新对齐 |
| 覆盖层 | 每个显示器一个无边框置顶窗口，内含底图 + 四向遮罩 + 选区边框 + 八向控制点 + 工具栏 |
| 坐标体系 | 选区内部一律使用「相对选区的设备无关像素」，导出时整体 ×缩放比 → 预览与成图严格一致 |
| 标注 | 自定义 `AnnotationLayer` 在 `OnRender` 中统一绘制；导出用同一套绘制代码渲染到 `RenderTargetBitmap` |
| 马赛克 | 按块对裁剪图求平均色，再绘制纯色方块（不依赖位图插值，缩放后仍是硬边色块） |
| 剪贴板 | 同时写入 `Bitmap` 与 `PNG` 两种格式，兼容更多软件；被占用时自动重试 |
| 热键 | 隐藏消息窗口 + `RegisterHotKey`（`MOD_NOREPEAT`） |
| 贴图 | 无边框置顶窗口，物理像素定位，`DragMove` + 滚轮缩放 |

### 目录结构

```
轻截/
├─ assets/app.ico                 # 由 tools/make_icon.py 生成
├─ src/QingJie/
│  ├─ App.xaml(.cs)               # 入口：单实例、托盘、热键、设置应用
│  ├─ AppTray.cs                  # 托盘图标与菜单
│  ├─ Core/
│  │  ├─ Native.cs                # Win32 互操作（显示器枚举、DPI、窗口定位、热键）
│  │  ├─ ScreenGrab.cs            # 整屏抓取 / 裁剪 / 取色
│  │  ├─ Settings.cs              # 设置模型 + 热键解析
│  │  ├─ HotkeyManager.cs         # 全局热键
│  │  ├─ ImageIo.cs               # 剪贴板 / PNG 保存
│  │  ├─ Toast.cs(.xaml)          # 轻提示浮层
│  │  └─ Logger.cs                # 文件日志
│  ├─ Capture/
│  │  ├─ CaptureSession.cs        # 一次截图流程的编排
│  │  ├─ OverlayWindow.xaml(.cs)  # 划区 + 标注 + 工具栏
│  │  └─ Annotation.cs            # 标注模型 / 绘制 / 马赛克
│  ├─ Pin/PinWindow.xaml(.cs)     # 贴图窗口
│  └─ Views/SettingsWindow.xaml   # 设置窗口
└─ tools/make_icon.py             # 纯标准库生成多尺寸 ico
```

## 快捷键一览

| 按键 | 作用 |
| --- | --- |
| `Ctrl+Alt+A` | 唤起截图（可自定义） |
| 拖拽 | 划区 |
| `Enter` | 复制到剪贴板 |
| `Ctrl+S` | 保存为 PNG |
| `Ctrl+P` | 贴图钉住 |
| `Ctrl+Z` | 撤销标注 |
| `Ctrl+A` | 选中整个屏幕 |
| `方向键` / `Shift+方向键` | 微调选区 1px / 10px |
| `Esc` | 退出工具 → 取消选区 → 退出截图（逐级） |
| 右键 | 退出当前工具 → 取消选区 → 退出截图 |

## 构建

环境要求：Windows 10/11 + [.NET SDK 10](https://dotnet.microsoft.com/download)（`net10.0-windows`）。

```bash
git clone https://github.com/CloudCoffe/qingjie.git
cd qingjie

# 调试运行
dotnet run --project src/QingJie

# 发布单文件（依赖 .NET 运行时，体积约 1 MB）
dotnet publish src/QingJie -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist

# 发布独立单文件（自带运行时，免安装 .NET，体积约 70 MB）
dotnet publish src/QingJie -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/standalone
```

或直接双击 `build.cmd`。

## 开发者自检模式

为便于在没有 UI 自动化的情况下核对渲染结果，程序内置了几个隐藏参数（不影响正常使用）：

```bash
QingJie.exe --capture              # 启动后立即进入截图
QingJie.exe --snapshot out.png     # 渲染截图界面并抓屏，输出 out.png 与 out_selected.png
QingJie.exe --compose out.png      # 导出示例选区合成图，并校验剪贴板写入
QingJie.exe --pintest out.png      # 创建贴图窗口并抓屏，校验定位与 1:1 显示
QingJie.exe --dragtest out.png     # 用合成鼠标/键盘真实拖拽一遍：划区 → 拖动 → 控制点缩放 → 画标注 → 输入文字
```

`--dragtest` 会真实移动鼠标并按下拖动，跑完把每一步的选区（DIU 与物理像素）写进日志，同时输出
`out_base.png`（拖拽前的桌面）与 `out.png`（拖拽后的裁剪结果），便于做逐像素对齐校验。

## 更新日志

### v1.0.1

- 修复**拖拽无法划区**：鼠标捕获原先挂在 `Window` 上，捕获后事件的源变成 Window 本身，
  作为子元素的画布收不到 `MouseMove` / `MouseLeftButtonUp`，导致按下后选区不再跟随、松手也不复位。
  改为捕获到画布元素后，划区、拖动、控制点缩放、标注绘制全部恢复。
- 修复**中文输入法抢走快捷键**：覆盖层窗口禁用了输入法（`InputMethod.IsInputMethodEnabled=False`），
  否则激活输入法时 `R/O/A/P/T/M` 等单键会被判成 `ImeProcessed` 而失效；文字输入框单独开启输入法，
  仍可正常输入中文。
- 新增 `--dragtest` 真实输入自检模式。


## 已知限制

- 仅支持 Windows（依赖 GDI / Win32 / WPF）
- 选区限制在单一显示器内；跨屏拼接截图尚未支持
- 暂不支持滚动长截图、OCR、贴图内二次标注

## License

[MIT](LICENSE)
