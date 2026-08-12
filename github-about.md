# GitHub 仓库 About 完整配置指南

---

## 📌 1. 仓库描述 (Description)

### 中文描述（推荐）
```
右键视频 → 手机扫码 → 直接播放。不用装 App，不用拷文件，局域网内秒开。
```

### 英文描述（国际用户可见）
```
Right-click any video → Scan QR with your phone → Watch instantly. No app install, no file transfer, pure local network streaming.
```

> 💡 GitHub 会同时显示两者，建议都填。在仓库设置里，Description 填中文，然后点右侧「Edit」添加英文翻译。

---

## 🏷️ 2. 话题标签 (Topics)

按优先级排序，建议全部添加：

| 标签 | 说明 |
|:---:|:---|
| `video-streaming` | 核心功能：视频流 |
| `media-server` | 媒体服务器 |
| `qrcode` | 二维码扫码 |
| `local-network` | 局域网传输 |
| `lan` | 局域网缩写 |
| `home-theater` | 家庭影院场景 |
| `ffmpeg` | 底层转码引擎 |
| `video-player` | 视频播放器 |
| `transcoding` | 实时转码 |
| `windows` | 目标平台 |
| `nodejs` | 技术栈 |
| `electron` | 如使用 Electron |
| `streaming` | 流媒体 |
| `subtitle` | 字幕支持 |
| `hevc` | HEVC 格式支持 |
| `av1` | AV1 格式支持 |

**添加方式：** 仓库主页 → 右侧 About 区 → ⚙️ → Topics → 逐个输入回车

---

## 🌐 3. 网站链接 (Website)

```
https://github.com/Iamtianyuyang/pu/releases/latest
```

或如果有独立官网：
```
https://Iamtianyuyang.github.io/pu
```

---

## ✅ 4. 勾选选项

| 选项 | 建议 | 说明 |
|:---:|:---:|:---|
| **Releases** | ✅ 必须勾选 | 显示版本发布 |
| **Packages** | ❌ 不勾选 | 除非发布 npm 包 |
| **Deployments** | 可选 | 如有 GitHub Pages |

---

## 🖼️ 5. Social Preview 图片

GitHub 仓库设置 → Options → Social Preview

建议尺寸：**1280×640px**

设计建议：
- 左侧放蓝色噗噗 logo（放大，醒目）
- 右侧放软件名「噗~噗噗~~噗噗噗噗~~~~」
- 底部小字：「右键视频，手机扫码即看」
- 背景用渐变蓝 #1e5aa8 → #4a90d9
- 整体简洁，文字要大，因为缩略图很小

---

## 📝 6. Release Note 模板

每次发版时，Release Note 建议按这个结构写：

```markdown
## 🎉 噗噗 v1.x.x

### ✨ 新功能
- 新增 xxx 功能
- 支持 xxx 格式

### 🐛 修复
- 修复了 xxx 问题
- 优化了 xxx 体验

### 📦 下载
| 版本 | 文件名 | 大小 |
|:---:|:---|:---:|
| ⭐ 全自带版 | `pu-setup-full-v1.x.x.exe` | ~xx MB |
| 🪶 标准版 | `pu-setup-v1.x.x.exe` | ~xx MB |
| 🎒 便携版 | `pu-windows-x64-v1.x.x.zip` | ~xx MB |

### 💝 致谢
感谢噗噗大王的陪伴与白眼。
```

---

## 🏷️ 7. Issue 标签建议

在仓库 Settings → Labels 里创建以下标签：

| 标签名 | 颜色 | 用途 |
|:---:|:---:|:---|
| `bug` | 🔴 #d73a4a | 程序错误 |
| `enhancement` | 🟢 #a2eeef | 功能建议 |
| `question` | 🟣 #d876e3 | 使用问题 |
| `good first issue` | 🟡 #7057ff | 适合新手贡献 |
| `help wanted` | 🟠 #008672 | 需要帮助 |
| `documentation` | 🔵 #0075ca | 文档相关 |
| `windows` | #1e5aa8 | Windows 平台问题 |
| `transcoding` | #fbca04 | 转码相关问题 |
| `network` | #0e8a16 | 局域网连接问题 |

---

## 🎯 8. 仓库设置优化

### Settings → General
- ✅ **Issues** — 开启（接收用户反馈）
- ✅ **Discussions** — 建议开启（用户交流区）
- ✅ **Projects** — 可选（管理开发进度）
- ✅ **Sponsorships** — 可选（打赏入口）
- ✅ **Preserve this repository** — 可选（归档保护）

### Settings → Branches
- 设置 `main` 为保护分支
- 要求 PR 审查后再合并

---

## 👤 9. 个人 GitHub Profile README（进阶）

创建仓库：`你的用户名/你的用户名`（和用户名完全一致）

放入 `README.md`：

```markdown
<p align="center">
  <img src="https://raw.githubusercontent.com/Iamtianyuyang/pu/main/assets/pu-logo.png" width="80">
</p>

<h1 align="center">噗~</h1>

<p align="center">
  <samp>
    正在让「电脑里的剧能在手机上看」这件事变得超级简单。
  </samp>
</p>

<p align="center">
  <a href="https://github.com/Iamtianyuyang/pu">
    <img src="https://img.shields.io/badge/🎬%20噗噗-右键扫码即看-1e5aa8?style=flat-square">
  </a>
  <img src="https://img.shields.io/badge/Windows-10+-0078D6?style=flat-square&logo=windows&logoColor=white">
  <img src="https://img.shields.io/badge/FFmpeg-✓-007808?style=flat-square&logo=ffmpeg&logoColor=white">
</p>

---

### 🛠️ 技术栈

<p align="center">
  <img src="https://img.shields.io/badge/Node.js-339933?style=for-the-badge&logo=nodedotjs&logoColor=white">
  <img src="https://img.shields.io/badge/Electron-47848F?style=for-the-badge&logo=electron&logoColor=white">
  <img src="https://img.shields.io/badge/FFmpeg-007808?style=for-the-badge&logo=ffmpeg&logoColor=white">
  <img src="https://img.shields.io/badge/Express-000000?style=for-the-badge&logo=express&logoColor=white">
</p>

### 📊 项目数据

<p align="center">
  <img src="https://github-readme-stats.vercel.app/api/pin/?username=Iamtianyuyang&repo=pu&theme=github_light&title_color=1e5aa8&icon_color=1e5aa8&hide_border=true">
</p>

### 💝 献给

<p align="center">
  <samp>
    全世界最可爱的噗噗大王~ 🫧<br>
    那个白眼就是这个软件的启动音。
  </samp>
</p>
```

---

## 📋 快速检查清单

发布前逐项确认：

- [ ] Description 已填写（中英文）
- [ ] Topics 已添加（至少 10 个）
- [ ] Website 链接已设置
- [ ] Releases 选项已勾选
- [ ] Social Preview 图片已上传
- [ ] Issue 标签已创建
- [ ] Discussions 已开启
- [ ] README.md 已完善
- [ ] LICENSE 文件已添加
- [ ] 第一个 Release 已发布

---

> 💡 **小贴士**：GitHub 仓库的 About 区是用户第一眼看到的内容，
> 好的配置能让项目看起来更专业、更可信，也更容易被搜索到。
