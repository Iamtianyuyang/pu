<div align="center">

<img src="assets/pu-logo.png" width="180" alt="噗~">

<h1>噗~噗噗~~噗噗噗噗~~~~</h1>

<p>
  <b>电脑上右键一个视频，手机扫个码就能看。</b><br>
  不用装 App · 不用拷文件 · 不用开会员<br>
  只要手机和电脑连着 <b>同一个 Wi-Fi</b> 🛜
</p>

<p>
  <img src="https://img.shields.io/github/v/release/Iamtianyuyang/pu?color=1e5aa8&label=版本&cacheSeconds=3600" alt="版本">
  <img src="https://img.shields.io/github/downloads/Iamtianyuyang/pu/total?color=1e5aa8&label=下载量&cacheSeconds=3600" alt="下载量">
  <img src="https://img.shields.io/badge/Windows-10+-0078D6?logo=windows&logoColor=white" alt="Windows">
  <img src="https://img.shields.io/github/license/Iamtianyuyang/pu?color=1e5aa8&label=许可证&cacheSeconds=3600" alt="许可证">
  <img src="https://img.shields.io/github/stars/Iamtianyuyang/pu?color=f1c40f&label=Stars&cacheSeconds=3600" alt="Stars">
</p>

<p>
  <a href="#-它是干嘛的">🍓 它是干嘛的</a> · 
  <a href="#-三分钟上手">🧁 三分钟上手</a> · 
  <a href="#-常见问题">🍬 常见问题</a> · 
  <a href="#-致谢">💝 致谢</a>
</p>

</div>

---

## 🍓 它是干嘛的

想象一个场景：

> 剧下载在电脑里，人想窝在被子里用 iPad 看。  
> 数据线？找不到。微信传？2GB 传到天亮。NAS？那是什么，能吃吗。

**噗噗就是来解决这个的** ——

```
右键视频 → 选择「噗~噗噗~~噗噗噗噗~~~~」→ 屏幕上出现二维码 → 手机扫一扫 → 开看
```

就这么简单。视频不离开你家，不用上传到任何地方 ✨

| | 功能 | 说明 |
|:---:|:---|:---|
| 🎬 | **右键单个视频** | 手机扫码即看，即点即播 |
| 📁 | **右键整个文件夹** | 整季剧集列成列表，点哪集看哪集 |
| ⚡ | **常见格式直接播** | 少见格式自动转码，转完自动开播 |
| 📝 | **字幕也能带上** | 内嵌字幕自动提取，不用手动找 |
| 🧠 | **看过的会记住** | 转码结果留在本地，下次打开秒开 |

---

## 🧁 三分钟上手

<a href="assets/demo.mp4">
  <img src="assets/demo-preview.png" width="720" alt="完整流程演示：右键 → 扫码 → 开看" />
</a>

<video src="https://github.com/Iamtianyuyang/pu/releases/latest/download/demo.mp4" controls width="720"></video>

> 🎬 完整流程演示：右键 → 扫码 → 开看。上面是内嵌播放器；如果浏览器不支持，点击预览图下载 [demo.mp4](assets/demo.mp4)。

### 1️⃣ 下载安装

下载 **[`pu-setup-full.exe`](https://github.com/Iamtianyuyang/pu/releases/latest)**（全自带版），双击，一路「下一步」即可。

> **全自带版**已经把 ffmpeg 打包在内，装完即用，无需额外配置。

| 版本 | 适合谁 | 说明 |
|:---:|:---|:---|
| ⭐ **`pu-setup-full.exe`** | **大多数用户（推荐）** | 自带 ffmpeg，装完即用 |
| 🪶 **`pu-setup.exe`** | 已自行安装 ffmpeg 的用户 | 体积更小，需自行配置 ffmpeg |
| 🎒 **`pu-windows-x64.zip`** / **`pu-windows-x64-full.zip`** | 不想安装的用户 | 解压即用，不写注册表 |

> 💡 便携版如需右键菜单，先运行一次 `pu.exe --install` 即可。  
> 需要 **Windows 10 及以上**。手机、平板不限，浏览器打开就能播。

### 2️⃣ 右键任意视频

在视频文件上点 **右键**，选 **「噗~噗噗~~噗噗噗噗~~~~」**。

第一次使用需要准备片刻，屏幕上就会出现二维码 🔲

### 3️⃣ 手机扫码

用手机自带相机或浏览器扫一下，**就能直接开看**。

> 💡 **右键文件夹也有效**：会列出里面所有视频，手机上当追剧列表用。

---

## 🍬 常见问题

<details>
<summary><b>手机扫了没反应？</b></summary>

确认手机和电脑连着 **同一个 Wi-Fi**。  
另外，第一次使用时 Windows 防火墙会弹窗，请点 **「允许」**，否则手机无法连接。
</details>

<details>
<summary><b>第一次用，它说要装 ffmpeg？</b></summary>

那你下载的是标准版。按提示安装一次 ffmpeg 即可，或者直接换 **全自带版**，省掉这一步。
</details>

<details>
<summary><b>能看但卡？</b></summary>

大文件第一次播放前需要先转码，进度条会显示进度，转完后自动开播。  
转码结果保存在本地，下次打开就是秒开 ⚡
</details>

<details>
<summary><b>支持哪些格式？</b></summary>

常见的视频格式都行。浏览器能直接放的（比如 MP4）秒开；HEVC、AV1 这类少见格式会自动转码，转完自动开播。内嵌字幕（SRT/ASS）也会一起带上。
</details>

<details>
<summary><b>会不会把我的视频传到网上？</b></summary>

**不会。** 视频只在手机和电脑之间传输，不会上传到任何服务器。你的剧，永远只属于你家的局域网 🏠
</details>

<details>
<summary><b>卸载干净吗？</b></summary>

干净。从「设置 → 应用」卸载后，右键菜单、缓存、快捷方式都会一并清除。
</details>

---

## 💝 致谢

<div align="center">

**这个小工具，是写给 我的女朋友——噗噗大王~ 的。**

</div>

<div align="center">

🍓 全世界最可爱的噗噗大王~  
✨ 无敌漂亮的噗噗大王~  
💎 闪闪发光的噗噗大王~  
🌸 人见人爱的噗噗大王~  
🍬 笑起来超甜的噗噗大王~  
⚡ 元气满满的噗噗大王~  
📖 聪明伶俐的噗噗大王~  
👑 宇宙第一美少女的噗噗大王~

</div>

<div align="center">

✨ *上面的词会在软件的每个角落循环播放，躲不掉的，认命吧。* ✨

</div>

感谢你让我想把看剧这件事变得简单一点，  
也感谢你看到这个名字时一定会翻的那个白眼。  
**那个白眼就是这个软件的启动音。噗~** 🫧

---

<div align="center">

<sub>技术细节、命令行参数、转码策略什么的，都在 <a href="README.dev.md">README.dev.md</a> 里，普通人不用看。</sub>

</div>
