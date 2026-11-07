# MacDock · macOS 风格 Dock（Windows 11）

![Dock](docs/images/macdock-top.png)

![整屏](docs/images/macdock-desktop.png)

> **独立项目，不是任何项目的官方版本。**
> 起点代码来自 [**lupanostefano/Gravitone**](https://github.com/lupanostefano/Gravitone)（MIT License，© 2026 Stefano Lupano），此后按自己的方向开发与维护。
> 与上游作者无关；问题请提到本仓库，不要打扰上游。

屏幕**顶部**是一条 macOS 风格的 **Dock**，配一个全屏**启动台**；Windows 原生任务栏留在底部——托盘图标、输入法、时钟、运行中窗口都还在系统原来那套里，不自绘第二份。

---

## 有什么

### 位置与几何

- Dock **贴在屏幕顶边居中**，注册为 AppBar，但只让开 **24 px**（macOS 菜单栏的厚度）；玻璃板本身约 54 px，多出来的部分**浮在窗口上面**，和 macOS Zoom 后 Dock 悬浮在桌面上是同一种层次感
- **自动隐藏**：平时收进屏幕外，鼠标推到最上沿滑出，移开再收起
- 有应用正在启动（图标在跳）或收到通知时**不收起**——否则启动过程全在屏幕外
- 多显示器时跟随设置里选定的那块屏

### 图标行为

- **悬停放大**：指针附近按余弦曲线放大，被指的那个图标位置不漂移，两侧依次递减
- **通知呼吸**：收到通知时，那个图标连同左右邻位一起做正弦起伏（周期 1.3 s，中心约 ±27%，邻居只轻微跟），连续不断，直到你把窗口切到前面或超过 20 s
  - 呼吸和悬停是**两条独立通路**，鼠标停在 Dock 上时通知图标照样自己鼓
- 运行提示点、启动弹跳、悬停名称
- 点图标：没开就启动；已开就前置；已在前台则最小化；多窗口时依次切换
- `Shift` / 中键新开窗口；右键看最近文件、窗口列表、在程序坞中保留、退出
- 拖拽排序、**拖出移除**（横向/纵向都判，误触阈值经过实测）、拖入任意 exe / 快捷方式 / 文件夹；文件夹变成**堆栈**，回收站是**真回收站**
- **网页应用**：右键「添加网页应用…」，或直接把网址拖到 Dock 上
- 玻璃板：三层渐隐阴影 + 圆角对齐设备像素 + 内外两道渐变高光；顶部时阴影只朝下，不会漏到板子下方

### 往 Dock 里加东西

三种方式，都是一次性生效、不用重启 Dock：

1. **资源管理器右键 →「添加到拓展坞」**（文件和文件夹都有这一项）
2. 把文件 / 快捷方式 / 文件夹直接拖到 Dock 上
3. 设置窗口里手动加条目

右键菜单那条走 `%LOCALAPPDATA%\MacDock\add-to-dock.vbs`：先设好 `DOTNET_ROOT` 再以无窗口方式调 `MacDock.exe --add-to-dock "%1"`，写完配置广播 `MacDock.ReloadConfig`，运行中的 Dock 立刻重读并补建图标。

### 启动台

- 扫描开始菜单，过滤发行版噪声与系统工具（约 200 个快捷方式 → 70~80 个应用）
- **拼音首字母 + 子序列搜索**（`wx` → 微信，`wyyy` → 网易云音乐）
- 分页 + 方向键 / 滚轮翻页
- 扫描结果缓存，打开约 0.4 秒
- 背景是**打开前那一帧屏幕的模糊版** + 一层 75% 的 `#1C1C1E` 暗色，深色下能透出壁纸而不是一块黑板
- `F4` 开关（虚拟键码可在配置里改）

### 和 Windows 任务栏的关系

| 项 | 默认行为 |
| --- | --- |
| 顶部 | **MacDock 的 Dock**（自动隐藏，让开 24 px） |
| 底部 | **Windows 原生任务栏**，一切照旧 |
| 是否隐藏原生任务栏 | **否**（`ReplaceTaskbar: false`）。想要独占任务栏位置，在 Dock 右键或设置里打开「替换 Windows 任务栏」 |

Dock 和任务栏可以各自换边：任务栏挪到底部、Dock 放顶部互不打架；判断"要不要给原生任务栏让位"只看**它是不是占了 Dock 那一条边**，不是看它可不可见。

## 构建

需要 .NET 9 SDK（Windows）。

```powershell
.\tools\build.ps1                 # 编译
.\tools\publish.ps1               # 发布到 dist\MacDock（框架依赖）
.\tools\install.ps1 -NoStart      # 安装到 %LOCALAPPDATA%\MacDock\app
```

> ⚠️ **不要用 self-contained / ReadyToRun 发布**：实测会让 ManagedShell 的托盘服务初始化失败。
> 框架依赖 + `MacDock.vbs`（设好 `DOTNET_ROOT` 再启动）是验证过的组合。
> 同理，任何直接调用 `MacDock.exe` 的地方（右键菜单、快捷方式）都要先有 `DOTNET_ROOT`，否则弹「You must install or update .NET」。

其它脚本：`verify.ps1`（构建+校验）、`shot.ps1 -Region dock|top|full`（DPI 感知截图）、`add-webapp.ps1`、`quit.ps1`、`run.ps1`。

## 安装 / 卸载

```powershell
# 卸载并还原 Windows 任务栏（配置保留在 %APPDATA%\MacDock）
.\tools\uninstall.ps1

# 连配置一起清掉
.\tools\uninstall.ps1 -Purge
```

卸载会一并删除「添加到拓展坞」的两个注册表键。

紧急还原（不进 UI、不依赖任何标记文件）：

```powershell
%LOCALAPPDATA%\MacDock\app\MacDock.exe --restore-taskbar
```

## 配置

| 路径 | 内容 |
| --- | --- |
| `%APPDATA%\MacDock\config.json` | Dock 项、文件夹、全部设置 |
| `%APPDATA%\MacDock\log.txt` | 运行日志（全屏判定、置顶、激活、资源占用都记在这里） |

常改的几个：

| 键 | 现在 | 说明 |
| --- | --- | --- |
| `Edge` | `"Top"` | `Top` / `Bottom` / `Left` / `Right` |
| `AutoHide` | `true` | 收起 + 鼠标触顶滑出 |
| `IconSize` | `32` | 静止图标（DIU），玻璃板高度 ≈ IconSize + 2×Pad |
| `MagnifiedSize` | `44` | 悬停放大上限（DIU） |
| `LaunchpadKey` | `115` | 启动台热键，`115` = F4 |
| `ReplaceTaskbar` | `false` | 是否隐藏原生任务栏 |

> 改 `Edge` / `IconSize` 这类值要重启 Dock；`AutoHide`、热键也是。

## 快捷键

| 按键 | 作用 |
| --- | --- |
| `F4` | 打开 / 关闭启动台（`LaunchpadKey` 可改，写虚拟键码） |
| `Ctrl+Alt+Shift+B` | 临时显示 / 隐藏 Windows 原生任务栏（仅在启用「替换任务栏」时有意义） |

## 命令行

| 命令 | 作用 |
| --- | --- |
| `--add-to-dock <path…>` | 把文件/快捷方式/文件夹追加进固定项，并让运行中的 Dock 热重载 |
| `--restore-taskbar` | 不进 UI、直接还原 Windows 任务栏 |
| `--quit` | 让运行中的 Dock 正常退出（任务栏还原、AppBar 释放） |
| `--set-autostart on\|off` | 开关开机自启 |
| `--version` / `--selftest` | 版本 / 自检 |

## 已知限制

- **「关闭到托盘」的程序**：托盘图标点击是唯一能让应用重置内部状态的路径，我们从外部只能显示窗口。所以这类程序由 Dock 唤起时，会再启动一次它自己、靠单实例转发恢复；不认这条的程序可能因此多开一个窗口。
- **窗口激活时标题栏按钮会重绘一帧**：Windows 在「非活动 → 活动」时对 —/□/× 的正常绘制，任何激活方式（Dock、任务栏、Alt+Tab）都一样。
- **Copilot 键**由厂商/系统在更底层处理，低级键盘钩子收不到，无法绑定。
- **UIPI**：MacDock 以普通权限运行，当前台是管理员权限窗口时键盘钩子收不到按键（`F4` 不响应）。让 MacDock 以管理员运行可以解决，但会失去与资源管理器的拖放。
- **开机首帧**仍有约 1.3–5.7 秒的 .NET/WPF 冷装载（加开机预热任务可压到 ~1.3 秒，取决于磁盘）。
- **独占全屏**（Exclusive Fullscreen）下任何 WPF 叠加层都画不出来，游戏需改用无边框全屏。
- **全屏判定**：只认四边贴满屏幕的窗口。有些应用（Flutter 播放器、部分截图工具）的无边框全屏也会被判定为全屏，此时 Dock 会整体收起。

## 许可

MIT。起点代码的版权归 **Stefano Lupano**；本项目新增与修改部分的版权归 **sujiu0616-art**。
详见 [`LICENSE`](LICENSE) 与 [`NOTICE.md`](NOTICE.md)。第三方组件见 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。
