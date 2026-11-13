<p align="center">
  <img src="./PicLaundry.png" alt="PicLaundry 应用图标" width="128" height="128">
</p>

<h1 align="center">PicLaundry</h1>

<p align="center">把截图和剪贴板图片挂在 Windows 11 屏幕顶端，随手复制、预览或拖入其他应用。</p>

<p align="center">
  <a href="#功能亮点">功能亮点</a> ·
  <a href="#隐私">隐私</a> ·
  <a href="#系统要求">系统要求</a> ·
  <a href="#安装">安装</a> ·
  <a href="#使用提示">使用提示</a> ·
  <a href="#构建和运行">构建和运行</a> ·
  <a href="#验收">验收</a> ·
  <a href="#项目结构">项目结构</a> ·
  <a href="#旧版数据">旧版数据</a> ·
  <a href="#致谢">致谢</a> ·
  <a href="#许可">许可</a>
</p>

<p align="center">
  <a href="../../releases/latest">下载最新版</a> ·
  <a href="../../issues">问题反馈</a>
</p>

## 功能亮点

**截图与剪贴板**

- 默认监听 Windows 的「屏幕截图」目录，也可以在设置里选择其他目录。
- 可开启剪贴板图片收集。截图同时通过文件和剪贴板进入时，只挂一张。

**屏幕顶部的晾衣绳**

- 鼠标停在屏幕顶部片刻即可展开，也可用 `Ctrl + Alt + Space` 或托盘菜单打开。快捷键可在设置里修改。
- 按住图片就能沿绳重新摆放，留空或部分重叠都可以。位置和叠放顺序会保留。
- 滚轮横向浏览。需要一直看到图片时，可在设置里开启「一直显示晾衣绳」。

**复制、预览与拖放**

- 单击复制原图，双击打开预览。预览里可以用滚轮缩放、按住左键平移，按 `Esc` 关闭。
- 把图片拖到其他应用，可插入图片或添加附件，具体取决于接收应用。松手后，图片会挂回晾衣绳。
- 右键可摘下图片。拖到 Windows 回收站时，回收的是图片副本，截图原文件仍留在原来的目录。

## 隐私

- 图片历史和设置保存在本机，晾衣绳最多保留 50 张图片。
- 剪贴板图片收集默认关闭。开启后，可以随时从托盘暂停收集。文字和链接不会挂上晾衣绳。
- 可在托盘菜单里清空历史。清空时会保留截图原文件。

## 系统要求

- Windows 11 x64
- 便携包内含 .NET 运行时，无需另行安装，也无需管理员权限。

## 安装

1. 从 [GitHub Releases](../../releases) 下载 `PicLaundry-win-x64.zip`。
2. 解压整个压缩包，双击 `PicLaundry.exe`。程序启动后会出现在任务栏通知区域的托盘里，图标也可能在折叠菜单中。
3. 在 Windows 截图工具中开启自动保存，截一张图试试。若截图保存到了自定义目录，在 PicLaundry 的托盘设置里选同一个目录。

## 使用提示

- 拖动中按 `Esc` 可以取消，图片会回到原位。拖到晾衣绳左右边缘可以继续滚动。
- 「一直显示晾衣绳」会将晾衣绳留在主屏顶部，其他应用窗口可以盖住重叠部分。
- 要开机启动，按 `Win + R` 打开 `shell:startup`，把 `PicLaundry.exe` 的快捷方式放进去。

## 构建和运行

在 Windows 安装 .NET 8 SDK，在仓库根目录执行以下 PowerShell 命令。

```powershell
dotnet publish PicLaundry.csproj -c Release -r win-x64 --self-contained true -o dist/PicLaundry-win-x64
.\dist\PicLaundry-win-x64\PicLaundry.exe
```

分发便携版时保留发布目录中的全部文件。可用 `PICLAUNDRY_DATA_DIR` 指定独立的数据目录，旧版的 `HILINE_DATA_DIR` 和 `HANGLINE_DATA_DIR` 也可用。

### 验收

在 Windows 桌面会话中退出 PicLaundry，再运行以下命令。验收使用独立的测试目录，并会向系统剪贴板写入测试图片和文字。

```powershell
dotnet run --project Verification/Verification.csproj -c Release -- (Join-Path $env:TEMP ("PicLaundry-check-" + [guid]::NewGuid()))
```

双屏混合缩放、全屏窗口和系统截图工具的手动检查见 [ACCEPTANCE.md](ACCEPTANCE.md)。

## 项目结构

```text
.
├── PicLaundry.csproj      .NET 8 Windows 桌面项目
├── Program.cs             应用入口、托盘、快捷键和剪贴板收集
├── ScreenshotCollector.cs 截图目录监听
├── Shelf.cs               晾衣绳窗口和图片卡片
├── Shelf.Click.cs         单击复制和双击预览
├── Shelf.Drag.cs          图片拖放和位置调整
├── Shelf.Motion.cs        卡片悬停和动画
├── DragPreview.cs         拖动时跟随鼠标的缩略图
├── ImagePreview.cs        原图预览、缩放和平移
├── History.cs             图片历史、去重和持久化
├── Settings.cs            设置窗口和配置
├── Native.cs              Windows 系统接口
├── RecycleBin.cs          图片副本回收
├── Verification/         自动验收项目
└── ACCEPTANCE.md          验收清单
```

## 旧版数据

PicLaundry 默认使用 `%LOCALAPPDATA%\PicLaundry`。从 Hiline 或 Hangline 升级时，如果新目录尚未创建，程序会沿用已有的旧版数据目录，避免移动历史记录引用的图片。

## 致谢

晾衣绳交互取材于 Alejandro Buján 的 [Tendedero](https://github.com/alejandrobujan/tendedero)。

感谢 Codex 在 PicLaundry 开发和发布过程中提供的帮助。

## 许可

[MIT](LICENSE)。原作者署名及 Tendedero 的名称和图标说明见许可证。
