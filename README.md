# 口琴谱演奏器 HarmoPlay

**本地离线**的简谱曲谱库 + 自动演奏 + 悬浮窗键位提示工具，内置《三角洲行动》口琴 8 键预设，
键位可自由映射；支持把 AI 转好的曲谱一键导入、校验、跟练和「音游下落」模式。

> 定位：对标「鼠鼠口琴谱」的**离线免费版**。不含账号 / 付费 / 在线曲谱 / 教练视频，**全程不联网**
> （只有你主动点「检查更新」时才会去读一次仓库里的 `update.json`）。

![主界面](docs/main-window.png)
![悬浮窗 · 经典堆叠](docs/overlay.png)
![悬浮窗 · 音游下落](docs/falling.png)

---

## 功能一览

| 模块 | 说明 |
| --- | --- |
| 曲谱库 | 分类 + 搜索（曲名 / 歌手 / 简谱内容）；内置示例曲谱；可导入原版「鼠鼠口琴谱」的 `library.json` |
| 简谱解析 | 三种格式自动识别 → 三角洲可视化谱、鼠鼠固定音高谱（AI 转谱规范）、D-hydra 键位谱 |
| 简谱编辑器 | 边写边解析，每小节显示「简谱 / 键位 / 时值 / 音名」；导入导出 TXT、JSON |
| 演奏引擎 | SendInput 扫描码注入键盘 + 鼠标修饰键；BPM / 速度 / Gap / Lead / 倒计时 / 重复 / 同键连音 |
| 全局热键 | 播放暂停、停止、上下首、悬浮窗显隐与锁定、跟练切换、快捷曲 1~6（均可改键） |
| 悬浮窗 | 置顶、半透明、点击穿透；**经典堆叠** 与 **音游下落**（判定线、按拍下落、当前音高亮）两种模式 |
| 键位映射 | 预设（三角洲 8 键 / 数字键 / 字母键）+ 可视化自定义（逐个按键捕获、6 种变调、配色） |
| 跟练模式 | 不发送按键，等你按对当前音再继续，可当练琴谱面用 |
| **AI 转谱校验** | `--validate` 按转谱规范校验 JSON：语法、时值、音域、小节拍数、字段类型 |
| **更新接口** | 从仓库 `update.json` 检查新版本（只提示、不自动替换） |
| **问题反馈** | 一键打开 GitHub Issues；内置诊断包导出（含环境与日志，不含曲谱内容） |

## 下载与运行

1. 到 [Releases](../../releases/latest) 下载 `HarmoPlay-<版本>-win-x64.zip`；
2. 解压后双击 **`口琴谱演奏器.exe`**（自包含，无需安装 .NET）；
3. **保持 5 个原生 DLL 与 exe 在同一目录**（`D3DCompiler_47_cor3.dll`、`PenImc_cor3.dll`、
   `PresentationNative_cor3.dll`、`vcruntime140_cor3.dll`、`wpfgfx_cor3.dll`）。

> 游戏以管理员身份运行时，本程序也要**右键 → 以管理员身份运行**，否则游戏收不到模拟按键。

## 三分钟上手

1. 左侧选一首曲谱 → `Alt+1` 播放 / 暂停。
2. 悬浮窗按 `Alt+L` 解锁后可拖动，`Ctrl+滚轮` 缩放；再按 `Alt+L` 锁定（点击穿透，不挡操作）。
3. 想让音符按节奏落下来：设置里把悬浮窗模式切到**音游下落**，或直接在「键位与设置 → 音游下落模式」里调速度与提前量。

### 默认键位（三角洲行动 · 口琴）

```
8 个琴键：  z   x   c   v   b   n   m   ,
简谱：      1   2   3   4   5   6   7   8（8 = 高音 1）
```

变调靠鼠标：**左键 = 降八度**、**中键 = 升半音**、**右键 = 升八度**，中键可与左/右键组合。

### 全局热键

| 热键 | 作用 |
| --- | --- |
| `Alt+1` / `Alt+2` / `Alt+3` / `Alt+↑` | 播放暂停 / 停止 / 下一首 / 上一首 |
| `Alt+H` / `Alt+L` | 悬浮窗 显隐 / 锁定（解锁后可拖动、`Ctrl+滚轮` 缩放） |
| `Alt+T` | 自动演奏 ↔ 跟练模式 |
| `Alt+4` ~ `Alt+9` | 快捷曲 1~6（曲谱上右键绑定） |

## 曲谱从哪来

- **内置示例**：首次启动自带小星星、欢乐颂、See You Again、父亲等。
- **导入原版曲库**：工具栏「导入鼠鼠口琴谱曲库」→ 选 `%LOCALAPPDATA%\HarmonicaMacro\Data\library.json`。
- **AI 转谱**（推荐）：把音频 / MIDI / 谱面图片交给大模型，按规范输出 `曲名.json`，再用本程序校验与导入。

