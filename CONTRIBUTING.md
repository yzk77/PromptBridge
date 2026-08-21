# 参与贡献

感谢你改进 Prompt Bridge。提交前请确保改动符合本项目的安全边界：任何写入输入框的内容都必须经过重新读取和验证，验证失败时不得发送。

## 开发环境

- Windows 10/11 x64
- .NET 8 SDK
- Visual Studio 2022、JetBrains Rider 或 VS Code（可选）

```powershell
dotnet restore ChineseToChatGPT.sln
dotnet build ChineseToChatGPT.sln -c Release --no-restore
dotnet run --project tests\ChineseToChatGPT.Tests\ChineseToChatGPT.Tests.csproj -c Release --no-build
```

## 提交要求

1. 不要提交 API Key、Secret、日志、用户设置、剪贴板内容或真实提示词。
2. 不要削弱写入后的验证、焦点检查、回滚或并发抑制。
3. 新增翻译服务商时实现现有抽象接口，并为签名、错误分类、超时和自动切换补充测试。
4. 修改 UI 时同时维护英文和简体中文资源，确保资源键完全一致。
5. 修改支持客户端适配逻辑时，至少手动验证即时发送、预览、长文本、附件、焦点丢失和回滚。
6. 保持 `TreatWarningsAsErrors` 下构建无警告。

## Pull Request 建议

- 标题简洁描述结果，例如 `fix: restore composer after failed verification`。
- 说明问题、实现方式、风险和验证结果。
- UI 变更请附深色界面截图。
- 不要把 `bin`、`obj`、`publish` 或发布 ZIP 提交到源码分支；发布文件应通过 GitHub Releases 提供。
