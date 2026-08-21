using System.Net;
using System.Text;
using System.Text.Json;
using ChineseToChatGPT.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Message formatting is exact and deduplicated", TestMessageFormatting),
    ("Provider defaults preserve old settings and enable automatic mode", TestProviderDefaults),
    ("Language values are stable and Simplified Chinese is supported", TestLanguageSettings),
    ("Desktop client registry matches only supported conversation clients", TestConversationClients),
    ("Claude Code CLI terminals are distinguished from desktop clients", TestClaudeCodeCliDetection),
    ("Chinese detection handles CJK and English", TestChineseDetection),
    ("Technical text is protected and reconstruction is lossless", TestSegmentation),
    ("Unicode-safe chunking reconstructs the source", TestChunking),
    ("Google responses preserve protected text and decode entities", TestGoogleTranslation),
    ("Google transient failures retry and authentication errors classify", TestGoogleErrors),
    ("Alibaba Cloud request signing and response parsing work", TestAlibabaTranslation),
    ("Baidu official signature example and response parsing work", TestBaiduTranslation),
    ("Tencent Cloud request signing and response parsing work", TestTencentTranslation),
    ("Provider errors are classified and sanitized", TestProviderErrors),
    ("Automatic mode fails over and remembers exhausted providers", TestAutomaticFailover),
    ("Automatic mode skips unconfigured providers and aggregates failures", TestAutomaticSummary),
    ("Automatic mode respects local monthly character limits", TestLocalQuotaFailover),
    ("Composer text normalization handles safe editor differences", TestComposerNormalization),
    ("Antigravity verification removes only editor sentinel characters", TestAntigravityNormalization),
    ("Composer verification polling accepts delayed updates", TestDelayedComposerVerification),
    ("Clipboard fallback write is accepted only after verification", TestClipboardFallbackWorkflow),
    ("Immediate workflow replaces, verifies, and sends", TestImmediateWorkflow),
    ("English workflow skips translation", TestEnglishWorkflow),
    ("Preview workflow sends unchanged preview on second invocation", TestPreviewWorkflow),
    ("Edited preview is translated again", TestEditedPreview),
    ("Verification failure restores the original", TestRollback),
    ("Unverified recovery raises a high-priority error", TestRecoveryFailure),
    ("Focus loss never sends", TestFocusLoss),
    ("Concurrent workflow requests are suppressed", TestConcurrency)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.WriteLine($"FAIL  {test.Name}");
        Console.WriteLine($"      {exception}");
    }
}

Console.WriteLine();
Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} tests passed.");
if (failures.Count > 0)
{
    Environment.ExitCode = 1;
}

return;

static Task TestMessageFormatting()
{
    Assert.Equal(
        "Hello\r\n\r\nPlease reply in Chinese.",
        MessageFormatter.Format("Hello"));
    Assert.Equal(
        "Hello\r\n\r\nPlease reply in Chinese.",
        MessageFormatter.Format("Hello\r\n\r\nPlease reply in Chinese.\r\n"));
    Assert.Equal(
        "Hello\r\nPlease reply in Chinese.",
        MessageFormatter.FormatForClient("Hello", ConversationClient.Antigravity));
    Assert.Equal(
        "Hello\r\n\r\nPlease reply in Chinese.",
        MessageFormatter.FormatForClient("Hello", ConversationClient.ClaudeDesktop));
    return Task.CompletedTask;
}

static Task TestProviderDefaults()
{
    Assert.Equal(0, (int)TranslationProvider.Google);
    Assert.Equal(1, (int)TranslationProvider.AlibabaCloud);
    Assert.Equal(2, (int)TranslationProvider.Baidu);
    Assert.Equal(TranslationProvider.Automatic, new AppSettings().TranslationProvider);
    Assert.Equal("ap-beijing", new AppSettings().TencentRegion);
    Assert.Equal(TranslationProvider.AlibabaCloud, new AppSettings().ProviderPriority[0]);
    Assert.Equal(500_000L, new ProviderQuotaSettings().Google);
    Assert.Equal(5_000_000L, new ProviderQuotaSettings().TencentCloud);
    return Task.CompletedTask;
}

