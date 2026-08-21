# Prompt Bridge

<p align="center">
  <a href="README.md">简体中文</a> · <strong>English</strong>
</p>

<p align="center">
  <strong>Think in Chinese. Send polished English prompts from your AI desktop client.</strong>
</p>

Prompt Bridge is a Windows system tray utility. Focus the message composer in a supported AI desktop client, type in Chinese, and press `Ctrl+Shift+Enter`. Prompt Bridge translates the translatable content into English and appends:

> Please reply in Chinese.

Depending on your settings, the transformed message is either sent immediately or left in the composer for review.

![Translation provider settings](providers-refactor-preview.png)

## Highlights

- Supports ChatGPT for Windows, Claude Desktop / Claude Code views, and Antigravity 2.0.
- Supports Google, Alibaba Cloud, Baidu, and Tencent Cloud machine translation.
- Provides automatic failover, fixed-provider selection, and configurable provider priority.
- Automatically skips providers that have reached their locally configured monthly character limit.
- Preserves fenced code, inline code, URLs, email addresses, Markdown link destinations, file paths, and common command lines.
- Offers immediate-send and preview-before-send modes.
- Includes English and Simplified Chinese interfaces in a professional dark console.
- Stores credentials in Windows Credential Manager instead of settings files or logs.
- Verifies every composer replacement and restores the original draft on failure. Untranslated content is never sent silently.

## Requirements

- Windows 10 or Windows 11, x64
- ChatGPT, Claude Desktop, or Antigravity 2.0 desktop client
- API credentials for at least one supported translation provider

The self-contained build from GitHub Releases does not require a separate .NET Runtime installation.

## Quick Start

1. Download the latest `PromptBridge-*-win-x64.zip` from [GitHub Releases](https://github.com/yzk77/PromptBridge/releases).
2. Extract the archive and run `ChineseToChatGPT.exe`.
3. During first-run setup, select a translation provider, enter its API credentials, and run the connection test.
4. Open a supported AI client and focus its message composer.
5. Type in Chinese and press `Ctrl+Shift+Enter`.

Immediate send is enabled by default. You can switch to preview mode under **Send & Behavior**: the first hotkey press translates the draft, and a second press sends it if the content has not changed.

## Supported Translation Providers

| Provider | Credentials | Default local monthly limit |
|---|---|---:|
| Alibaba Cloud Machine Translation | AccessKey ID / AccessKey Secret | 1,000,000 characters |
| Baidu Translate | APPID / Secret Key | 50,000 characters |
| Tencent Cloud Machine Translation | SecretId / SecretKey / Region | 5,000,000 characters |
| Google Cloud Translation Basic v2 | API Key | 500,000 characters |

The default automatic failover order is Alibaba Cloud, Baidu, Tencent Cloud, then Google. Only providers with configured credentials participate. These values are local safety limits, not authoritative remaining quotas reported by provider accounts. Leave additional headroom if the same credentials are used by other devices or applications.

## Safety Model

Prompt Bridge uses a transactional workflow:

1. Capture the original draft without modifying the composer.
2. Segment the draft and protect technical content.
3. Call the translation provider and reconstruct the message.
4. Write the final English message.
5. Read it back and verify it through UI Automation or the clipboard fallback path.
6. Send only after the content, window, and focus have all been verified.
7. Restore the original draft if any step fails. If restoration cannot be confirmed, display a high-priority warning and do not send.

The clipboard fallback temporarily preserves and restores all accessible clipboard formats. Diagnostic logs never include drafts, translations, clipboard contents, credentials, signatures, authorization headers, request bodies, or complete request URLs.

## Supported Clients

| Client | Process | Status |
|---|---|---|
| ChatGPT for Windows | `ChatGPT.exe` | Supported |
| Claude Desktop / Claude Code view | `Claude.exe` | Supported |
| Antigravity 2.0 | `Antigravity.exe` | Supported |
| Browser versions | — | Not currently supported |
| macOS | — | Not currently supported |

The hotkey is handled only when a supported foreground process and editable message composer are focused. Normal Enter behavior and each client's built-in Send button remain unchanged.

## Privacy

- API credentials are stored in Windows Credential Manager.
- Non-sensitive settings are stored under `%LocalAppData%\ChineseToChatGPT`.
- Diagnostics contain only timestamps, providers, operation types, error categories/codes, HTTP status codes, durations, and character counts.
- Translation content is sent only to the currently selected translation provider.
- The final prompt is written only to the focused composer of a supported client.

## Build from Source

Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), then run:

```powershell
dotnet restore ChineseToChatGPT.sln
dotnet build ChineseToChatGPT.sln -c Release --no-restore
dotnet run --project tests\ChineseToChatGPT.Tests\ChineseToChatGPT.Tests.csproj -c Release --no-build
```

To publish a self-contained, single-file Windows x64 build:

```powershell
dotnet publish src\ChineseToChatGPT.App\ChineseToChatGPT.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish
```

The test runner has no third-party testing framework dependency and never connects to a translation provider.

## Project Structure

```text
src/
  ChineseToChatGPT.App/    WPF, tray integration, Windows UI Automation, and credential storage
  ChineseToChatGPT.Core/   Translation providers, text protection, failover, and transactional workflow
tests/
  ChineseToChatGPT.Tests/  Unit and integration tests without an external test framework
.github/workflows/         Continuous integration and release automation
```

## Known Limitations

- Desktop client updates may change their accessibility structure. Prompt Bridge stops safely if identification or verification fails.
- Current binaries are not code-signed, so Windows SmartScreen may display a warning.
- The Tencent Cloud adapter uses the project's existing TMT request path. If an account no longer supports that operation, automatic mode safely moves to the next provider.
- Prompt Bridge does not intercept normal Enter presses, mouse-based Send actions, or browser composers.

## Contributing and Security

- Read [CONTRIBUTING.md](CONTRIBUTING.md) before submitting code.
- Report vulnerabilities or issues that could expose prompts or credentials privately by following [SECURITY.md](SECURITY.md). Do not open a public issue for them.
- See [CHANGELOG.md](CHANGELOG.md) for version history.

## Disclaimer

This project is not affiliated with OpenAI, Anthropic, Google, Alibaba Cloud, Baidu, Tencent Cloud, or Antigravity. Third-party translation APIs may incur charges. You are responsible for managing account permissions, quotas, and billing.

## License

This project is licensed under the [MIT License](LICENSE).
