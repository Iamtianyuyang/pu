# pu~（开发者文档）

> 右键视频 → `噗~噗噗~~噗噗噗噗~~~~` → 手机 / iPad 扫码就能看。对面不用装任何 App，浏览器打开即播。
> 给人看的介绍在 [README.md](README.md)，这里记技术细节。

## 当前状态：.NET 10 + WPF 桌面版

右键视频/文件夹 → **WPF 桌面窗口**（不弹控制台、不强制打开浏览器）：

- 窗口宽 440、高度随内容；默认停在鼠标所在屏幕的右下角，向上长高（用户拖动过就不再自动挪）
- 二维码 + 链接 + 「复制链接 / 在这台电脑上打开」；转码进度（排线进度条 + 剩余时间估算 + 任务栏按钮进度）
- **送达**：手机打开链接后（`SessionServer.ClientArrived`）窗口切到「已送到 iPad」，二维码收起，可再展开给第二台设备扫；转码失败时分享区整块收起
- 文件夹模式：整个文件夹一个二维码 + 列表（也可在电脑上点开单集）；窗口可上下拉伸，高度记在 `%LOCALAPPDATA%\Pu\ui.json`；手机上点开的集这边也会刷新状态
- 深浅色跟随 Windows「应用模式」（`Ui/Theme/Light.xaml`、`Dark.xaml`，token 与网页 `pu.css` 一一对应；`PU_THEME=dark|light` 可强制指定，截图用）
- 噗噗吉祥物 `Ui/Mascot.xaml` 由 `tools/mascot/build.py` 生成（与网页 `mascot.svg` 同源），动画在 `Mascot.xaml.cs`
- 托盘常驻：显示窗口 / 关于 / 停止
- 网页版（/s/ 与 /f/）供手机 / 平板扫码后播放

```powershell
# 开发运行
pu --register          # 注册右键菜单（34 个媒体扩展名 + 文件夹，HKCU）
```

### 发给别人