static Task TestLanguageSettings()
{
    Assert.Equal(0, (int)AppLanguage.English);
    Assert.Equal(1, (int)AppLanguage.SimplifiedChinese);
    var settings = new AppSettings { Language = AppLanguage.SimplifiedChinese };
    Assert.Equal(AppLanguage.SimplifiedChinese, settings.Language);
    return Task.CompletedTask;
}

static Task TestConversationClients()
{
    Assert.Equal(
        ConversationClient.ChatGPT,
        ConversationClientRegistry.MatchProcessName("ChatGPT.exe")!.Client);
    Assert.Equal(
        ConversationClient.ClaudeDesktop,
        ConversationClientRegistry.MatchProcessName("claude")!.Client);
    Assert.Equal(
        ConversationClient.Antigravity,
        ConversationClientRegistry.MatchProcessName("ANTIGRAVITY.EXE")!.Client);
    Assert.Equal(
        "Antigravity 2.0",
        ConversationClientRegistry.MatchProcessName("Antigravity")!.DisplayName);
    Assert.True(ConversationClientRegistry.MatchProcessName("Antigravity IDE.exe") is null);
    Assert.True(ConversationClientRegistry.MatchProcessName("Code.exe") is null);
    Assert.True(ConversationClientRegistry.MatchProcessName("not-chatgpt.exe") is null);
    Assert.Equal(3, ConversationClientRegistry.Supported.Count);
    return Task.CompletedTask;
}

static Task TestClaudeCodeCliDetection()
{
    Assert.True(ConversationClientRegistry.IsClaudeCodeCli(
        "WindowsTerminal.exe",
        "Claude Code"));
    Assert.True(ConversationClientRegistry.IsClaudeCodeCli(
        "pwsh",
        "project — claude"));
    Assert.False(ConversationClientRegistry.IsClaudeCodeCli(
        "WindowsTerminal",
        "PowerShell"));
    Assert.False(ConversationClientRegistry.IsClaudeCodeCli(
        "Claude.exe",
        "Claude"));
    return Task.CompletedTask;
}

static Task TestChineseDetection()
{
    Assert.True(MessageFormatter.ContainsChinese("请解释 this code"));
    Assert.False(MessageFormatter.ContainsChinese("Explain this code."));
    return Task.CompletedTask;
}

static Task TestSegmentation()
{
    const string source = """
        请解释 `Console.WriteLine("x")` 和 https://example.com/docs。
        ```csharp
        var 路径 = @"C:\Temp\file.txt";
        ```
        git status --short
        邮箱 test@example.com，文件 C:\work\a.cs 和 /usr/local/bin/node。
        [文档](https://example.com/a)
        """;
    var segmenter = new ProtectedTextSegmenter();
    var segments = segmenter.Segment(source);
    Assert.Equal(source, string.Concat(segments.Select(static segment => segment.Text)));

    var protectedText = string.Concat(segments.Where(static segment => segment.IsProtected)
        .Select(static segment => segment.Text));
    Assert.Contains("`Console.WriteLine(\"x\")`", protectedText);
    Assert.Contains("https://example.com/docs", protectedText);
    Assert.Contains("git status --short", protectedText);
    Assert.Contains("C:\\work\\a.cs", protectedText);
    Assert.Contains("/usr/local/bin/node", protectedText);
    return Task.CompletedTask;
}

static Task TestChunking()
{
    const string source = "你好🙂世界。这是一段用于测试的文字。";
    var chunks = GoogleTranslationService.Chunk(source, 7);
    Assert.Equal(source, string.Concat(chunks));
    Assert.True(chunks.All(static chunk =>
        chunk.Length == 0 || !char.IsHighSurrogate(chunk[^1])));
    return Task.CompletedTask;
}

