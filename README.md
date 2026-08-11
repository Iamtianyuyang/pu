# pu~

> 右键视频 → `pu~` → 手机 / iPad 扫码就能看。对面不用装任何 App，浏览器打开即播。

## 当前状态：.NET 10 + WPF 桌面版

右键视频/文件夹 → **WPF 桌面窗口**（浅色界面，不弹控制台、不强制打开浏览器）：

- 二维码 + 「复制链接 / 打开链接」按钮
- 转码进度条（实时百分比 + 硬件加速说明）
- 文件夹模式：文件列表直接点播
- 托盘常驻：显示窗口 / 停止
- 网页版（/s/ 与 /f/）保留，供手机 / 平板扫码后播放

```powershell
# 开发运行
pu --register          # 注册右键菜单（34 个媒体扩展名 + 文件夹，HKCU）
```

### 发给别人（publish/pu-windows-x64.zip）

解压后：

```
pu.exe --install      # 安装到 %LOCALAPPDATA%\Pu\ 并注册右键菜单（无需管理员）
pu.exe --uninstall    # 卸载（移除菜单 + 删除安装文件）
```

- **.NET 10 自包含单文件 exe**，目标电脑无需预装 .NET
- 需要 ffmpeg（不捆绑，GPL 分发义务）：
  1. 下载 https://www.gyan.dev/ffmpeg/builds/ （ffmpeg-release-essentials.zip）
  2. bin 目录加入 PATH，或写 %LOCALAPPDATA%\Pu\config.json 的 `{"ffmpeg":"..."}`
  3. 缺失时程序会提示引导

### 单个视频

右键视频 → `pu~`：

1. 已有实例在跑？→ 命名管道把文件递过去，复用同一个服务
2. ffprobe 探测 → 决策矩阵选**最快看到视频**的方案：能直出就直出（0 等待）、能 copy 就 copy（秒级——音轨是 AAC/AC-3/E-AC-3 也直接 copy，产物写分段 MP4，无二次重写）、只有 HEVC 10bit / Hi10P / AV1 / VP9 才全转码；想让任何视频都强制重编码，写 `%LOCALAPPDATA%\Pu\config.json` 的 `{"transcode":"always"}`
3. 状态页在转码开始的瞬间就给出 —— **扫码 → 看到进度 → 转完自动起播**
4. 内嵌字幕（SRT/ASS）并行抽成 WebVTT；PGS/VobSub 图形字幕自动跳过
5. Kestrel 普通权限监听 `0.0.0.0`，端口被占自动上探，URL 带随机 token
6. 托盘图标（停止 / 打开状态页），空闲 30 分钟自动退出

### 文件夹（整季剧集）

右键文件夹 → `pu~` → 列表页（递归扫描、非媒体自动剔除、按名排序）：

- 点开文件才转码（懒加载，不预转）；状态徽标实时更新（未打开/转码中/就绪/失败）
- 重复点开复用同一任务，不重复转码

### 硬件加速

全转码路径按 **NVENC → AMF → QSV → libx264** 选编码器——这个顺序就是「独显优先」：N 卡必为独显，AMF 多为 A 卡独显，QSV 基本是 Intel 核显。注意 `ffmpeg -encoders` 列的是编译进 build 的编码器（没硬件也照列），所以**硬件候选逐个实测**（lavfi 试编 8 帧），第一个真能用的胜出；硬件编码器自动配硬件解码（`-hwaccel`），失败自动软解回退一次；低于 256×144 的小视频直接软编（硬编有最小尺寸限制）。

### 缓存

- `%LOCALAPPDATA%\Pu\cache\{sha1(路径|大小|mtime|策略变体)}\`，同一文件第二次运行零耗时；策略/编码器不同 → 不同缓存目录，不会命中旧产物
- 默认上限 **20 GB**，LRU 淘汰（命中刷新标记；正在被读取的条目跳过）
- `pu --clean` 手动清空

## 命令

```powershell
pu --install            安装到 %LOCALAPPDATA%\Pu\ 并注册右键菜单
pu --uninstall          卸载（移除右键菜单 + 删除安装文件）
pu --register           注册右键菜单（扩展名清单可改 %LOCALAPPDATA%\Pu\extensions.json）
pu --unregister         移除右键菜单
pu --clean              清空转码缓存
pu <视频文件|文件夹>      处理并弹出状态页/列表页（已有实例则交给它）
pu --help / --version
```

## 打包

```powershell
powershell -File tools/publish.ps1   # WPF 自包含单文件发布 + 产出 zip
```

## 测试

```powershell
dotnet test
```

决策矩阵（含硬编/小视频回退）、faststart、缓存 LRU、文件夹扫描、IPC、Range 服务为单元测试；probe / 转码 / 字幕 / 文件夹全链路为集成测试（依赖 PATH 中的 ffmpeg，缺失时静默跳过）。

## 目录结构

```
src/Pu.Core/     引擎（无 Windows 依赖）：Probe / Planning / Pipeline / Serving / Ipc / Cache
src/Pu.App/      入口：WPF 界面、CLI 分发、Shell 注册、托盘、单实例
web/             状态/播放页 + 文件夹列表页（嵌入程序集，离线可用）
tests/           单元 + 集成测试
assets/          图标源（编辑 SVG 后跑 tools/build-icon.ps1 重新生成 pu.ico）
tmp/             临时产物（已 gitignore）
```

## 备注

- 首次对外提供媒体时 Windows 防火墙会弹窗，允许即可
- Win11 会把新托盘图标放进「显示隐藏的图标」溢出区，属系统默认行为
- 临时 / 实验产物一律放 `tmp/`，不提交
