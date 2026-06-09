# ClaudeStatus

> 任务栏上的 Claude Code「红绿灯」—— 一眼看清 Claude 现在在干活、刚答完、空闲、还是在等你。
> A tiny Windows taskbar status light for Claude Code.

[![Release](https://img.shields.io/github/v/release/ALANGMeow/ClaudeStatus)](https://github.com/ALANGMeow/ClaudeStatus/releases/latest)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

📦 [下载最新 Release](https://github.com/ALANGMeow/ClaudeStatus/releases/latest) · 🐙 [GitHub 仓库](https://github.com/ALANGMeow/ClaudeStatus)

ClaudeStatus 是一个常驻 Windows 任务栏的小挂件：在托盘图标区左侧显示一个**彩色指示灯 + 文字**，通过 Claude Code 的 hooks 实时反映当前会话状态。它不抢焦点、不在任务栏占按钮，安装也几乎零操作——**把文件夹交给 Claude Code，让它自己读说明书装好**。

---

## 状态一览

| 灯 | 文字 | 含义 |
|---|---|---|
| 🟡 黄·闪烁（黄↔橙） | `Active`   | 正在干活（你发了消息 / 它在调用工具）|
| 🟢 绿·闪烁（绿↔黑） | `Finished` | 这一轮刚刚答完 |
| 🟢 绿·常亮          | `Idle`     | 空闲待命 |
| 🔴 红·常亮          | `Ask`      | 需要你处理（权限请求 / 在等你输入）|
| ⚪ 白·常亮          | `Claude`   | 没有活跃会话 |

> 多个 Claude 会话同时开着时，按优先级显示最要紧的那个：`Ask` > `Active` > `Finished` > `Idle`。
> 文字、颜色、大小、字体都可自定义（见下文「配置」）。

---

## 功能特性

- 🚦 **实时状态**：基于 Claude Code hooks，几乎零延迟反映工作状态。
- 🟢 **矢量指示灯**：内置 10 色 Noto Emoji 圆形图标（SVG），按显示尺寸实时矢量渲染，放多大都清晰。
- 🎨 **配色可换**：每个状态用哪种颜色都能在 `config.json` 里自由组合（10 色任选）。
- 🎛️ **高度可配置**：整体缩放、指示灯独立缩放、字体、字重（100–900）、文字、闪烁间隔、位置等。
- 🪟 **不打扰**：无边框、置顶、不抢焦点、不在任务栏产生按钮；全屏应用时自动隐藏。
- 🖱️ **可交互**：左键拖动微调位置（自动记忆），右键菜单暂停/重新定位/退出。
- 🧩 **多会话聚合**：同时跟踪多个会话，显示最重要的状态。
- 🪶 **轻量**：单个 .NET Framework exe，无需安装运行时；挂件没开时 hooks 也不会拖慢 Claude。

---

## 环境要求

- **Windows 10 / 11**
- 已安装 **[Claude Code](https://claude.com/claude-code)**（既然你在看这个工具，应该已经装了）
- `curl`（Win10 1803+ / Win11 自带）
- `.NET Framework 4.x`（Win10/11 自带，仅从源码构建时需要）

---

## 安装（交给 Claude 自己装）

这个工具的安装方式很特别：**你不用手动敲命令，让 Claude Code 读随附的说明书替你装。**

1. **获取文件**，二选一：
   - 下载 [**最新 Release**](https://github.com/ALANGMeow/ClaudeStatus/releases/latest)（`ClaudeStatus.zip` 或 `ClaudeStatus.7z`）并解压；
   - 或克隆源码：`git clone https://github.com/ALANGMeow/ClaudeStatus.git`
2. **在该文件夹打开 Claude Code**（在文件夹里启动 `claude`）。
3. **对 Claude 说一句**，例如：
   
   > 帮我接入 ClaudeStatus
   
   或「帮我装上这个状态灯」之类。
4. Claude 会自动读取同目录的 `SETUP_FOR_CLAUDE.md`，**全程替你完成**：环境检查 → 构建/校验 exe → 写入 hooks 到你的 `settings.json` → （可选）设置开机自启 → 启动并验证。期间它会用问答的形式问你几个偏好（开机自启、大小、指示灯缩放、字体、字重等）。
5. 装好后**无需重启 Claude Code** 即可开始联动，应该立刻就能看到状态了。

> 说明：hooks 理论只对**新会话**生效，不过 Claude Code 在安装过程中会**自动重载**配置文件，所以安装完当前会话会立刻亮，如果没有，重启 Claude Code 后就好了。

---

## 配置

配置都在挂件目录的 `config.json` 里（挂件启动时读取，**改完需重启挂件生效**）。你也可以直接对 Claude 说「把状态灯调大一点 / 换成 XX 字体 / 文字改成 YY」，让它帮你改。

常用字段（括号内为默认值）：

| 字段 | 说明 |
|---|---|
| `Scale` (1.0) | 整体缩放，**同时**影响文字和窗口 |
| `DotScale` (1.5) | 指示灯**额外**缩放，独立于字体；`2.0` = 灯再大一倍 |
| `FontName` ("Segoe UI") | 文字字体（需系统已安装）|
| `FontWeight` (400) | 字重，GDI `lfWeight` 100–900；400=标准、700=加粗 |
| `TextRunning` / `TextWaiting` / `TextIdle` / `TextAttention` / `TextNone` | 五种状态文字 |
| `BlinkMs` (500) | 闪烁间隔（毫秒）|
| `WaitingDecaySec` (60) | `Finished`（绿闪）多少秒后衰减为 `Idle` |
| `RightGap` / `OffsetX` / `OffsetY` | 相对托盘的位置微调 |
| `HideOnFullscreen` (true) | 全屏应用时是否自动隐藏 |
| `Port` (51234) | 本地 HTTP 端口（与 hooks 命令里的端口一致即可）|

### 指示灯配色

指示灯图标是 `assets/` 下的 Noto Emoji SVG，程序内置了 10 种颜色，可在 `config.json` 中为每个状态任意指定（**只能从这 10 色里挑，不支持自己新增**）：

`white` · `red` · `orange` · `yellow` · `green` · `black` · `blue` · `purple` · `brown` · `hollow`（空心红圈）

| 字段 | 默认 | 说明 |
|---|---|---|
| `ColorRunning` / `ColorRunningBlink` | `yellow` / `orange` | 运行中（闪烁时两色交替）|
| `ColorWaiting` / `ColorWaitingBlink` | `green` / `black` | 一轮结束（闪烁时两色交替）|
| `ColorAttention` | `red` | 需要你处理 |
| `ColorIdle` | `green` | 空闲 |
| `ColorNone` | `white` | 无会话 |

例如想把「运行中」改成蓝灯、闪烁配紫：`"ColorRunning":"blue","ColorRunningBlink":"purple"`。改完重启挂件生效。

---

## 工作原理

Claude Code 触发事件时，hooks 用 `curl` 把事件 JSON 发到挂件监听的本地端口（默认 `127.0.0.1:51234`）：

```
Claude Code -> hook(curl) -> 127.0.0.1:51234 -> ClaudeStatus 挂件 -> 任务栏指示灯
```

挂件根据 `UserPromptSubmit` / `PreToolUse` / `Notification` / `Stop` / `SessionStart` / `SessionEnd` 等事件维护每个会话的状态并聚合显示。挂件没运行时，`curl` 会瞬间失败（命令带 `-m 1` 超时兜底），**完全不影响 Claude Code 的正常使用**。

---

## 从源码构建

```bat
build.bat
```

会用系统自带的 .NET Framework 编译器（`csc.exe`）生成 `ClaudeStatus.exe`，无需安装任何 SDK。（你也可以直接让 Claude 帮你构建。）

---

## 卸载

最简单：对 Claude 说「卸载 ClaudeStatus」，它会按说明书清理。手动的话：

1. 结束进程：`Stop-Process -Name ClaudeStatus -Force`
2. 删除开机自启快捷方式：`shell:startup` 下的 `ClaudeStatus.lnk`
3. 从 `~/.claude/settings.json` 的 `hooks` 里移除指向 `127.0.0.1:<port>/...` 的那几条 curl 命令
4. 删除整个工具文件夹

---

## 许可证

本项目采用 [Apache License 2.0](LICENSE) 开源。

Copyright © 2026 ALANGMeow

---

## 致谢

- 指示灯图标来自 [Google Noto Emoji](https://github.com/googlefonts/noto-emoji)（Apache License 2.0），相关版权与许可见 [`NOTICE`](NOTICE) 与 [`assets/LICENSE`](assets/LICENSE)。

---

## 免责声明

这是一个**社区开源工具**，与 Anthropic 没有任何隶属或背书关系。"Claude" 与 "Claude Code" 是 Anthropic 的商标，此处仅用于说明本工具的用途。
