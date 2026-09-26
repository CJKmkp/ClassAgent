# ClassAgent

ICC-CE 的课堂 AI 助手插件。

当前版本提供独立的配置、日志、模型客户端和悬浮球；模型支持 OpenAI-compatible 与 Anthropic 协议。屏幕截图、UI Automation 元素快照、结构化工具调用、画布墨迹和白板能力由宿主 Plugin SDK 提供。

## 权限与隐私

- `Network`：向用户配置的模型端点发送问题和可选截图。
- `ScreenCapture` / `UIAutomation`：仅在用户主动触发屏幕上下文或自动批注时读取。
- `Microphone`：默认关闭；只有用户打开 Windows 语音唤醒后才启动识别。
- API key 使用插件独立的随机 salt/IV + AES/HMAC 配置封装保护，插件日志不写入密钥、完整截图或模型原始密钥头。

## 课堂流程

- **讲题/批注**：在 iUWM Agent 页面选择“屏幕与批注”，按需读取题目截图和 UI Automation 元素；视觉模型可生成讲解或结构化批注计划。
- **辅助板书**：选择“智慧白板 → 截取题目并迁移到白板”，插件会隐藏自身窗口、截取当前题目、进入/新建白板页，并将截图作为可缩放画布图片插入；后续可以再把讲解文字以真实墨迹写入。
- **工具与图标**：导航、聊天、屏幕、绘制、设置和画布工具按钮统一使用 iUWM `SegoeFluentIcons`，不会混用自绘 Path 图标。

截图和 UI Automation 对自绘控件、浏览器 canvas、视频内容可能不完整；此时使用视觉模型或手动选择区域。

## GitHub Actions

- `build.yml`：在 `main` push、Pull Request 和手动触发时构建 net10 SDK/Controls、ClassAgent，并上传 `.icpx` artifact；版本变化时可自动创建 Release。
- `release.yml`：推送 `v*` tag 或手动输入 tag 时创建正式 Release。
- `plugin-build-net10.yml`：检出 ICC-CE `net10` 宿主 SDK 源码后构建，打包时只分发 `manifest.json`、入口 DLL、deps.json 和 icon，避免把宿主 SDK DLL 重复放入插件包。
