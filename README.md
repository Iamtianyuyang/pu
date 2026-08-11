# pu~

> 右键视频 → `pu~` → 手机 / iPad 扫码就能看。对面不用装任何 App，浏览器打开即播。

完整技术方案见 [方案.md](方案.md)。

## 当前状态：M1（Core 引擎 + CLI）

`pu <视频文件>` 打通全链路：

1. ffprobe 探测（格式 / 编码 / 位深 / 音频）
2. 转码决策矩阵（**尽可能不转**）：H.264/HEVC 8bit 视频 copy，只有 HEVC 10bit / AV1 / VP9 才全转码（优先 NVENC → QSV → AMF → x264）
3. 按需转码：HEVC 打 `hvc1` tag（Safari 必需）、一律 `+faststart` 秒起播
4. Kestrel 起服务（普通权限监听 `0.0.0.0`，端口被占自动上探）
5. 输出带 token 的播放链接，支持 Range（可拖动进度条）

## 构建与运行

```powershell
dotnet build
dotnet run --project src/Pu.App -- <视频文件>
```

输出形如：

```
✓ 就绪：movie
  手机/平板（同 Wi-Fi）: http://192.168.1.5:8000/s/3f9a...c2d1
  本机测试            : http://localhost:8000/s/3f9a...c2d1
```

Ctrl+C 停止服务。同一文件第二次运行走缓存（`%LOCALAPPDATA%\Pu\cache`），零耗时。

## 测试

```powershell
dotnet test
```

- 决策矩阵、faststart 判定：纯单元测试
- probe / 转码 / HTTP Range：集成测试（依赖 PATH 中的 ffmpeg，缺失时静默跳过）

## 目录结构

```
src/Pu.Core/     引擎（无 Windows 依赖）：Probe / Planning / Pipeline / Serving / Cache
src/Pu.App/      入口：CLI 分发（--register / --unregister 等留待 M2）
tests/           单元 + 集成测试
assets/          图标源（编辑 SVG 后跑 tools/build-icon.ps1 重新生成 pu.ico）
tmp/             临时产物（已 gitignore）
```

## 备注

- 首次对外提供媒体时 Windows 防火墙会弹窗，允许即可
- 临时 / 实验产物一律放 `tmp/`，不提交
