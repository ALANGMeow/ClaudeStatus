# ClaudeStatus 接入手册（本文件是写给 Claude 读的，不是给最终用户读的）

> 你（Claude Code）正在一个包含本工具的文件夹里被启动。用户对你说了类似「接入这个小工具 / 帮我装上这个状态灯」之类的话。
> 请**严格按本手册执行**：先做环境检查 → 用 `AskUserQuestion` 工具向用户提问 → 根据回答自动完成全部配置（构建、写 hooks、可选开机自启）→ 启动程序并验证。
> 全程你来做，不要让用户手动敲命令。每一步出错要把原因和解决办法清楚地告诉用户。

---

## 0. 这个工具是什么

一个 Windows 任务栏小挂件：在任务栏托盘图标区左侧显示一个**矢量彩色指示灯（圆形 emoji 图标）+ 文字**，实时反映当前 Claude Code 的工作状态（红绿灯隐喻）。
它通过 Claude Code 的 **hooks** 把事件用 `curl` 发到挂件内置的本地 HTTP 端口（默认 `127.0.0.1:51234`）来感知状态。挂件本身是一个 .NET Framework 编译的单文件 exe，常驻运行、不抢焦点、不在任务栏产生按钮。指示灯图标是同目录 `assets\` 下的 Noto Emoji **SVG**（程序内置极简 SVG 渲染器，按显示尺寸实时矢量光栅化，任意缩放都清晰），内置 10 种颜色、每个状态用哪种色可在 `config.json` 配置；文字用 GDI 渲染、支持任意字重；指示灯可独立于字体单独缩放。

**默认状态灯方案（源码 Config 默认值，稍后要展示给用户看）：**

| 灯 | 文字 | 含义 | 触发事件 |
|---|---|---|---|
| 🟡 黄·闪烁（黄↔橙交替） | `Active` | 正在干活 | `UserPromptSubmit` 起，到 `Stop` 止 |
| 🟢 绿·闪烁（绿↔黑交替） | `Finished` | 这一轮刚结束 | `Stop` 后 `WaitingDecaySec`(默认60)秒内 |
| 🟢 绿·常亮 | `Idle` | 空闲待命 | 上面绿闪衰减后 |
| 🔴 红·常亮 | `Ask` | 需要你处理（权限/提问） | 非空闲的 `Notification` |
| ⚪ 白·常亮 | `Claude` | 无活跃会话 | 全部 `SessionEnd` 后 |

多个 Claude 会话并存时，按优先级取最重要的显示：`Ask` > `Active` > `Finished` > `Idle`。

> 灯的渲染：黄灯闪烁是**黄↔橙交替**，绿灯闪烁是**绿↔黑交替**（均为两张图标切换，不再用透明度明灭）。内置 10 色：`white`/`red`/`orange`/`yellow`/`green`/`black`/`blue`/`purple`/`brown`/`hollow`(空心红)，各状态用色见第 4 节 `ColorXxx` 字段。若 `assets\` 的 SVG 缺失/解析失败，对应状态自动回退为纯色实心圆，不影响运行。

> 说明：如果用户**没有**开启 `bypassPermissions`，那么每次工具授权都会触发 `Ask`（红灯），这是正常且符合预期的。

---

## 1. 环境前置检查（先全部确认，缺啥告诉用户）

依次确认（用 PowerShell / Bash 工具）：

1. **操作系统**：必须是 Windows 10/11。本工具仅支持 Windows。
2. **当前工具目录**：确定本文件所在的绝对路径，记为 `$DIR`（后续所有路径都基于它）。同目录应有 `ClaudeStatus.cs`、`app.manifest`、`build.bat`、`config.json`、`assets\`（内含 `emoji_u*.svg` 共 10 个指示灯图标 + `LICENSE`），通常还有 `ClaudeStatus.exe`。指示灯 SVG 在运行时读取，需随 exe 一起存在。
3. **curl**：`curl.exe` 是否存在（`Get-Command curl.exe`）。Win10 1803+ / Win11 自带。没有的话 hooks 无法上报，需提醒用户。
4. **.NET Framework 编译器 csc**：路径 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` 是否存在（Win10/11 自带 .NET Framework 4.x）。用于本地构建 exe。
5. **用户 settings.json 路径**：`"$env:USERPROFILE\.claude\settings.json"`（记为 `$SETTINGS`）。可能不存在，稍后若无则创建。

