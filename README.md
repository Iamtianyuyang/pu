# pu~

> 右键视频 → `pu~` → 手机 / iPad 扫码就能看。对面不用装任何 App，浏览器打开即播。

完整技术方案见 [方案.md](方案.md)。

## 当前状态：M2（右键 + 托盘 + 二维码状态页）

```powershell
pu --register          # 注册右键菜单（HKCU，无需管理员）
```

然后**右键任意视频 → pu~**：

1. 已有实例在跑？→ 命名管道把文件递过去，复用同一个服务
2. ffprobe 探测 → 转码决策矩阵（**尽可能不转**）：H.264/HEVC 8bit 视频 copy，只有 HEVC 10bit / AV1 / VP9 才全转码（优先 NVENC → QSV → AMF → x264）
3. 状态页在转码开始的瞬间就给出 —— **扫码 → 看到进度 → 转完自动起播**
4. 内嵌字幕（SRT/ASS）并行抽成 WebVTT，页面上按钮切换；PGS/VobSub 图形字幕自动跳过
5. Kestrel 普通权限监听 `0.0.0.0`，端口被占自动上探，URL 带随机 token
6. 托盘图标（停止 / 打开状态页），空闲 30 分钟自动退出

## 命令

```powershell
pu --register            注册右键菜单（34 个媒体扩展名，可改 %LOCALAPPDATA%\Pu\extensions.json）
pu --unregister          移除右键菜单
pu <视频文件>             处理并弹出状态页（已有实例则交给它）
pu --no-browser          不自动打开浏览器
pu --help / --version
```

缓存：`%LOCALAPPDATA%\Pu\cache`，同一文件第二次运行零耗时。

## 测试

```powershell
dotnet test
```

- 决策矩阵、faststart 判定、IPC、Range 服务：单元测试
- probe / 转码 / 字幕抽取：集成测试（依赖 PATH 中的 ffmpeg，缺失时静默跳过）

## 目录结构

```
src/Pu.Core/     引擎（无 Windows 依赖）：Probe / Planning / Pipeline / Serving / Ipc / Cache
src/Pu.App/      入口：CLI 分发、Shell 注册、托盘（P/Invoke）、单实例
web/index.html   状态/播放页（嵌入程序集，离线可用）
tests/           单元 + 集成测试
assets/          图标源（编辑 SVG 后跑 tools/build-icon.ps1 重新生成 pu.ico）
tmp/             临时产物（已 gitignore）
```

## 备注

- 首次对外提供媒体时 Windows 防火墙会弹窗，允许即可
- Win11 会把新托盘图标放进「显示隐藏的图标」溢出区，属系统默认行为
- 临时 / 实验产物一律放 `tmp/`，不提交