static async Task TestGoogleTranslation()
{
    var handler = new StubHttpHandler(request =>
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        using var json = JsonDocument.Parse(body);
        var count = json.RootElement.GetProperty("q").GetArrayLength();
        var translations = Enumerable.Range(0, count)
            .Select(index => new { translatedText = index == 0 ? "Hello &amp; welcome" : $"translated-{index}" });
        return Json(new { data = new { translations } });
    });
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
    var service = new GoogleTranslationService(client, new ProtectedTextSegmenter(), () => "test-key");

    var result = await service.TranslateToEnglishAsync("你好 `do-not-change`", CancellationToken.None);
    Assert.Contains("Hello & welcome", result);
    Assert.Contains("`do-not-change`", result);
}

static async Task TestGoogleErrors()
{
    var attempts = 0;
    var retryHandler = new StubHttpHandler(_ =>
    {
        attempts++;
        return attempts < 3
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : Json(new { data = new { translations = new[] { new { translatedText = "Hello" } } } });
    });
    using var retryClient = new HttpClient(retryHandler);
    var retryService = new GoogleTranslationService(
        retryClient,
        new ProtectedTextSegmenter(),
        () => "key");
    Assert.Equal("Hello", await retryService.TranslateToEnglishAsync("你好", CancellationToken.None));
    Assert.Equal(3, attempts);

    using var authClient = new HttpClient(new StubHttpHandler(
        _ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
    var authService = new GoogleTranslationService(
        authClient,
        new ProtectedTextSegmenter(),
        () => "bad");
    var exception = await Assert.ThrowsAsync<CompanionException>(
        () => authService.TranslateToEnglishAsync("你好", CancellationToken.None));
    Assert.Equal(ErrorCategory.Authentication, exception.Category);
}

static async Task TestAlibabaTranslation()
{
    using (var fixedRequest = AlibabaCloudTranslationService.CreateSignedRequest(
        "你好",
        "en",
        "ACCESS_KEY_REDACTED",
        "ACCESS_KEY_REDACTED",
        DateTimeOffset.Parse("2026-07-27T11:02:30Z"),
        "nonce-123"))
    {
        Assert.Equal(
            "https://mt.cn-hangzhou.aliyuncs.com/?AccessKeyId=ACCESS_KEY_REDACTED&Action=TranslateGeneral&Format=JSON&FormatType=text&RegionId=cn-hangzhou&Scene=general&SignatureMethod=HMAC-SHA1&SignatureNonce=nonce-123&SignatureVersion=1.0&SourceLanguage=auto&SourceText=%E4%BD%A0%E5%A5%BD&TargetLanguage=en&Timestamp=2026-07-27T11%3A02%3A30Z&Version=2018-10-12&Signature=leh%2FXr39AJB59kEqdoDlUcMqcZU%3D",
            fixedRequest.RequestUri!.AbsoluteUri);
    }

    var handler = new StubHttpHandler(request =>
    {
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("mt.cn-hangzhou.aliyuncs.com", request.RequestUri!.Host);
        var query = request.RequestUri.Query;
        Assert.Contains("Action=TranslateGeneral", query);
        Assert.Contains("Version=2018-10-12", query);
        Assert.Contains("RegionId=cn-hangzhou", query);
        Assert.Contains("SourceText=", query);
        Assert.Contains("Signature=", query);
        return Json(new
        {
            Code = "200",
            Message = "success",
            Data = new { Translated = "Hello", DetectedLanguage = "zh" }
        });
    });
    using var client = new HttpClient(handler);
    var service = new AlibabaCloudTranslationService(
        client,
        new ProtectedTextSegmenter(),
        () => "ACCESS_KEY_REDACTED",
        () => "ACCESS_KEY_REDACTED");

    Assert.Equal(
        "Hello`code`",
        await service.TranslateToEnglishAsync("你好`code`", CancellationToken.None));
}

static async Task TestBaiduTranslation()
{
    Assert.Equal(
        "a1a7461d92e5194c5cae3182b5b24de1",
        BaiduTranslationService.CreateSignature(
            "2015063000000001",
            "apple",
            "65478",
            "1234567890"));

    var handler = new StubHttpHandler(request =>
    {
        Assert.Equal(
            "application/x-www-form-urlencoded",
            request.Content!.Headers.ContentType!.MediaType);
        return Json(new
        {
            from = "zh",
            to = "en",
            trans_result = new[] { new { src = "你好", dst = "Hello" } }
        });
    });
    using var client = new HttpClient(handler);
    var service = new BaiduTranslationService(
        client,
        new ProtectedTextSegmenter(),
        () => "TEST_APP_ID",
        () => "ACCESS_KEY_REDACTED");

    Assert.Equal("Hello", await service.TranslateToEnglishAsync("你好", CancellationToken.None));
}

static async Task TestTencentTranslation()
{
    const string fixedBody =
        """{"SourceText":"你好","Source":"auto","Target":"en","ProjectId":0}""";
    using (var fixedRequest = TencentCloudTranslationService.CreateSignedRequest(
        fixedBody,
        "AKID_REDACTED",
        "ACCESS_KEY_REDACTED",
        "ap-beijing",
        DateTimeOffset.FromUnixTimeSeconds(1551113065)))
    {
        Assert.Equal(
            "TC3-HMAC-SHA256 Credential=AKID_REDACTED/2019-02-25/tmt/tc3_request, SignedHeaders=content-type;host;x-tc-action, Signature=6e4bf62d45b92c49275baaa63b2428275df71a07f1e1bfa5d6b3d4c819521f44",
            fixedRequest.Headers.GetValues("Authorization").Single());
    }

    var handler = new StubHttpHandler(request =>
    {
        Assert.Equal("tmt.tencentcloudapi.com", request.Headers.Host);
        Assert.Equal("TextTranslate", request.Headers.GetValues("X-TC-Action").Single());
        Assert.Equal("ap-beijing", request.Headers.GetValues("X-TC-Region").Single());
        Assert.True(request.Headers.GetValues("Authorization").Single()
            .StartsWith("TC3-HMAC-SHA256 Credential=AKID_REDACTED/", StringComparison.Ordinal));
        Assert.Contains(
            "SignedHeaders=content-type;host;x-tc-action",
            request.Headers.GetValues("Authorization").Single());
        return Json(new
        {
            Response = new
            {
                TargetText = "Hello",
                Source = "zh",
                Target = "en",
                UsedAmount = 2,
                RequestId = "request-id"
            }
        });
    });
    using var client = new HttpClient(handler);
    var service = new TencentCloudTranslationService(
        client,
        new ProtectedTextSegmenter(),
        () => "AKID_REDACTED",
        () => "ACCESS_KEY_REDACTED");

    Assert.Equal("Hello", await service.TranslateToEnglishAsync("你好", CancellationToken.None));

    using var quotaClient = new HttpClient(new StubHttpHandler(_ => Json(new
    {
        Response = new
        {
            Error = new
            {
                Code = "FailedOperation.NoFreeAmount",
                Message = "No free amount"
            },
            RequestId = "request-id"
        }
    })));
    var quotaService = new TencentCloudTranslationService(
        quotaClient,
        new ProtectedTextSegmenter(),
        () => "AKID_REDACTED",
        () => "ACCESS_KEY_REDACTED");
    var exception = await Assert.ThrowsAsync<CompanionException>(
        () => quotaService.TranslateToEnglishAsync("你好", CancellationToken.None));
    Assert.Equal(ErrorCategory.Quota, exception.Category);
}

static async Task TestProviderErrors()
{
    Assert.Equal(ErrorCategory.InvalidSignature, BaiduTranslationService.ClassifyError("54001"));
    Assert.Equal(
        ErrorCategory.InvalidSignature,
        TencentCloudTranslationService.ClassifyError("AuthFailure.SignatureFailure"));
    var numeric = BaiduTranslationService.ParseResponse(
        """{"error_code":54003,"error_msg":"rate limited"}""");
    Assert.Equal("54003", numeric.ErrorCode);
    var alibabaNumeric = AlibabaCloudTranslationService.ParseResponse(
        """{"Code":200,"Data":{"Translated":"Hello","DetectedLanguage":"zh"}}""");
    Assert.Equal("200", alibabaNumeric.Code);

    var longMessage = new string('x', 500) + "\r\nsecret";
    var sanitized = ProviderErrorSanitizer.Sanitize(longMessage)!;
    Assert.True(sanitized.Length <= 301);
    Assert.False(sanitized.Contains('\r'));
    Assert.False(sanitized.Contains('\n'));
    await Task.CompletedTask;
}

static async Task TestAutomaticFailover()
{
    var google = new ThrowingTranslator(
        new CompanionException(ErrorCategory.Quota, "exhausted"));
    var alibaba = new FakeTranslator("Alibaba result");
    var usage = new FakeUsageTracker();
    var selected = TranslationProvider.Automatic;
    var router = new ProviderTranslationService(
        () => selected,
        new Dictionary<TranslationProvider, ITranslationService>
        {
            [TranslationProvider.Google] = google,
            [TranslationProvider.AlibabaCloud] = alibaba
        },
        provider => provider is TranslationProvider.Google or TranslationProvider.AlibabaCloud,
        _ => 1_000,
        usage,
        () => [TranslationProvider.Google, TranslationProvider.AlibabaCloud]);

    Assert.Equal(
        "Alibaba result",
        await router.TranslateToEnglishAsync("你好", CancellationToken.None));
    Assert.Equal(1, google.CallCount);
    Assert.Equal(1, alibaba.CallCount);
    Assert.True(usage.Get(TranslationProvider.Google).ProviderReportedExhausted);

    await router.TranslateToEnglishAsync("再次", CancellationToken.None);
    Assert.Equal(1, google.CallCount);
    Assert.Equal(2, alibaba.CallCount);
}

static async Task TestAutomaticSummary()
{
    var baidu = new ThrowingTranslator(new CompanionException(
        ErrorCategory.RateLimit,
        "limited",
        provider: TranslationProvider.Baidu,
        providerErrorCode: "54003"));
    var tencent = new ThrowingTranslator(new CompanionException(
        ErrorCategory.Quota,
        "quota",
        provider: TranslationProvider.TencentCloud,
        providerErrorCode: "FailedOperation.NoFreeAmount"));
    var router = new ProviderTranslationService(
        () => TranslationProvider.Automatic,
        new Dictionary<TranslationProvider, ITranslationService>
        {
            [TranslationProvider.Baidu] = baidu,
            [TranslationProvider.TencentCloud] = tencent
        },
        provider => provider is TranslationProvider.Baidu or TranslationProvider.TencentCloud,
        _ => 1_000,
        new FakeUsageTracker());

    var exception = await Assert.ThrowsAsync<CompanionException>(
        () => router.TranslateToEnglishAsync("你好", CancellationToken.None));
    Assert.Equal(ErrorCategory.ProviderUnavailable, exception.Category);
    Assert.Contains("AlibabaCloud: not configured", exception.Message);
    Assert.Contains("Baidu: rate limited (54003)", exception.Message);
    Assert.Contains("TencentCloud: quota exhausted (FailedOperation.NoFreeAmount)", exception.Message);
    Assert.Contains("Google: not configured", exception.Message);
}

static async Task TestLocalQuotaFailover()
{
    var google = new FakeTranslator("Google result");
    var alibaba = new FakeTranslator("Alibaba result");
    var usage = new FakeUsageTracker();
    usage.RecordSuccess(TranslationProvider.Google, 9);
    var router = new ProviderTranslationService(
        () => TranslationProvider.Automatic,
        new Dictionary<TranslationProvider, ITranslationService>
        {
            [TranslationProvider.Google] = google,
            [TranslationProvider.AlibabaCloud] = alibaba
        },
        provider => provider is TranslationProvider.Google or TranslationProvider.AlibabaCloud,
        provider => provider == TranslationProvider.Google ? 10 : 100,
        usage);

    Assert.Equal(
        "Alibaba result",
        await router.TranslateToEnglishAsync("你好", CancellationToken.None));
    Assert.Equal(0, google.CallCount);
    Assert.Equal(1, alibaba.CallCount);
}

static Task TestComposerNormalization()
{
    const string expected = "Line 1\r\nLine 2 😀\r\n\r\nPlease reply in Chinese.";
    Assert.True(ComposerTextNormalizer.AreEquivalent(
        expected,
        "\u200BLine\u00A01\u2028Line 2 😀\u2029\rPlease reply in Chinese.\u2029\u200B"));
    Assert.True(ComposerTextNormalizer.AreEquivalent("a\uFEFFb", "ab"));
    Assert.True(ComposerTextNormalizer.AnyEquivalent(
        "new\r\ntext",
        ["old text", "new\u2028text\u2029"]));
    Assert.False(ComposerTextNormalizer.AreEquivalent("word  gap", "word gap"));
    Assert.False(ComposerTextNormalizer.AreEquivalent("prefix complete", "prefix"));
    Assert.Equal("a\u200Bb", ComposerTextNormalizer.NormalizeComposerText("a\u200Bb"));
    return Task.CompletedTask;
}

static Task TestAntigravityNormalization()
{
    const string expected = "Hello 👨‍👩‍👧\r\n\r\nPlease reply in Chinese.";
    const string actual =
        "Hel\u200Blo 👨‍👩‍👧\r\n\r\nPlease\u2060 reply in Chinese.\u200E";
    Assert.True(ComposerTextNormalizer.AreEquivalentForClient(
        expected,
        actual,
        ConversationClient.Antigravity));
    Assert.False(ComposerTextNormalizer.AreEquivalentForClient(
        expected,
        actual,
        ConversationClient.ChatGPT));
    Assert.Contains(
        "\u200D",
        ComposerTextNormalizer.NormalizeForClient(
            expected,
            ConversationClient.Antigravity));
    return Task.CompletedTask;
}

static async Task TestDelayedComposerVerification()
{
    var reads = 0;
    var result = await ComposerVerificationPoller.VerifyAsync(
        (_, _) => Task.FromResult(++reads == 3),
        CancellationToken.None,
        [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
    Assert.True(result.Success);
    Assert.Equal(3, result.Attempts);
    Assert.Equal(3, reads);
}

static async Task TestClipboardFallbackWorkflow()
{
    var composer = new FakeComposer("你好")
    {
        WriteMethod = ComposerWriteMethod.Clipboard
    };
    var translator = new FakeTranslator("Hello");
    var workflow = new TranslationWorkflow(composer, translator, new NullLogger());
    await workflow.ExecuteAsync(SendMode.Immediate, CancellationToken.None);

    Assert.Equal(1, translator.CallCount);
    Assert.Equal(1, composer.SendCount);
    Assert.Equal(ComposerWriteMethod.Clipboard, composer.LastWriteMethod);
}

static async Task TestImmediateWorkflow()
{
    var composer = new FakeComposer("你好");
    var translator = new FakeTranslator("Hello");
    var workflow = new TranslationWorkflow(composer, translator, new NullLogger());
    var result = await workflow.ExecuteAsync(SendMode.Immediate, CancellationToken.None);

    Assert.Equal(WorkflowOutcome.Sent, result.Outcome);
    Assert.Equal("Hello\r\n\r\nPlease reply in Chinese.", composer.Text);
    Assert.Equal(1, composer.SendCount);
    Assert.Equal(1, translator.CallCount);
}

static async Task TestEnglishWorkflow()
{
    var composer = new FakeComposer("Explain this.");
    var translator = new FakeTranslator("must not be used");
    var workflow = new TranslationWorkflow(composer, translator, new NullLogger());
    await workflow.ExecuteAsync(SendMode.Immediate, CancellationToken.None);

    Assert.Equal(0, translator.CallCount);
    Assert.Equal("Explain this.\r\n\r\nPlease reply in Chinese.", composer.Text);
}

static async Task TestPreviewWorkflow()
{
    var composer = new FakeComposer("你好");
    var workflow = new TranslationWorkflow(composer, new FakeTranslator("Hello"), new NullLogger());
    var first = await workflow.ExecuteAsync(SendMode.Preview, CancellationToken.None);
    var second = await workflow.ExecuteAsync(SendMode.Preview, CancellationToken.None);

    Assert.Equal(WorkflowOutcome.Previewed, first.Outcome);
    Assert.Equal(WorkflowOutcome.Sent, second.Outcome);
    Assert.Equal(1, composer.SendCount);
}

static async Task TestEditedPreview()
{
    var composer = new FakeComposer("你好");
    var translator = new FakeTranslator("Hello");
    var workflow = new TranslationWorkflow(composer, translator, new NullLogger());
    await workflow.ExecuteAsync(SendMode.Preview, CancellationToken.None);
    composer.Text = "重新翻译";
    var result = await workflow.ExecuteAsync(SendMode.Preview, CancellationToken.None);

    Assert.Equal(WorkflowOutcome.Previewed, result.Outcome);
    Assert.Equal(2, translator.CallCount);
    Assert.Equal(0, composer.SendCount);
}

static async Task TestRollback()
{
    var composer = new FakeComposer("原文") { VerificationResult = false };
    var workflow = new TranslationWorkflow(composer, new FakeTranslator("Translated"), new NullLogger());
    var exception = await Assert.ThrowsAsync<CompanionException>(
        () => workflow.ExecuteAsync(SendMode.Immediate, CancellationToken.None));

    Assert.Equal(ErrorCategory.ReplacementVerification, exception.Category);
    Assert.Equal("原文", composer.Text);
    Assert.Equal(1, composer.RestoreCount);
    Assert.Equal(0, composer.SendCount);
}

static async Task TestRecoveryFailure()
{
    var composer = new FakeComposer("原文")
    {
        VerificationResult = false,
        RecoveryResult = false
    };
    var workflow = new TranslationWorkflow(composer, new FakeTranslator("Translated"), new NullLogger());
    var exception = await Assert.ThrowsAsync<CompanionException>(
        () => workflow.ExecuteAsync(SendMode.Immediate, CancellationToken.None));

    Assert.Equal(ErrorCategory.ComposerRecovery, exception.Category);
    Assert.Equal(0, composer.SendCount);
}

static async Task TestFocusLoss()
{
    var composer = new FakeComposer("原文")
    {
        ReplacementException = new CompanionException(
            ErrorCategory.UnsupportedWindow,
            "focus changed"),
        RecoveryResult = false
    };
    var workflow = new TranslationWorkflow(composer, new FakeTranslator("Translated"), new NullLogger());
    await Assert.ThrowsAsync<CompanionException>(
        () => workflow.ExecuteAsync(SendMode.Immediate, CancellationToken.None));
    Assert.Equal(0, composer.SendCount);
}

static async Task TestConcurrency()
{
    var composer = new FakeComposer("你好");
    var translator = new BlockingTranslator();
    var workflow = new TranslationWorkflow(composer, translator, new NullLogger());

    var first = workflow.ExecuteAsync(SendMode.Immediate, CancellationToken.None);
    await translator.Started.Task;
    var second = await workflow.ExecuteAsync(SendMode.Immediate, CancellationToken.None);
    Assert.Equal(WorkflowOutcome.Busy, second.Outcome);

    translator.Complete.SetResult("Hello");
    await first;
}

static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
{
    Content = new StringContent(
        JsonSerializer.Serialize(value),
        Encoding.UTF8,
        "application/json")
};

file sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => Task.FromResult(responder(request));
}

file sealed class FakeComposer(string text) : IComposerAdapter
{
    public string Text { get; set; } = text;
    public int SendCount { get; private set; }
    public int RestoreCount { get; private set; }
    public bool VerificationResult { get; init; } = true;
    public bool RecoveryResult { get; init; } = true;
    public ComposerWriteMethod WriteMethod { get; init; } = ComposerWriteMethod.UiAutomation;
    public ComposerWriteMethod? LastWriteMethod { get; private set; }
    public CompanionException? ReplacementException { get; init; }

    public Task<ComposerSnapshot> CaptureAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ComposerSnapshot(Text, 42));

    public Task<ComposerWriteResult> ReplaceAndVerifyAsync(
        ComposerSnapshot original,
        string replacement,
        CancellationToken cancellationToken)
    {
        if (ReplacementException is not null)
        {
            return Task.FromException<ComposerWriteResult>(ReplacementException);
        }

        Text = replacement;
        LastWriteMethod = WriteMethod;
        return Task.FromResult(new ComposerWriteResult(
            VerificationResult,
            WriteMethod,
            1,
            TimeSpan.Zero));
    }

    public Task SendAsync(nint windowHandle, CancellationToken cancellationToken)
    {
        SendCount++;
        return Task.CompletedTask;
    }

    public Task<ComposerWriteResult> RestoreAndVerifyAsync(
        ComposerSnapshot original,
        CancellationToken cancellationToken)
    {
        RestoreCount++;
        Text = original.Text;
        return Task.FromResult(new ComposerWriteResult(
            RecoveryResult,
            WriteMethod,
            1,
            TimeSpan.Zero));
    }
}

file class FakeTranslator(string result) : ITranslationService
{
    public int CallCount { get; private set; }

    public virtual Task<string> TranslateToEnglishAsync(string text, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(result);
    }
}

file sealed class BlockingTranslator : ITranslationService
{
    public TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<string> Complete { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<string> TranslateToEnglishAsync(string text, CancellationToken cancellationToken)
    {
        Started.SetResult();
        return Complete.Task;
    }
}

file sealed class ThrowingTranslator(CompanionException exception) : ITranslationService
{
    public int CallCount { get; private set; }

    public Task<string> TranslateToEnglishAsync(string text, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromException<string>(exception);
    }
}

file sealed class FakeUsageTracker : ITranslationUsageTracker
{
    private readonly Dictionary<TranslationProvider, ProviderUsage> _usage = [];

    public ProviderUsage Get(TranslationProvider provider) =>
        _usage.TryGetValue(provider, out var usage) ? usage : new ProviderUsage(0, false);

    public void RecordSuccess(TranslationProvider provider, int characters)
    {
        var current = Get(provider);
        _usage[provider] = current with { UsedCharacters = current.UsedCharacters + characters };
    }

    public void MarkProviderExhausted(TranslationProvider provider)
    {
        var current = Get(provider);
        _usage[provider] = current with { ProviderReportedExhausted = true };
    }
}

file sealed class NullLogger : IDiagnosticLogger
{
    public void Event(string eventName, int characterCount = 0, long durationMilliseconds = 0)
    {
    }

    public void Error(ErrorCategory category, int characterCount = 0)
    {
    }

    public void ProviderTest(ProviderTestResult result, int inputCharacters)
    {
    }

    public void ComposerVerification(ComposerVerificationDiagnostic diagnostic)
    {
    }
}

file static class Assert
{
    public static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    public static void False(bool condition) => True(!condition);

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected <{expected}> but found <{actual}>.");
        }
    }

    public static void Contains(string expectedSubstring, string actual)
    {
        if (!actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected <{actual}> to contain <{expectedSubstring}>.");
        }
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
