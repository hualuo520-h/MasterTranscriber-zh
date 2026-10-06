# 🎙️ Master Transcriber 转写助手（中文汉化版）

一个 Windows 桌面应用，用于**录音转写**、**实时字幕**和 **AI 摘要**。基于 WPF + .NET 8 构建，使用本地 whisper.cpp 做语音识别，使用 DeepSeek 做智能摘要。

本仓库是 [LoveIiei/TranscribeMeeting](https://github.com/LoveIiei/TranscribeMeeting) 的**中文汉化 + 实时字幕增强版**。

![平台](https://img.shields.io/badge/platform-Windows-blue)
![.NET 8.0](https://img.shields.io/badge/.NET-8.0-purple)
![License](https://img.shields.io/badge/license-MIT-green)

---

## ✨ 功能特点

### 🎯 核心功能

| 功能 | 说明 |
|---|---|
| **💬 实时字幕** | 悬浮字幕条实时显示正在说的话，**滞后仅 0.5～3 秒**（采用滚动缓冲 + LocalAgreement 增量确认算法） |
| **⚫ 录音转写** | 录制麦克风和/或系统声音，结束后自动转写为文字 |
| **📂 导入转写** | 直接导入视频/音频文件（mp4、mkv、mp3、wav、m4a 等）批量转写 |
| **✨ AI 摘要** | 用 DeepSeek 自动生成会议纪要、知识点总结，自动纠正同音错字 |
| **📝 Markdown 预览** | 摘要以富文本 Markdown 渲染，可一键导出 |
| **🌐 翻译（可选）** | 接入 DeepL 将转写结果翻译为 8+ 种语言 |

### 🎨 中文优化

- **全界面汉化**：约 185 处界面文案已翻译为简体中文
- **中文识别优化**：自动注入普通话提示词，输出**简体中文**而非繁体
- **同音错字自动纠正**：DeepSeek 摘要阶段会自动修正「现成→线程」「计存器→寄存器」这类同音误识别

### 🔧 灵活的配置

- **本地或云端**：本地 whisper.cpp 转写 + 云端 DeepSeek 摘要（也可全本地 Ollama）
- **即开即用**：完整版压缩包已内置 whisper.cpp 和模型，**解压后双击即可运行，无需任何配置**
- **参数可调**：字幕字号、刷新步长、缓冲窗口、场景模板均可自定义

---

## 🚀 快速开始

### 方式一：下载完整版（推荐，开箱即用）

1. 前往 [**Releases 页面**](https://github.com/hualuo520-h/MasterTranscriber-zh/releases/latest)
2. 下载 `MasterTranscriber-win-x64-full.zip`（约 500 MB，已内置 whisper.cpp + CUDA 加速库 + 语音模型）
3. 解压到任意目录（**请完整解压，不要直接在压缩包里运行**）
4. 双击 `TranscribeMeetingUI.exe` 启动

> 程序会自动识别同目录下的 `whisper\` 文件夹，无需手动设置路径。

### 方式二：下载精简版

下载 `MasterTranscriber-win-x64.zip`（约 7 MB），然后自行准备 whisper.cpp：

1. 从 [whisper.cpp Releases](https://github.com/ggerganov/whisper.cpp/releases) 下载 Windows 版本
2. 下载模型文件（推荐 `ggml-small.bin`，约 465 MB）放到 `Models\` 目录
3. 在应用内 **⚙️ 设置 → 🎙️ 转写引擎** 中指定 `whisper-cli.exe` 和模型路径

### 环境要求

- **Windows 10 / 11 (64 位)**
- [**.NET 8.0 Desktop Runtime**](https://dotnet.microsoft.com/download/dotnet/8.0)（完整版压缩包内已附带，无需单独安装）
- **NVIDIA 显卡（可选）**：有 NVIDIA 显卡时自动启用 CUDA 加速，速度约提升 **7 倍**
  （30 秒音频：GPU 1.98 秒 vs CPU 13.93 秒）

---

## 📖 使用说明

### 实时字幕

1. 在主界面打开 **💬 字幕** 开关
2. 屏幕底部会出现一条半透明悬浮字幕条
3. 对着麦克风说话，文字会以约 **0.5 秒一拍**的节奏增量出现
4. 字幕条上的 `A−` / `A+` 可调整字号，`✕` 可关闭

字幕条会显示两行信息：
- **正文**：已确认的文字 + 正在识别的待定内容
- **状态**：`已确认 N 字（滞后 X.X 秒）`，滞后即「这句话是几秒前说的」

> 💡 首次开启字幕需要几秒钟启动 whisper-server，请稍等片刻。

### 录音转写

1. 选择场景（会议 / 讲座 / 访谈 / 播客 / 其他）
2. 按需打开 **🎙️ 麦克风** 和 **🔊 系统声音** 开关
3. 点击 **⚫ 开始录制**，结束后自动转写并生成摘要

### 导入文件转写

点击 **📂 导入** 选择视频或音频文件，程序会自动提取音轨、转写、生成摘要。

### AI 摘要配置

1. 打开 **⚙️ 设置 → ✨ AI 摘要服务商**
2. 选择 **DeepSeek**
3. 填入 API Key（在 [platform.deepseek.com](https://platform.deepseek.com/) 申请）
4. 模型填 `deepseek-chat`

---

## 🛠️ 从源码构建

```bash
git clone https://github.com/hualuo520-h/MasterTranscriber-zh.git
cd MasterTranscriber-zh

dotnet restore
dotnet build TranscribeMeetingUI/TranscribeMeetingUI.csproj -c Release
dotnet run --project TranscribeMeetingUI
```

发布独立版本：

```bash
dotnet publish TranscribeMeetingUI/TranscribeMeetingUI.csproj -c Release -r win-x64 --self-contained false -o out
```

### 技术栈

- **UI**: WPF (.NET 8, `net8.0-windows`)
- **音频采集**: [NAudio](https://github.com/naudio/NAudio) 2.2.1
- **语音识别**: [whisper.cpp](https://github.com/ggerganov/whisper.cpp)（本地 HTTP 服务，端口 8917）
- **Markdown 渲染**: [Markdig.Wpf](https://github.com/xoofx/markdig) 0.5.0.1
- **云端语音**: [Microsoft.CognitiveServices.Speech](https://learn.microsoft.com/azure/cognitive-services/speech-service/) 1.46.0（可选）

---

## 🔬 实时字幕算法说明

本版本对实时字幕做了深度优化，把滞后从最初的 **约 30 秒降到 0.5～3 秒**。

核心思路是**滚动缓冲 + LocalAgreement 增量确认**：

1. 麦克风 PCM 持续写入滚动缓冲区
2. 每 0.5 秒把整个缓冲区送进常驻的 `whisper-server` 推理一次
3. 对比相邻两次推理结果，**取两者的公共前缀**作为「已确认」内容
4. 已确认部分从缓冲区裁掉，只保留未确认的尾巴

关键优化点：

- **字符级宽松前缀匹配**：忽略标点、空白、符号的差异，避免 whisper 的标点抖动导致永远匹配不上（这是最初 30 秒滞后的根因）
- **提示词回显过滤**：whisper 在静音段会把提示词原样吐回，已加入过滤
- **提交与裁剪解耦**：裁剪不再依赖时间戳，只用 whisper 给出的分段边界
- **0.5 秒步长 / 0.3 秒尾部保护**：经大量实测，这是质量与延迟的最佳平衡点（0.3 秒步长会出现幻觉，已放弃）

实测数据（103 秒中文测试音频）：**139 个提交点，滞后全程 0.0 秒，识别准确率 96.8%**。

---

## ❓ 常见问题

**Q: 字幕出不来 / 一直显示「识别中」？**
A: 首次启动 whisper-server 需要几秒。若长时间无反应，检查 `whisper\whisper-server.exe` 是否存在，或 8917 端口是否被占用。

**Q: 转写结果是繁体字？**
A: 确保 **⚙️ 设置 → 🎙️ 转写引擎 → 语言** 设为 `zh`，程序会自动注入简体中文提示词。

**Q: 转写速度很慢？**
A: 检查是否启用了 CUDA。需要 NVIDIA 显卡 + 最新驱动。若没有 N 卡，可在设置中改用 `ggml-base.bin` 等更小的模型。

**Q: 摘要功能报错？**
A: 检查 DeepSeek API Key 是否有效、账户是否有余额。

**Q: 字幕里偶尔出现同音错字？**
A: 这是语音识别的固有现象（如「线程」→「现成」）。**AI 摘要会自动纠正这些错字**，不影响最终整理结果。

**Q: 杀毒软件报警？**
A: 本程序未做代码签名，属于常见的误报。可自行从源码构建验证。

---

## 📄 许可证

本项目基于上游 [LoveIiei/TranscribeMeeting](https://github.com/LoveIiei/TranscribeMeeting) 修改，遵循 **MIT License**。

## 🙏 致谢

- [LoveIiei/TranscribeMeeting](https://github.com/LoveIiei/TranscribeMeeting) — 原始项目
- [whisper.cpp](https://github.com/ggerganov/whisper.cpp) — 本地语音识别引擎
- [NAudio](https://github.com/naudio/NAudio) — 音频录制与处理
- [Markdig](https://github.com/xoofx/markdig) — Markdown 渲染
- [DeepSeek](https://www.deepseek.com/) — AI 摘要服务
- [Ollama](https://ollama.ai/) — 本地 AI 模型（可选）
- [DeepL](https://www.deepl.com/) — 翻译服务（可选）
- [Azure Cognitive Services](https://azure.microsoft.com/en-us/services/cognitive-services/) — 云端语音识别（可选）

---

⭐ 如果这个项目对你有帮助，欢迎点个 Star！
