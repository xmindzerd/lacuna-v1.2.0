# Lacuna V1.2.0

Lacuna 是面向 AI 短剧制作的 Windows 桌面工具，支持从剧本分析、资产制作到分镜、视频提示词和视频生成。这里是 V1.2.0 Windows x64 团队版的可编辑源码。

[下载 Windows x64 团队版](https://github.com/xmindzerd/lacuna-v1.2.0/releases/download/v1.2.0/Lacuna-V1.2.0-Windows-x64.zip) · [查看 v1.2.0 Release](https://github.com/xmindzerd/lacuna-v1.2.0/releases/tag/v1.2.0)

## 源码目录

- 根目录下的 `.cs`、`.xaml` 和构建脚本是 Windows 主程序、界面和升级工具的源码。
- [`builtin-skills/`](builtin-skills/) 和 [`lacuna-feedback-diagnostics/`](lacuna-feedback-diagnostics/) 包含随程序内置的制作和反馈规则。
- [`ocr-engine/`](ocr-engine/) 包含扫描 PDF 的 OCR 引擎源码。第三方组件许可见 [`third-party/`](third-party/) 和 [`第三方组件许可.txt`](第三方组件许可.txt)。

## 构建 Windows 主程序

在 Windows x64 上，先下载上方的正式 ZIP。在仓库根目录用 Windows PowerShell 5.1 执行以下命令，替换为实际下载路径。

```powershell
.\scripts\Restore-BuildDependencies.ps1 -ReleaseZip 'C:\Downloads\Lacuna-V1.2.0-Windows-x64.zip'
.\build.ps1
```

恢复脚本先校验正式 ZIP 的 SHA‑256，再读取其中 `Lacuna.exe` 的 PDF 和 OCR 依赖资源。`build.ps1` 使用 Windows .NET Framework 4.x 编译器，输出 `build/Lacuna.exe`。

如需使用随包运行时，可把编译后的 `Lacuna.exe` 放进一份独立解压的 V1.2.0 团队包。要重新生成带双基线原地升级功能的正式发行包，`package-team.ps1` 还需要随包运行时以及原版 V1.1.55 和 V1.1.6 的完整解包目录。

## 自定义图片 API

在「设置 → 自定义图片 API」保存渠道名称、Base URL、API Key 和图片模型 ID，然后在项目的「资产出图渠道」或资产页选择该渠道。它不更改 Codex 登录、剧本分析或分镜设置，Key 在本机按 Windows 用户加密保存。编辑已有渠道的 Base URL 时须填写新地址的 Key。保存后，使用该渠道的已有项目在下一次出图时自动使用新地址。已有暂停批次若提示渠道设置已变更，可先核对已提交任务，再用「仅重试未完成项」继续。

- **OpenAI 兼容 Images API**：无参考图用 `/images/generations`；有参考图用 `/images/edits`。地址填供应商的 API 基础路径（通常以 `/v1` 结尾），不要加具体接口路径。供应商必须支持所选模型和对应接口。
- **Responses API 生图**：另填支持 `image_generation` 的主模型；思考强度默认不传。旧项目和原来的 Codex 内置生图无需重新配置。