**全自带版 `publish/pu-setup-full.exe`**（推荐，大多数用户）：双击 → 下一步 → 完成。附带 ffmpeg/ffprobe（gyan.dev release-essentials，发布时下载一次缓存进 `tools/vendor\`，不进 git），装到 `{app}\ffmpeg\`，用户零依赖。自动装到 `%LOCALAPPDATA%\Programs\pu~`、注册右键菜单、创建开始菜单快捷方式和「应用和功能」卸载条目；卸载时反注册菜单并清理 `%LOCALAPPDATA%\Pu`。

**标准版 `publish/pu-setup.exe`**：同上但不带 ffmpeg，体积更小——给已自行安装 ffmpeg 的用户。

**便携版 `publish/pu-windows-x64.zip` / `pu-windows-x64-full.zip`**：解压即用，不写注册表；需要右键菜单时手动执行：

```
pu.exe --install      # 安装到 %LOCALAPPDATA%\Pu\ 并注册右键菜单（无需管理员）
pu.exe --uninstall    # 卸载（移除菜单 + 删除安装文件）
```

> 所有版本共用 `%LOCALAPPDATA%\Pu` 的配置与缓存；不要同时装两份（卸载任一份会清掉共享数据）。

- **.NET 10 自包含单文件 exe**，目标电脑无需预装 .NET
- ffmpeg 查找顺序：config.json → **exe 旁的 `ffmpeg\` 子目录（全自带版）** → PATH：
  1. 下载 https://www.gyan.dev/ffmpeg/builds/ （ffmpeg-release-essentials.zip）
  2. bin 目录加入 PATH，或写 %LOCALAPPDATA%\Pu\config.json 的 `{"ffmpeg":"..."}`
  3. 缺失时程序会提示引导

### 单个视频

右键视频 → `pu~`：

1. 已有实例在跑？→ 命名管道把文件递过去，复用同一个服务
2. ffprobe 探测 → 决策矩阵选**最快看到视频**的方案：能直出就直出（0 等待——含 H.264+faststart 的 MP4、纯音频 mp3/flac/wav，浏览器原生可解；直出按「扩展名 + 编码/容器」双重门控，错命名文件不会以错误 MIME 直出）、能 copy 就 copy（秒级——音轨是 AAC/AC-3/E-AC-3 也直接 copy，产物写 HLS 分片，无二次重写）、只有 HEVC 10bit / Hi10P / AV1 / VP9 才全转码；想让任何视频都强制重编码，写 `%LOCALAPPDATA%\Pu\config.json` 的 `{"transcode":"always"}`
3. 状态页在转码开始的瞬间就给出 —— **扫码 → 看到进度 → 转完自动起播**
4. 内嵌字幕（SRT/ASS）并行抽成 WebVTT；PGS/VobSub 图形字幕自动跳过。
   直出/复用命中时**视频立即可播**，字幕后台抽取、就绪后补发（页面自动更新字幕按钮）；
   抽字幕失败只丢字幕，不拖垮视频
5. Kestrel 普通权限监听 `0.0.0.0`，端口被占自动上探，URL 带随机 token
6. 托盘图标（停止 / 打开状态页），空闲 30 分钟自动退出

### 文件夹（整季剧集）

右键文件夹 → `pu~` → 列表页（递归扫描、非媒体自动剔除、按名排序）：

- 点开文件才转码（懒加载，不预转）；状态徽标实时更新（未打开/转码中/就绪/失败）
- 重复点开复用同一任务，不重复转码
- 同一文件夹重复右键 → **复用同一会话**（URL 不变）并刷新列表：新加的文件立即可见，`_folders` 不随重复提交膨胀（路径键去尾部分隔符，`C:\a` 与 `C:\a\` 算同一文件夹）
- 文件夹会话登记上限 **128**：超限淘汰最老的会话（旧 token 的页面 404，列表快照随会话释放）；重提同路径自动重建并刷新列表

### 网页（手机 / 平板扫码后看到的）

视觉是「作业本上的圆珠笔涂鸦」：圆珠笔蓝 + 淡横线纸底，吉祥物噗噗按状态换表情；亮 / 暗色跟随系统。

- **转码中**：主角是噗噗 + 大号百分比 + 圆珠笔排线进度条；剩余时间由前端按进度速率估算（前 5 秒显示「正在估算」）。二维码收进「分享」面板（页面是 http，`navigator.share` / `navigator.clipboard` 都不可用，复制走 `execCommand` 兜底）；宽屏（≥900px）右侧常驻二维码
- **播放**：原生控件（iOS 全屏 / AirPlay / 画中画）；字幕按钮含「关」，选择按名字记住，换下一集沿用
- **断点续播**：按文件名把位置存在这台设备的 localStorage（token 每次启动会变，文件名不会）；看过 15 秒、没到最后 5% 才续播
- **上一个 / 下一个 + 自动连播**（仅从文件夹点开时）：播到一半后台 `POST /f/{token}/open/{next}` 预转下一个；播完倒数 5 秒，在**同一个 `<video>` 上换源** + `history.replaceState`——跳转新页面的话 iOS 不允许自动带声播放
- **链接失效**：失效 token 的页面也返回页面本身（状态码 404），显示「回电脑上重新右键」而不是浏览器空白错误页
- **扫码送达**：状态轮询带 `?d=` 设备提示（iPadOS Safari 伪装成 Mac，只有页面能认出 iPad），服务端记录非本机来访设备并发 `SessionServer.ClientArrived` 事件，供电脑窗口显示「送到了 iPad」
- 公共资源走 `/assets/{name}` 白名单：`pu.css` / `pu.js` / `mascot.svg` / `words.json`（ETag 协商缓存）、`pu-logo.png` / `hls.min.js`（长缓存）。无构建步骤，改完重新 `dotnet build` 生效
- 吉祥物：`tools/mascot/trace.py` 描摹 `assets/pu~.png` 拆层 → `tools/mascot/build.py` 补画表情，同时生成 `web/mascot.svg` 与 WPF 的 `src/Pu.App/Ui/Mascot.xaml`；夸夸词与台词只维护 `web/words.json` 一份（WPF 也读它）

### 硬件加速

全转码路径按 **NVENC → AMF → QSV → libx264** 选编码器——这个顺序就是「独显优先」：N 卡必为独显，AMF 多为 A 卡独显，QSV 基本是 Intel 核显。注意 `ffmpeg -encoders` 列的是编译进 build 的编码器（没硬件也照列），所以**硬件候选逐个实测**（lavfi 试编 8 帧），第一个真能用的胜出；硬件编码器自动配硬件解码（`-hwaccel`），失败自动软解回退一次；低于 256×144 的小视频直接软编（硬编有最小尺寸限制）。

### 产物与缓存

- **就地生产**：Remux / 转码产物写在源文件旁的 `.pu\` 子目录（hidden 属性），配同名 `.json` 清单（源大小|mtime|策略）——源文件不变 → 第二次运行零耗时；源目录不可写（只读 NAS 等）自动回退中央缓存
- 生产先写 `.tmp` 再改名，失败不留半截文件
- 中央缓存 `%LOCALAPPDATA%\Pu\cache\`：默认上限 **20 GB**，LRU 淘汰（命中刷新标记；正在被读取的条目跳过）
- `pu --clean` 一键清空：中央缓存 + 所有登记过的就地产物（空的 `.pu\` 目录一并删）

## 命令

```powershell
pu --install            安装到 %LOCALAPPDATA%\Pu\ 并注册右键菜单
pu --uninstall          卸载（移除右键菜单 + 删除安装文件）
pu --register           注册右键菜单（扩展名清单可改 %LOCALAPPDATA%\Pu\extensions.json）
pu --unregister         移除右键菜单
pu --clean              清空转码缓存
pu <视频文件|文件夹>      处理并弹出状态页/列表页（已有实例则交给它）
pu <视频文件|文件夹> --debug   服务模式时输出日志到控制台（须放在路径之后）
pu --help / --version
```

## 打包

```powershell
powershell -File tools/publish.ps1   # 发布单文件 exe + 便携 zip + 全自带 zip；装了 Inno Setup 时同时产出 pu-setup.exe / pu-setup-full.exe
```

> 发布前记得同步版本号：`src/Pu.App/Pu.App.csproj` 的 `<Version>` 与 `tools/setup.iss` 的 `AppVersion` / `VersionInfoVersion` 两处（当前 csproj 已是 0.0.521，setup.iss 还停在 0.0.520）。

## 测试

```powershell
dotnet test
```

决策矩阵（含硬编/小视频回退）、faststart、缓存 LRU、文件夹扫描、IPC、Range 服务为单元测试；probe / 转码 / 字幕 / 文件夹全链路为集成测试（依赖 PATH 中的 ffmpeg，缺失时静默跳过）。
**全格式转换矩阵**（FormatMatrixTests）：右键菜单注册的全部 34 个扩展名 + 关键编码变体（约 41 种）生成真实样本，走完整链路 生成→探测→决策→转码→产物校验——每行断言决策分支、RequiresEncoder 一致性与产物可探测性，防决策矩阵与真实 ffmpeg 行为脱节。

## 目录结构

```
src/Pu.Core/     引擎（无 Windows 依赖）：Probe / Planning / Pipeline / Serving / Ipc / Cache
src/Pu.App/      入口：WPF 界面、CLI 分发、Shell 注册、托盘、单实例
web/             播放页 + 文件夹列表页 + 公共样式/脚本/吉祥物（嵌入程序集，离线可用）
tools/mascot/    吉祥物描摹与表情生成脚本（uv run）
tests/           单元 + 集成测试
assets/          图标源（编辑 SVG 后跑 tools/build-icon.ps1 重新生成 pu.ico）
tmp/             临时产物（已 gitignore）
```

## 备注

- 首次对外提供媒体时 Windows 防火墙会弹窗，允许即可。**防火墙规则按 exe 路径记**：换安装位置（便携版 → 安装版、重装到新目录）后旧规则不算数；弹窗被挡住没点、或点了「取消」（Windows 会自动生成拦截规则），手机就连不上。为此窗口会检查（`Shell/FirewallCheck.cs` 读 HNetCfg.FwPolicy2，普通权限；判定逻辑 `Pu.Core/Common/FirewallRules.cs`，有单测）：当前网络下没放行就显示「手机可能连不上」卡片，「放行」按钮弹 UAC、用户确认后提权跑 PowerShell 删本程序的入站拦截规则并加放行规则（程序本身仍不需要管理员）。已送达（有手机连进来）时不提示。卸载不会删这条规则（卸载不提权）
- Win11 会把新托盘图标放进「显示隐藏的图标」溢出区，属系统默认行为
- 临时 / 实验产物一律放 `tmp/`，不提交
- **空闲退出按「真实活动」计时**：转码中/字幕后补中的页面轮询算活跃，已定案的页面轮询与文件夹列表轮询不再续命（否则手机端开着的页面会让服务永不退出）；正在播放（分片请求）自动续命；**续命只发生在 token 校验通过之后**——失效页面 / 无效 URL 的请求不算活动
- **播放页心跳**：已定案的播放页保持 30s 低速轮询，服务端把它记为「最近传输时刻」——页面开着时产物不被 LRU/Job 上限淘汰（暂停播放后恢复不 404）；心跳不续空闲退出，不影响自动退出
- **会话登记上限**：job 1024（淘汰最老的「已定案、字幕已定、窗口内无传输」）、文件夹会话 128（淘汰最老）——被淘汰会话的旧页面返回 404（可接受）；重提交走产物复用 / 会话重建，不重转码
- **ffmpeg 全链路超时**：ffprobe 探测 30s、编码器实测 15s、字幕抽取单次 5min / 总 15min；转码 120s 无进展输出视为卡死，自动终止并走硬解/硬编兜底