---

## 2. 构建 / 校验 exe

为避免分享过程中 exe 损坏或被杀软拦截，**优先本地重新构建**一个干净的二进制：

```
& "$DIR\build.bat"
```

- 成功会输出 `BUILD OK: ...\ClaudeStatus.exe`。
- 若 `build.bat` 失败但 `$DIR\ClaudeStatus.exe` 已存在，可直接用现成的 exe（它是 .NET Framework 程序，跨 Windows 机器可直接运行，无需安装 SDK）。
- 若 exe 不存在且构建失败：把 csc 的报错原文给用户，停止流程。

---

## 3. 向用户提问（用 AskUserQuestion 工具）

用 `AskUserQuestion` 提出下面这些问题。**外观问题必须把上面第 0 节的「默认状态灯方案」表格内容讲给用户听**（写在问题描述里），让用户知道当前默认长什么样再决定是否改。

> `AskUserQuestion` 每次最多 4 问，下面共 6 项，分两批问即可（第一批 1-4，第二批 5-6）。可按需精简，但「开机自启」「状态文字/外观」两项要有。

1. **开机自启**：是否希望开机自动启动这个状态灯？
   - 选项：`是（推荐）` / `否`
2. **状态文字 / 外观**：是否要修改默认的文字 / 颜色 / 状态映射？（在描述里展示当前默认方案表）
   - 选项：`保持默认（推荐）` / `要自定义`
3. **挂件整体大小** `Scale`：默认 `1.0`（约 132×28 像素）。`Scale` 同时缩放**文字与窗口**。
   - 选项：`保持默认` / `更大一些` / `更小一些`
4. **指示灯缩放** `DotScale`：默认 `1.5`。在 `Scale` 基础上**只**再放大指示灯、**不影响字体**；`2.0` = 灯再放大一倍。
   - 选项：`保持默认（推荐）` / `更大一些` / `更小一些`
5. **字体** `FontName`：默认 `Segoe UI`（Windows 自带）。是否指定其它字体？
   - 选项：`保持默认（推荐）` / `指定其它字体`（追问字体名，并按第 4 节做可用性检查）
6. **字重** `FontWeight`：默认 `400`（标准）。GDI 字重，范围 100-900。
   - 选项：`标准 400（推荐）` / `加粗 700` / `自定义（100-900）`

（可选）**端口**：默认 `51234`，一般不用改；若被占用你会在第 6 步发现并自动换，此问可省略。

如果用户选了「要自定义」外观，再追问具体要改什么（文字建议**避免带下伸部的字母** g/j/p/q/y 以保持基线整齐；灯的颜色可在内置 10 色中为每个状态任选，改 `ColorXxx` 字段即可、无需替换图片；缩放/字体/字重都在 `config.json` 里可调，见第 4 节）。收集清楚后再写入。

---

## 4. 按回答写 config.json

`config.json` 是挂件的配置（启动时读取）。根据用户回答修改对应字段，**保留其它字段不动**。常用字段（括号内为源码默认值）：

- 整体大小：`Scale`（默认 `1.0`；更大→如 1.5/2.0，更小→如 0.8）。同时影响文字与窗口。
- 指示灯缩放：`DotScale`（默认 `1.5`）。在 `Scale` 基础上**只**再缩放指示灯、不影响字体；`2.0` = 灯再大一倍。
- 文字：`TextRunning`(=Active) / `TextWaiting`(=Finished) / `TextAttention`(=Ask) / `TextIdle`(=Idle) / `TextNone`(=Claude)
- 字体：`FontName`（默认 `Segoe UI`，Windows 自带）
- 字重：`FontWeight`（默认 `400`）。GDI `lfWeight`，范围 100-900：400=标准、700=加粗。实际效果取决于该字体是否提供对应粗细——单一字面的字体会取最接近值或对加粗做合成，想要分明的层次用多字重字体（如 `Segoe UI`、`Microsoft YaHei`）。
- 指示灯配色：`ColorRunning`(=yellow) / `ColorRunningBlink`(=orange) / `ColorWaiting`(=green) / `ColorWaitingBlink`(=black) / `ColorAttention`(=red) / `ColorIdle`(=green) / `ColorNone`(=white)。取值只能是内置 10 色之一：`white`/`red`/`orange`/`yellow`/`green`/`black`/`blue`/`purple`/`brown`/`hollow`；填了无效色名会回退到该状态默认色。
- 右边距：`RightGap`（离托盘越远就调大）
- 端口：`Port`（若第 6 步检测到占用，改成空闲端口，并确保第 5 步 hooks 用同一端口）

