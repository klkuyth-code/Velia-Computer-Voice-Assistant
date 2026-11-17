# 首次配置指南

## 选择需要的服务

各项服务独立配置，不要求一次填满。云端服务的可用地区、额度和费用由相应供应商决定。

| 服务 | Velia 中的用途 | 凭据入口 |
| --- | --- | --- |
| DeepSeek | 普通对话与共享任务规划 | [API Key 管理](https://platform.deepseek.com/api_keys) |
| Gemini | Live 实时语音与屏幕理解 | [Google AI Studio](https://aistudio.google.com/app/apikey) |
| 火山 ASR | 普通语音转写 | [豆包语音 API Key 管理](https://console.volcengine.com/speech/new/setting/apikeys?projectName=default) |
| Firecrawl | 可选的公开网页检索与正文抓取 | [API Key 管理](https://firecrawl.dev/app/api-keys) |

API Key 只粘贴到 Velia 的对应设置中。不要提交到仓库或发送到公开聊天、Issue。

## 火山 ASR

当前实现调用的语音识别接口为：

```text
POST https://openspeech.bytedance.com/api/v3/auc/bigmodel/recognize/flash
X-Api-Resource-Id: volc.bigasr.auc_turbo
```

这是语音识别服务，不是豆包聊天模型。需要在同一项目中开通该接口对应资源，并使用有权访问它的凭据。当前支持单一 API Key，以及 App ID + Access Token 两种凭据模式。

请使用 Velia 内的真实连接测试确认服务可用。仅显示“已配置”不能证明服务已开通、账户有额度或识别请求成功。模型或控制台名称以后可能变化，可用上面的接口和资源 ID 对照供应商文档排查。

## Chrome 扩展

1. 从源码构建后打开 `chrome://extensions`。
2. 开启开发者模式，选择“加载已解压的扩展程序”。
3. 选择仓库中的 `browser-extension/dist`。
4. 在 Velia 的“浏览器与网页”复制配对令牌。
5. 打开扩展弹窗，填写令牌并连接。

请使用你准备让 Velia 操作的同一 Chrome 配置。扩展令牌只用于本机连接，不应公开分享。

## 本地唤醒

源码用户执行以下命令准备已固定版本和校验值的运行时：

```powershell
pnpm --dir desktop run prepare:wake
```

本地检测只负责唤醒。被唤醒后的普通语音转写和模型回答仍使用相应云服务。

## 常见启动问题

- 提示缺少 `.tools/dotnet`：在仓库根目录执行 `scripts/setup.ps1`。
- 提示找不到 Core：按 README 先编译 Release；UI 烟测使用 Debug，由 `verify.ps1` 构建。
- Chrome 显示未连接：确认扩展已加载到正确的 Chrome 配置，且 Velia 正在运行。
- Word / Excel 操作不可用：确认安装了相应桌面软件、没有阻塞的首次启动或保存对话框。
- 云端接口拒绝：检查服务资源、项目、凭据、网络和额度，勿反复把密钥粘贴到 Issue。