👉 完整流程、提问模板、固定音高对照表、常见错误清单见
**[docs/曲谱生成与导入教程.md](docs/曲谱生成与导入教程.md)**，规范原文见
[docs/AI转谱要求-原文.txt](docs/AI转谱要求-原文.txt)。

```bat
:: 校验 AI 生成的曲谱（错误必须为 0 才导入）
HarmoPlay.exe --validate "D:\曲谱\晴天.json"

:: 批量导入一个目录里的 txt / json
HarmoPlay.exe --import "D:\曲谱"
```

## 命令行参数

```
HarmoPlay.exe                    启动图形界面
--selftest                       自检（解析器 / 键位表 / 校验器 / 更新接口 / 诊断包），结果写 selftest.log
--validate <曲名.json> ...       按转谱规范校验曲谱文件
--import <文件|目录>             导入曲谱（library.json / 单曲 JSON / 简谱 txt / 目录）
--checkupdate [地址]             检查更新，结果写 update-check.log
--feedback                       导出诊断包到 data/feedback/
--list                           列出曲谱库到 library.txt
--help                           显示帮助
```

## 数据目录

优先放在**程序同目录的 `data\`**（绿色便携）；该目录不可写时依次回退到
`%APPDATA%\HarmoPlay` → `%LOCALAPPDATA%\HarmoPlay` → 文档 → 临时目录。

```
data/library.json    曲谱库（分类 + 曲谱 + 简谱文本 + BPM）
data/settings.json   设置（键位映射、热键、悬浮窗、演奏参数、更新地址）
data/feedback/       导出的诊断包
```

## 更新与反馈

- **更新**：读仓库根目录的 [update.json](update.json) 比对版本，只提示、不自动替换。
  格式与自动发版流程见 **[docs/更新接口.md](docs/更新接口.md)**；
  推一个 `v*` 标签，GitHub Actions 会自动编译、打 zip、发 Release 并更新清单。
- **反馈**：软件内一键打开 GitHub Issues；先点「导出诊断包」，把
  `HarmoPlay-诊断-*.txt`（不含曲谱内容）一起贴进 Issue。
  见 **[docs/问题反馈.md](docs/问题反馈.md)**。

上传自己的仓库后，记得把更新地址改成自己的：软件「键位与设置 → 更新与问题反馈」，
或直接改 `AppSettings` 里的 `UpdateUrl` / `DownloadUrl` / `IssuesUrl`。

## 从源码构建

需要 **.NET 8 SDK**（源码面向 Windows / WPF）。

```powershell
dotnet build   HarmoPlay.csproj -c Debug
dotnet publish HarmoPlay.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:DebugType=none -o dist
```

> ⚠️ **不要加** `-p:IncludeNativeLibrariesForSelfExtract=true`：那要求运行时把原生库解压到
> `%TEMP%\.net`，在部分被 ACL 限制的用户目录下会直接启动失败
> （`Failed to create default extraction directory`）。
> 当前发布方式 = 托管程序集内嵌进单文件 exe + 5 个 WPF 原生 DLL 放同目录，无需解压。

本地打完整发布包（含 zip、SHA256、update.json）：

```powershell
pwsh -File tools/make-release.ps1 -Version 1.0.1 -Notes "新增下落模式" -Repo "你的用户名/HarmoPlay"
```

## 项目结构

```
HarmoPlay.csproj            工程（WPF / net8.0-windows / 自带 Program.Main）
Program.cs                  入口：启动追踪 + 命令行模式
App.xaml(.cs)               深色主题 + 全局异常记录
SelfTest.cs                 无界面自检
Cli.cs                      命令行（import / validate / checkupdate / feedback / list）
Models/                     Song、KeyMap（预设与自定义映射）、PitchFingering（固定音高与指法）、设置
Core/                       ScoreParser 简谱解析、ScoreValidator 转谱校验、Chord 组合解析、
                            InputSender SendInput、PlaybackEngine 演奏引擎、GlobalHotkeys 全局热键、
                            LibraryStore 存储与导入导出、UpdateService 更新接口、Diagnostics 诊断包
Views/                      MainWindow 主界面、OverlayWindow/OverlayCanvas 悬浮窗（堆叠 + 下落）、
                            KeyMapWindow 键位编辑、HotkeyCaptureWindow、InputWindow
Assets/Seed/*.txt           内置示例曲谱
docs/                       曲谱生成与导入教程、转谱规范原文、更新接口、问题反馈、截图
tools/make-release.ps1      本地打发布包
.github/workflows/release.yml  推 v* 标签自动发版
```

## 免责声明

- 本程序只做三件事：画置顶窗口、读键盘鼠标状态、（自动演奏时）模拟按键。
  **不联网、不读游戏内存、不注入游戏进程、不修改任何游戏文件。**
- 请遵守你所玩游戏的用户协议，自行评估使用风险。
- 曲谱版权归原作者所有；请不要传播未授权的付费曲谱。

## 许可

[MIT](LICENSE)