**字体可用性检查**（用户指定了非默认字体时务必确认已安装，否则回退）：

```powershell
Add-Type -AssemblyName System.Drawing
$ok = [System.Drawing.FontFamily]::Families | Where-Object { $_.Name -eq '<字体名>' }
```

没装就把 `FontName` 改回 `Segoe UI`（Windows 自带）或 `Microsoft YaHei`。

> 注意：`config.json` 用 UTF-8、单行 JSON 即可。改完后挂件需重启才生效（第 7 步会启动）。

---

## 5. 写入 hooks 到用户的 settings.json（核心步骤）

把下面 6 个 hook 合并进 `$SETTINGS` 的 `"hooks"` 对象里。**务必保留用户原有的所有配置和已有 hooks，只做合并/追加，不要整体覆盖。**

- 先读取 `$SETTINGS`（不存在就以 `{}` 起步）。
- 取 `config.json` 里的 `Port`（默认 51234），把下面命令里的端口替换成它。
- 对每个事件：若该事件已存在 hook 列表，则**追加**我们的 entry；若不存在则新建。

要写入的 hooks（端口按 config.json 的 Port 替换）：

```json
{
  "hooks": {
    "UserPromptSubmit": [
      { "hooks": [ { "type": "command", "command": "curl -s -m 1 --data-binary @- http://127.0.0.1:51234/UserPromptSubmit" } ] }
    ],
    "PreToolUse": [
      { "matcher": "*", "hooks": [ { "type": "command", "command": "curl -s -m 1 --data-binary @- http://127.0.0.1:51234/PreToolUse" } ] }
    ],
    "Notification": [
      { "hooks": [ { "type": "command", "command": "curl -s -m 1 --data-binary @- http://127.0.0.1:51234/Notification" } ] }
    ],
    "Stop": [
      { "hooks": [ { "type": "command", "command": "curl -s -m 1 --data-binary @- http://127.0.0.1:51234/Stop" } ] }
    ],
    "SessionStart": [
      { "hooks": [ { "type": "command", "command": "curl -s -m 1 --data-binary @- http://127.0.0.1:51234/SessionStart" } ] }
    ],
    "SessionEnd": [
      { "hooks": [ { "type": "command", "command": "curl -s -m 1 --data-binary @- http://127.0.0.1:51234/SessionEnd" } ] }
    ]
  }
}
```

要点：
- 命令里的 `--data-binary @-` 表示把 hook 的 stdin（事件 JSON）原样发给挂件，挂件用它解析 `session_id` 等。**不要加引号包住 `@-`**（在 Windows shell 里 `@-` 直接写即可）。
- 挂件没开时 `curl` 会瞬间连接失败，`-m 1` 兜底，**不会拖慢 Claude**，所以即使没装/没开也不影响用户正常使用。
- 写完后，**hooks 只对新的 Claude 会话生效**。当前这个会话装完后，建议提示用户「装好后重启 Claude Code（或新开一个会话）才会开始联动」。

写回 `$SETTINGS` 时保持合法 JSON（UTF-8）。改完最好再读一次确认能被 JSON 解析。

---

## 6. 端口占用处理（自动）

启动挂件前或失败后，若发现 `Port` 被占用（挂件启动会弹错或进程秒退），自动换一个空闲端口：

- 选一个新端口（如 51235、51299 之类高位端口），同时更新 `config.json` 的 `Port` **和** 第 5 步写入的 6 条 hook 命令里的端口，保持两边一致。

---

## 7. 开机自启（仅当用户在问题 1 选「是」）

在「启动」文件夹放一个指向 exe 的快捷方式：

```powershell
$startup = [Environment]::GetFolderPath('Startup')
$lnk = Join-Path $startup 'ClaudeStatus.lnk'
$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut($lnk)
$sc.TargetPath = "<$DIR>\ClaudeStatus.exe"
$sc.WorkingDirectory = "<$DIR>"
$sc.Description = "Claude Code 任务栏状态灯"
$sc.Save()
```

