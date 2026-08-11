# pu~

> 右键视频 → `pu~` → 手机 / iPad 扫码就能看。对面不用装任何 App，浏览器打开即播。

完整技术方案见 [方案.md](方案.md)。

## 当前状态：M5（原生窗口）

右键视频/文件夹 → **原生桌面窗口**（Win32 + GDI+ 自绘，不弹控制台、不开浏览器）：

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

- **单文件 NativeAOT exe（≈16 MB）**，无 .NET 依赖，冷启动 <50 ms
- 需要 ffmpeg（不捆绑，GPL 分发义务）：
  1. 下载 https://www.gyan.dev/ffmpeg/builds/ （ffmpeg-release-essentials.zip）
  2. bin 目录加入 PATH，或写 %LOCALAPPDATA%\Pu\config.json 的 `{"ffmpeg":"..."}`
  3. 缺失时程序会提示引导

### 单个视频

右键视频 → `pu~`：

1. 已有实例在跑？→ 命名管道把文件递过去，复用同一个服务
2. ffprobe 探测 → 转码决策矩阵（**尽可能不转**）：H.264/HEVC 8bit 视频 copy，只有 HEVC 10bit / AV1 / VP9 才全转码
3. 状态页在转码开始的瞬间就给出 —— **扫码 → 看到进度 → 转完自动起播**
4. 内嵌字幕（SRT/ASS）并行抽成 WebVTT；PGS/VobSub 图形字幕自动跳过
5. Kestrel 普通权限监听 `0.0.0.0`，端口被占自动上探，URL 带随机 token
6. 托盘图标（停止 / 打开状态页），空闲 30 分钟自动退出

### 文件夹（整季剧集）

右键文件夹 → `pu~` → 列表页（递归扫描、非媒体自动剔除、按名排序）：

- 点开文件才转码（懒加载，不预转）；状态徽标实时更新（未打开/转码中/就绪/失败）
- 重复点开复用同一任务，不重复转码

### 硬件加速

全转码路径按探测结果优先 **NVENC → QSV → AMF → libx264**，硬件编码器自动配硬件解码（`-hwaccel`），失败自动软解回退一次；低于 256×144 的小视频直接软编（硬编有最小尺寸限制）。

### 缓存

- `%LOCALAPPDATA%\Pu\cache\{sha1(路径|大小|mtime)}\`，同一文件第二次运行零耗时
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
pu --no-browser         不自动打开浏览器
pu --help / --version
```

## 打包

```powershell
powershell -File tools/publish.ps1   # NativeAOT 发布 + 产出 zip
```

## 测试

```powershell
dotnet test
```

决策矩阵（含硬编/小视频回退）、faststart、缓存 LRU、文件夹扫描、IPC、Range 服务为单元测试；probe / 转码 / 字幕 / 文件夹全链路为集成测试（依赖 PATH 中的 ffmpeg，缺失时静默跳过）。

## 目录结构

```
src/Pu.Core/     引擎（无 Windows 依赖）：Probe / Planning / Pipeline / Serving / Ipc / Cache
src/Pu.App/      入口：CLI 分发、Shell 注册、托盘（P/Invoke）、单实例
web/             状态/播放页 + 文件夹列表页（嵌入程序集，离线可用）
tests/           单元 + 集成测试
assets/          图标源（编辑 SVG 后跑 tools/build-icon.ps1 重新生成 pu.ico）
tmp/             临时产物（已 gitignore）
```

## 备注

- 首次对外提供媒体时 Windows 防火墙会弹窗，允许即可
- Win11 会把新托盘图标放进「显示隐藏的图标」溢出区，属系统默认行为
- 临时 / 实验产物一律放 `tmp/`，不提交
