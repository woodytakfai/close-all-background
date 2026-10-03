# 一键关闭后台应用 (CloseAllBackground)

一个 Windows 原生小工具：**双击 → 点一下红色按钮 → 所有后台应用被关闭**。

- 单文件绿色 EXE，**86 KB**，零依赖（只用系统自带的 .NET Framework 4.x，Win7 及以上都能跑）
- 无广告、无联网、无安装、无注册表写入
- 关闭前把要关的东西**如实列给你看**（包括会连带关闭的子进程），确认后才动手

## 下载

**[⬇ 点这里下载最新版 EXE](https://github.com/woodytakfai/close-all-background/releases/latest/download/CloseAllBackground-v1.0.0.exe)**（88 KB，双击即用）

或者从 [Releases 页面](https://github.com/woodytakfai/close-all-background/releases/latest) 下载。
想自己编译的话，克隆本仓库后运行 `build.ps1` 即可，不需要装 Visual Studio。

## 怎么用

双击下载到的 EXE（或仓库里的 `dist\一键关闭后台应用.exe`）：

1. 程序自动扫描当前登录会话里的后台程序（托盘图标类、无窗口类）
2. 列表里默认**全部勾选**，可以取消勾选某些程序
3. 点 **⚡ 一键关闭 X 个后台应用**

不需要界面、只想脚本化调用时：

```bat
一键关闭后台应用.exe --list                  :: 列出所有可关闭的后台应用
一键关闭后台应用.exe --dry-run               :: 只显示计划，不真关
一键关闭后台应用.exe --all --yes             :: 静默关闭全部后台应用
一键关闭后台应用.exe --pid=1234,5678         :: 只关指定 PID
一键关闭后台应用.exe --pid=1234 --out=报告.txt
```

| 参数 | 作用 |
|---|---|
| `--include-windows` | 连有窗口的应用一起关 |
| `--include-system` | 连 Windows 系统组件一起关（慎用） |
| `--include-services` | 连会话 0 服务一起关（需管理员，慎用） |
| `--no-tree` | 不连带关闭目标程序的子进程 |
| `--force` | 跳过"正常退出"，直接强制结束 |
| `--json` | 以 JSON 输出列表 |
| `--out=文件` | 把报告写入文件（UTF-8） |

退出码：`0` 成功 / `1` 参数错误 / `2` 有进程没关掉 / `3` 全部被安全闸门拦下。

## 关闭策略：先礼后兵

1. 先向目标窗口发送 `WM_CLOSE`，让它**像你点右上角×一样自己退出**（该弹保存提示的会弹）
2. 等 1.5 秒还没退出，才**强制结束进程**
3. 同时结束它的**子进程**（先杀子、后杀父）

界面上的「优先正常退出（推荐）」控制第 1 步，「连带关闭子进程」控制第 3 步。
**确认对话框里会把子进程一并列出来**——你批准 N 个，就只会关这 N 个。

## 什么永远不会被关掉

无论勾了什么、选了什么选项，以下都**绝对不会**被关闭（点「受保护进程」可看完整清单）：

| 类别 | 例子 |
|---|---|
| Windows 关键进程 | `csrss` `winlogon` `lsass` `services` `wininit` `smss` `dwm` `svchost` `fontdrvhost` `audiodg` `spoolsv` `WmiPrvSE` … |
| 桌面外壳 | `explorer`（任务栏）、`StartMenuExperienceHost`、`TextInputHost`（输入法）、`RuntimeBroker`、`conhost` … |
| 安全组件 | `SecurityHealthService`、`MsMpEng`、`SearchIndexer` … |
| 程序自身 | 本进程 PID，以及**本程序的另一个实例** |
| 父进程链 | 启动本程序的那条链（终端、启动器等）全部被保护 |
| 其他登录会话 | 远程桌面/其他用户会话里的程序（EnumWindows 是会话隔离的，它们在别的会话里看像"没窗口"，最容易被误伤） |

子进程也走同一套闸门：即使某个后台应用的子进程正好是 `explorer.exe` 或 `conhost.exe`，也只会被**跳过并记录**，不会被连带杀掉。

## 四道额外保险

1. **PID 复用防护**——确认框停留期间目标自己退出了、PID 被系统回收给别的程序时，会比对进程名 + 启动时间，发现不一致就跳过并报告，绝不误杀。
2. **权限不足会如实报告**——用原生 `OpenProcess`/`GetExitCodeProcess` 区分"进程已经没了"和"进程还在但我碰不到"。高权限进程关不掉时会明确写"拒绝访问（请以管理员身份运行）"，**不会假装成功**。
3. **`--pid` 也过闸门**——`--pid=<explorer 的 PID>` 会被直接拒绝，不会因为没有界面确认就绕过保护名单。
4. **读不到路径就"失败关闭"**——拿不到可执行文件路径时，不敢断定它是普通应用，一律归入「系统组件(路径不可读)」，必须显式勾选「包含系统组件」才处理。

## 关于"后台应用"的判定

同时满足才算：

- 运行在**当前登录会话**（既不是会话 0 服务，也不是别的用户/远程桌面会话）
- 可执行文件路径**可读**，且**不在 `C:\Windows` 和 `Program Files\WindowsApps`** 里
- **没有可见主窗口**（有窗口的程序默认不算，除非勾了「包含有窗口的应用」）

所以微信、QQ、Steam、NVIDIA、Edge 后台进程这类会被识别；Windows 自带组件要显式勾选才会纳入。

## 常见问题

**有些程序关不掉，提示"拒绝访问"？**
那是更高权限（管理员/SYSTEM）的进程。点「以管理员身份重启」，再关一次。

**怕误关某个程序？**
列表里右键它 → **永不关闭此程序**，会写入 `%APPDATA%\CloseAllBackground\exclude.txt`，以后扫描时它直接显示为"受保护"。

**程序崩了怎么办？**
异常会自动写到 `%TEMP%\CloseAllBackground-crash-<时间>.log` 并弹窗提示，日志里有完整调用栈。

> ⚠️ **注意**：DeepSeek Harness、IDE、正在跑任务的终端，只要没有可见主窗口，就会被当成后台应用。**如果你正在 Harness 里跑任务，别点这个按钮**——除非你就是想把它们全关掉。只有在"启动它的那条父进程链"上的实例才会自动豁免。

## 已知限制

- 父进程链保护靠遍历 PPID 实现。如果启动本程序的是一个**已经退出的**启动器（Squirrel/ClickOnce 这类"更新完就退出"的壳），链会断在那里，更上层的祖先进程不会被保护。
- 子进程递归展开有 12 层深度上限，极深的进程树可能漏掉末端。
- 提示"路径不可读"的进程在普通权限下本来就关不掉，归入系统组件只是避免误判。

## 目录结构

```
CloseAllBackground\
├─ dist\一键关闭后台应用.exe     ← 交付物，直接双击运行
├─ src\                          源代码
│   ├─ Program.cs                入口 + 命令行
│   ├─ MainForm.cs               图形界面 + 确认对话框
│   ├─ Scanner.cs                进程扫描与安全分类
│   ├─ Killer.cs                 关闭计划 + 执行引擎
│   ├─ Native.cs                 Win32/NT API 封装
│   ├─ Models.cs                 数据模型
│   ├─ AssemblyInfo.cs           版本信息
│   ├─ app.manifest              DPI / 权限清单
│   └─ app.ico                   图标
├─ tools\
│   ├─ make_icon.py              图标生成脚本
│   ├─ Victim.cs                 测试夹具（假后台应用）
│   ├─ ConfirmPreview.cs         确认对话框排版预览器
│   └─ publish.ps1               发布到 GitHub（走 API，无需安装 git）
├─ test\
│   ├─ run_tests.ps1             功能验收测试（19 项）
│   └─ run_hardening_tests.ps1   安全加固验收测试（18 项）
├─ screenshot.png                界面截图
├─ build.ps1                     一键重新编译
└─ LICENSE                       MIT 协议
```

## 重新编译

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

用的是 Windows 自带的 `csc.exe`，**不需要 Visual Studio，也不需要联网**。

## 验收测试

```powershell
powershell -ExecutionPolicy Bypass -File test\run_tests.ps1
powershell -ExecutionPolicy Bypass -File test\run_hardening_tests.ps1
```

共 **37 项**，全部通过。覆盖：

- 真实关杀无窗口进程 / 对窗口程序走正常退出 / 进程树连带关闭
- 自身、父进程链、程序第二个实例的保护
- **把一个无害程序改名成 `explorer.exe`**，验证保护名单在 `--pid` 和进程树两条路径上都能拦住它（这样测不用拿真的资源管理器冒险）
- 系统关键进程零泄漏、恶意参数（`--pid=abc` / `--pid 123` / 未知开关）全部被拒绝
- 读不到路径的进程不会进入候选、跨会话进程不会进入候选、`C:\Windows` 下的程序不会进入候选
- `--all` 不带 `--yes` 时绝不执行

测试只使用临时造出来的假进程，不会碰你机器上的真实程序。

## 开源协议

[MIT](LICENSE) —— 随便用、随便改、随便分发，出事不负责。