把 `<$DIR>` 换成实际绝对路径。若用户选「否」，跳过本步（如已存在同名快捷方式且用户选否，则删除它）。

---

## 8. 启动并验证（自动）

1. 启动（若已在运行，先结束旧进程再启动，确保读到最新 config）：
   ```powershell
   Stop-Process -Name ClaudeStatus -Force -ErrorAction SilentlyContinue
   Start-Process "<$DIR>\ClaudeStatus.exe"
   ```
2. 确认进程在跑：`Get-Process ClaudeStatus`。
3. 发一个测试事件，确认挂件能收并返回 `ok`（端口用 config 的 Port）：
   ```powershell
   '{"session_id":"setup-test","message":"Claude is waiting for your input"}' | curl.exe -s -m 1 --data-binary '@-' http://127.0.0.1:51234/Notification
   ```
   - 注意：在 **PowerShell** 里手动测试时 `@-` 要写成 `'@-'`（单引号），且 JSON 必须是合法的（用单引号包整段、内部用真双引号）。这只是你测试用；第 5 步写进 settings.json 的命令里则**不要**给 `@-` 加引号。
   - 返回 `ok` 即表示挂件 HTTP 服务正常。
4. 收尾测试态：`'{"session_id":"setup-test"}' | curl.exe -s -m 1 --data-binary '@-' http://127.0.0.1:51234/SessionEnd`，把测试会话清掉。
5. 让用户**看一眼任务栏托盘图标区的左侧**：应能看到挂件（无会话时是 ⚪ `Claude`）。若看不到，见第 9 节排查。
6. 告诉用户：**装好了，请重启 Claude Code（或新开会话）开始联动**；之后发消息会看到 🟡 `Active`，答完变 🟢 `Finished`，约一分钟后变 🟢 `Idle`。

---

## 9. 故障排查（按需告诉用户）

- **看不到挂件**：
  - 可能正处于全屏应用（游戏等）自动隐藏中：`config.json` 的 `HideOnFullscreen` 设 `false` 可禁用此行为。
  - 位置被托盘挤掉/偏移：调 `config.json` 的 `RightGap`（离托盘距离）、`OffsetX`/`OffsetY`（微调），或用鼠标左键直接拖动挂件（会记忆位置）。
  - 多显示器/缩放变化：挂件每秒自动重定位到主任务栏，稍等或重启 exe。
- **状态不更新**：确认已重启 Claude Code（hooks 仅对新会话生效）；确认 `config.json` 的 `Port` 与 settings.json 里 hook 命令端口一致；确认 `curl.exe` 存在。
- **端口冲突 / exe 秒退**：换端口（第 6 步）。
- **指示灯显示为纯色圆 / 看不到灯图**：`assets\` 下对应的 `emoji_u*.svg` 缺失或解析失败（缺失时会回退为纯色实心圆），补回 SVG 即可。也可能是 `ColorXxx` 填了无效色名（会回退到该状态默认色）。
- **中文/字体显示异常或字重无层次**：确认 `FontName` 指定的字体已安装（默认 `Segoe UI` 系统自带）；若 `FontWeight` 调了却没变化，多半是该字体只有单一字面，换 `Segoe UI`/`Microsoft YaHei` 等多字重字体。
- **想临时排查事件**：`config.json` 设 `LogEvents:true` 重启，会在工具目录写 `events.log`（记录所有收到的 hook 事件，含原始 body）；排查完设回 `false` 并删掉日志，避免无限增长。

---

## 10. 卸载

1. 结束进程：`Stop-Process -Name ClaudeStatus -Force`。
2. 删除开机自启快捷方式：`shell:startup` 下的 `ClaudeStatus.lnk`。
3. 从 `$SETTINGS` 的 `"hooks"` 里移除我们加的 6 条（指向 `127.0.0.1:<port>/...` 的 curl 命令）。
4. 删除整个工具文件夹。

---

## 附：右键菜单与交互

- 挂件**左键拖动**可微调位置（自动存入 config 的 OffsetX/OffsetY）。
- 挂件**右键**有菜单：暂停/恢复、重新定位、退出。
