# Fluent OSD Pro (`FluentOsdPro`)

**Windows 11 光标级输入法状态指示器（InputSourcePro 风格） · 120Hz 亚克力音量与大写锁定浮窗 · UU 远程 / 中兴 VDI 全屏音量与虚拟桌面守护**

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011-0078D4?logo=windows)](https://github.com/Hurdmmmer/FluentOsdPro)
[![Framework](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4?logo=dotnet)](https://github.com/Hurdmmmer/FluentOsdPro)
[![License](https://img.shields.io/badge/License-MIT-success)](https://github.com/Hurdmmmer/FluentOsdPro)
[![Built with Antigravity](https://img.shields.io/badge/AI%20Pair%20Programmed-Google%20DeepMind%20Antigravity-4285F4?logo=google)](https://github.com/Hurdmmmer/FluentOsdPro)

![Fluent OSD Pro Cursor-Following IME OSD Preview](Assets/preview_cursor_osd.png)

---

## ✨ 核心功能概览

### 1. 光标级输入法状态指示器 (`Caret-Following IME Indicator`)
- **全场景光标右上角精准跟随（对标 macOS `InputSourcePro`）**：
  - 结合 `GUIThreadInfo`、`IMM32`（`ImmGetCompositionWindow` / `ImmGetCandidateWindow`）、`MSAA`（`OBJID_CARET`）、原生 `CUIAutomation8`（`IUIAutomationTextPattern2::GetCaretRange` / `IUIAutomationTextPattern::GetSelection`）与 GPU 终端画布特征扫描，实现跨应用光标定位：
    - **原生 Win32 / WPF 应用**：记事本、资源管理器、Office、Visual Studio 等。
    - **Chromium / Electron / CEF 应用**：Chrome、Edge、VS Code、Cursor、飞书、微信等。
    - **终端与 GPU 自绘应用**：Windows Terminal、PowerShell、以及基于 `MyGoSurface` GPU 直绘的 `Kero` 终端（支持输入框水平分界线锁定与光标 500ms 闪烁熄灭帧自动补偿）。
- **屏幕边缘智能翻转避让 (`Smart Screen Edge Flip`)**：
  - 默认停靠于输入光标的**右上角**（上方 `6 DIP`、右侧 `4 DIP`）；
  - 当打字靠近当前显示器右边缘、右侧空间不足以完整容纳浮窗时，自动平滑翻转至**光标左上角**；当光标位于屏幕最顶行时自动翻转至光标下方。
- **主动切回中文自动恢复母语输入模式**：
  - 当通过 `Win + Space` 或 `Alt + Shift` 从英文布局（`0x0409`）主动切回中文输入法（`0x0804`）时，自动将转换模式设为**中文模式（`中`）**，告别“切回中文输入法却还是英文半角状态”的痛点。
- **按键门控 + 切换窗口静默期 (`Key-Gated & Quiet Period`)**：
  - 仅在用户主动按下输入法切换键（`Shift` / `Ctrl + Space` / `Win + Space` / `Alt + Shift`）时弹出提示；鼠标点击输入框或 `Alt + Tab` 切换窗口时保持静默，绝不打扰。

---

### 2. 五套可选 120Hz 微交互切换动画 (`5 Selectable 120Hz Motion Presets`)
针对首次弹出/退场与浮窗可见状态下的原地连续切换（`[中] 简体拼音` <-> `[A] English`），内置 **5 套风格鲜明的微交互动效预设**（可在设置面板实时预览切换）：

| 预设名称 | 枚举值 | 动效设计细节 | 适用风格 |
| :--- | :--- | :--- | :--- |
| **灵动回弹** *(默认)* | `SpringPop` | 对标 `InputSourcePro` / macOS Sonoma：左侧 `[中/A]` 徽标中心微弹（`Scale: 0.84 -> 1.0`, `BackOut`），右侧文字自左向右轻柔舒展（`X: -2.2 -> 0`, `Y = 0`），中英胶囊等宽锁定不塌缩 | 果味灵动、跟手自然 |
| **丝滑柔光** | `SmoothFade` | 对标微软 Fluent 2 无干扰规范：零水平/垂直位移（`X = 0, Y = 0`），徽标与文字原地纯光学柔和呼吸溶解（`120ms SineOut`） | 极致克制、高频码字 |
| **极速微推** | `CrispSlide` | 对标 Linear / GitHub 120Hz 工具流：`95ms` 超短时长、`QuarticOut` 高初速度水平利落推进（`X: -4.5 -> 0`） | 极客干脆、高刷屏首选 |
| **呼吸脉冲** | `PulseMorph` | 对标 iOS / macOS 灵动岛（Dynamic Island）：切换瞬间整卡向外轻微充气舒张（`Scale: 1.0 -> 1.045 -> 1.0`）+ 徽标弹性缩放 | 强视觉反馈 |
| **极速直出** | `None` | `0ms` 零延迟硬切，关闭所有出入场与原地切换过渡动画 | 电竞全屏 / 远程低带宽 |

---

### 3. 零白屏 120Hz Windows 11 亚克力音量与大写锁定浮窗 (`Volume & CapsLock OSD`)
- **彻底消除首次显示白屏闪烁 (`Zero First-Show White Flash`)**：
  - 启动时在屏幕外 `(-32000, -32000)` 预创建并预热 `VolumeOsdWindow` 与 `StatusOsdWindow`，预先挂载 `DwmExtendFrameIntoClientArea(-1)`、Windows 11 原生圆角 (`DWMWCP_ROUND`) 与 `SetWindowCompositionAttribute` 亚克力毛玻璃背景。
- **大写锁定矢量锁扣动画 (`CapsLock Status Flyout`)**：
  - 按下 `CapsLock` 时展示纯矢量锁头与锁梁动画（开启时锁梁闭合 `0°`，关闭时斜向弹开 `25°`）。
- **多种浮窗位置可选**：
  - 支持 `跟随输入光标 (InputSourcePro)`、`与音量位置相同 (底部居中自动堆叠)`、`屏幕左上角`、`屏幕右上角`、`屏幕左下角`、`屏幕右下角`、`屏幕顶部居中`。

---

### 4. UU 远程 & 中兴 VDI 本地音量与虚拟桌面守护 (`Remote & VDI Guard`)
- **本地音量键拦截 (`Volume Up / Down / Mute`)**：当 UU 远程 (`GameViewer`) 或中兴 VDI (`uSmartView_VDI_Client` / `VirtualAppVDI`) 处于全屏或前台焦点时，键盘音量键与旋钮直接通过 Windows Core Audio (`IAudioEndpointVolume`) 调节**宿主机本地音量**并弹出亚克力音量浮窗。
- **`Ctrl + 音量加/减` 快速切换音频输出设备 (`Audio Output Switcher`)**：按住 `Ctrl` 并滚动音量滚轮即可在本地扬声器/耳机/显示器音频之间循环切换，浮窗宽度随设备名弹性过渡。
- **`Win + Ctrl + 左/右方向键` 切回本地虚拟桌面**：在远程全屏下直接切换宿主机本地 Windows 虚拟桌面。
- **无边框最大化窗口任务栏唤醒守护 (`Taskbar Hover Guard`)**：自动为微信、飞书、浏览器等无边框最大化窗口注入 `NonRudeHWND=1`，确保自动隐藏任务栏（含原生任务栏与 Windhawk 任务栏）100% 正常唤出。

---

## 🙏 致谢与借鉴项目 (`Acknowledgements & Credits`)

### 🤖 AI 结对编程与动效设计 (`AI Pair Programming`)
本项目由 **[Jimmy (@Hurdmmmer)](https://github.com/Hurdmmmer)** 与 **Google DeepMind Antigravity** 深度结对编程共同打造：
- **Google DeepMind Antigravity**：负责全栈架构设计、Win32 / IMM32 / UIAutomation / GDI 底层光标追踪引擎实现、零白屏亚克力双浮窗渲染、Kero (`MyGoSurface`) 终端分界线与闪烁帧补偿算法以及全量代码审查与调优。
- **UI Motion & Animation Subagent (Claude)**：协助诊断微交互曲线并共同设计 5 套 120Hz 浮窗切换动画预设（`SpringPop` / `SmoothFade` / `CrispSlide` / `PulseMorph` / `None`）。

### 🌟 致敬与借鉴的优秀开源项目 (`Inspired By`)
在 UI 视觉、光标定位策略与系统浮窗交互设计上，本项目深入研究并借鉴了以下优秀的开源项目，特此致以诚挚感谢：
1. **[runjuu/InputSourcePro](https://github.com/runjuu/InputSourcePro)** — 借鉴了其极具质感的 macOS 极简输入法胶囊视觉规范（`16×16` 反色圆角徽标 + `26 DIP` 紧凑胶囊比例、深浅色自适应高对比度配色）以及“跟随输入光标提示、无光标时回退默认位置”的核心交互哲学。
2. **[mianhuamu1109/CapsLockFlyout](https://github.com/mianhuamu1109/CapsLockFlyout)** — 借鉴了其轻量化 Windows 状态提示浮窗思路与大写锁定矢量锁扣状态动画设计。
3. **[unchihugo/FluentFlyout](https://github.com/unchihugo/FluentFlyout)** & **[ModernFlyouts-Community/ModernFlyouts](https://github.com/ModernFlyouts-Community/ModernFlyouts)** — 借鉴了 Windows 11 Fluent Design 亚克力音量浮窗布局、音频设备切换交互与平滑过渡曲线。
4. **[tanyuan/GetCaretPosEx](https://github.com/tanyuan/GetCaretPosEx)** — 借鉴了其在 Windows 平台跨框架获取真实输入光标坐标的多级回退思路（`GetGUIThreadInfo` -> `ImmGetCompositionWindow` -> `MSAA OBJID_CARET` -> `UIAutomation TextPattern`）。

---

## 📁 项目结构

```text
FluentOsdPro/
├── FluentOsdPro.sln                   # Visual Studio 2022 / 2026 解决方案文件
├── FluentOsdPro.csproj                # SDK-Style WPF + WinForms 项目文件 (TargetFramework: net48)
├── app.manifest                       # PerMonitorV2 DPI 感知与 Windows 10/11 兼容性清单
├── README.md                          # 项目说明文档
├── Assets/                            # 静态内嵌图标与预览资源目录
│   ├── app.ico                        # EXE 应用程序多尺寸图标
│   ├── tray_dark.ico                  # 深色任务栏专用纯白多层托盘图标
│   ├── tray_light.ico                 # 浅色任务栏专用深色多层托盘图标
│   └── preview_cursor_osd.png         # 光标右上角输入法状态浮窗实测预览图
└── Src/
    ├── Program.cs                     # 应用入口、键盘/窗口钩子、虚拟桌面与焦点调度
    ├── Config/
    │   ├── AppSettings.cs             # 配置持久化 (Data/settings.json，含位置与 5 套动画预设)
    │   └── AutoStartManager.cs        # Windows 计划任务高权限无弹窗开机自启管理
    ├── Interop/
    │   └── ThemeService.cs            # 系统与应用深浅色主题、透明效果、语言与时区检测
    ├── Audio/
    │   └── AudioController.cs         # Core Audio 音量调节与 PolicyConfig 输出设备切换
    ├── Guards/
    │   └── TaskbarHoverGuard.cs       # 无边框最大化任务栏唤醒守护与远程全屏任务栏抑制
    ├── InputMethod/
    │   ├── InputMethodSnapshot.cs     # 输入法语言族与转换模式快照模型
    │   ├── InputMethodTracker.cs      # 按键门控、窗口切换静默期与跨布局切换追踪状态机
    │   └── InputMethodMonitor.cs      # IMM32 监控器、键盘布局切换自动恢复中文模式
    ├── Osd/
    │   ├── NativeWindowStyler.cs      # DWM 亚克力毛玻璃、圆角边框与无焦点穿透样式
    │   ├── OsdPlacementService.cs     # 多策略光标定位、Kero 终端光标识别、右上角与右边缘翻转计算
    │   ├── VolumeOsdWindow.cs         # 底部居中音量与音频输出设备切换浮窗
    │   └── StatusOsdWindow.cs         # 输入法与大写锁定胶囊浮窗 (内置 5 套 120Hz 微交互动画)
    └── Tray/
        ├── TrayIconGenerator.cs       # 纯色深浅色自适应托盘图标生成与加载器
        ├── TrayMenuPopupWindow.cs     # 极简托盘弹出菜单
        └── SettingsWindow.cs          # Fluent 设置面板与切换动效实时预览窗口
```

---

## 🛠️ 构建与运行方式

### 方式一：使用 .NET CLI 编译
在项目根目录下运行：

```powershell
dotnet build FluentOsdPro.sln -c Release
```

编译产物位于 `bin\Release\net48\FluentOsdPro.exe`（基于 Windows 10/11 自带 `.NET Framework 4.8`，单文件零外部依赖）。

### 方式二：使用 Visual Studio 编译
使用 **Visual Studio 2022 / 2026** 打开 `FluentOsdPro.sln`，选择 `Release | Any CPU` 生成解决方案即可。

### 命令行参数说明
| 参数 | 说明 |
| :--- | :--- |
| `(无参数)` | 正常启动托盘守护进程（若非管理员权限会自动请求提权以兼容远程桌面 UIPI） |
| `--replace` | 启动并自动接管替换已运行的旧实例 |
| `--verify` | 执行无界面自检（验证深浅色图标加载、钩子状态与权限）并退出 |
| `--enable-autostart` | 注册 Windows 计划任务静默高权限开机自启（无 UAC 弹窗） |
| `--disable-autostart` | 移除开机自启计划任务 |
