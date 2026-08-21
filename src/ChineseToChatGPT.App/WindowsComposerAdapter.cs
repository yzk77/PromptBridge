using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;
using System.Windows.Threading;
using ChineseToChatGPT.Core;

namespace ChineseToChatGPT.App;

internal sealed class WindowsComposerAdapter(
    Dispatcher dispatcher,
    IDiagnosticLogger logger) : IComposerAdapter
{
    private const byte VirtualKeyControl = 0x11;
    private const byte VirtualKeyShift = 0x10;
    private const byte VirtualKeyMenu = 0x12;
    private const byte VirtualKeyA = 0x41;
    private const byte VirtualKeyC = 0x43;
    private const byte VirtualKeyV = 0x56;
    private const byte VirtualKeyReturn = 0x0D;
    private const uint KeyEventKeyUp = 0x0002;

    public async Task<ComposerSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var context = await dispatcher.InvokeAsync(
            ValidateContext,
            DispatcherPriority.Send,
            cancellationToken);
        var candidates = ReadWithAutomation(context.FocusedElement);
        var text = candidates.FirstOrDefault()?.Text;
        if (text is null)
        {
            text = await CaptureWithClipboardAsync(
                    context.WindowHandle,
                    context.Client.Client,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new ComposerSnapshot(text, context.WindowHandle, context.Client.Client);
    }

    public Task<ComposerWriteResult> ReplaceAndVerifyAsync(
        ComposerSnapshot original,
        string replacement,
        CancellationToken cancellationToken) =>
        WriteAndVerifyAsync(
            original.WindowHandle,
            original.Client,
            replacement,
            cancellationToken);

    public Task<ComposerWriteResult> RestoreAndVerifyAsync(
        ComposerSnapshot original,
        CancellationToken cancellationToken) =>
        WriteAndVerifyAsync(
            original.WindowHandle,
            original.Client,
            original.Text,
            cancellationToken);

    public async Task SendAsync(nint windowHandle, CancellationToken cancellationToken)
    {
        await WaitForModifiersReleasedAsync(cancellationToken).ConfigureAwait(false);
        await dispatcher.InvokeAsync(() =>
        {
            var context = ValidateContext();
            EnsureWindow(context.WindowHandle, windowHandle);
            PressKey(VirtualKeyReturn);
        }, DispatcherPriority.Send, cancellationToken);
    }

    private async Task<ComposerWriteResult> WriteAndVerifyAsync(
        nint expectedWindow,
        ConversationClient expectedClient,
        string text,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (expectedClient == ConversationClient.Antigravity)
        {
            var antigravityVerification = await ReplaceWithClipboardAndVerifyAsync(
                expectedWindow,
                expectedClient,
                text,
                stopwatch,
                cancellationToken).ConfigureAwait(false);
            return new ComposerWriteResult(
                antigravityVerification.Success,
                ComposerWriteMethod.Clipboard,
                antigravityVerification.Attempts,
                stopwatch.Elapsed);
        }

        var setWithAutomation = await dispatcher.InvokeAsync(() =>
        {
            var context = ValidateContext();
            EnsureContext(context, expectedWindow, expectedClient);
            return TrySetWithAutomation(context.FocusedElement, text);
        }, DispatcherPriority.Send, cancellationToken);

        if (setWithAutomation)
        {
            var automationVerification = await PollVerifyAsync(
                expectedWindow,
                expectedClient,
                text,
                ComposerWriteMethod.UiAutomation,
                stopwatch,
                cancellationToken).ConfigureAwait(false);
            if (automationVerification.Success)
            {
                return new ComposerWriteResult(
                    true,
                    ComposerWriteMethod.UiAutomation,
                    automationVerification.Attempts,
                    stopwatch.Elapsed);
            }
        }

        var clipboardVerification = await ReplaceWithClipboardAndVerifyAsync(
            expectedWindow,
            expectedClient,
            text,
            stopwatch,
            cancellationToken).ConfigureAwait(false);
        return new ComposerWriteResult(
            clipboardVerification.Success,
            ComposerWriteMethod.Clipboard,
            clipboardVerification.Attempts,
            stopwatch.Elapsed);
    }

    private async Task<(bool Success, int Attempts)> PollVerifyAsync(
        nint expectedWindow,
        ConversationClient expectedClient,
        string expected,
        ComposerWriteMethod writeMethod,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TimeSpan> delays = writeMethod == ComposerWriteMethod.UiAutomation
            ?
            [
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(200)
            ]
            :
            [
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(200),
                TimeSpan.FromMilliseconds(350)
            ];
        var verification = await ComposerVerificationPoller.VerifyAsync(
            async (attempt, token) =>
            {
                var candidates = await dispatcher.InvokeAsync(() =>
                {
                    var context = ValidateContext();
                    EnsureContext(context, expectedWindow, expectedClient);
                    return ReadWithAutomation(context.FocusedElement);
                }, DispatcherPriority.Send, token);

                foreach (var candidate in candidates)
                {
                    var matched = LogVerification(
                        writeMethod,
                        candidate.Method,
                        attempt,
                        expected,
                        candidate.Text,
                        expectedClient,
                        stopwatch.ElapsedMilliseconds);
                    if (matched)
                    {
                        return true;
                    }
                }

                return false;
            },
            cancellationToken,
            delays).ConfigureAwait(false);
        if (verification.Success)
        {
            return verification;
        }

        var clipboardText = await CaptureWithClipboardAsync(
                expectedWindow,
                expectedClient,
                cancellationToken)
            .ConfigureAwait(false);
        var clipboardMatched = LogVerification(
            writeMethod,
            "Clipboard",
            verification.Attempts + 1,
            expected,
            clipboardText,
            expectedClient,
            stopwatch.ElapsedMilliseconds);
        return (clipboardMatched, verification.Attempts + 1);
    }

    private bool LogVerification(
        ComposerWriteMethod writeMethod,
        string readMethod,
        int attempt,
        string expected,
        string actual,
        ConversationClient client,
        long durationMilliseconds)
    {
        var normalizedExpected = ComposerTextNormalizer.NormalizeForClient(expected, client);
        var normalizedActual = ComposerTextNormalizer.NormalizeForClient(actual, client);
        var rawMatched = HashEquals(expected, actual);
        var normalizedMatched = HashEquals(normalizedExpected, normalizedActual);
        logger.ComposerVerification(new ComposerVerificationDiagnostic(
            writeMethod,
            readMethod,
            attempt,
            expected.Length,
            actual.Length,
            normalizedExpected.Length,
            normalizedActual.Length,
            rawMatched,
            normalizedMatched,
            durationMilliseconds));
        return rawMatched || normalizedMatched;
    }

    private async Task<(bool Success, int Attempts)> ReplaceWithClipboardAndVerifyAsync(
        nint expectedWindow,
        ConversationClient expectedClient,
        string replacement,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        await WaitForModifiersReleasedAsync(cancellationToken).ConfigureAwait(false);
        var clipboard = await CaptureClipboardAsync(cancellationToken).ConfigureAwait(false);
        uint ownedSequence = 0;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                var context = ValidateContext();
                EnsureContext(context, expectedWindow, expectedClient);
                System.Windows.Clipboard.SetText(
                    replacement,
                    System.Windows.TextDataFormat.UnicodeText);
                PressChord(VirtualKeyControl, VirtualKeyA);
                PressChord(VirtualKeyControl, VirtualKeyV);
                ownedSequence = GetClipboardSequenceNumber();
            }, DispatcherPriority.Send, cancellationToken);

            return await PollVerifyAsync(
                expectedWindow,
                expectedClient,
                replacement,
                ComposerWriteMethod.Clipboard,
                stopwatch,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ExternalException exception)
        {
            throw new CompanionException(
                ErrorCategory.ComposerAccess,
                "The Windows clipboard is busy.",
                exception);
        }
        finally
        {
            await RestoreClipboardAsync(clipboard, ownedSequence, replacement).ConfigureAwait(false);
        }
    }

    private async Task<string> CaptureWithClipboardAsync(
        nint expectedWindow,
        ConversationClient expectedClient,
        CancellationToken cancellationToken)
    {
        await WaitForModifiersReleasedAsync(cancellationToken).ConfigureAwait(false);
        var clipboard = await CaptureClipboardAsync(cancellationToken).ConfigureAwait(false);
        uint ownedSequence = 0;
        string? copiedText = null;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                var context = ValidateContext();
                EnsureContext(context, expectedWindow, expectedClient);
                System.Windows.Clipboard.Clear();
                PressChord(VirtualKeyControl, VirtualKeyA);
                PressChord(VirtualKeyControl, VirtualKeyC);
            }, DispatcherPriority.Send, cancellationToken);

            await Task.Delay(75, CancellationToken.None).ConfigureAwait(false);
            copiedText = await dispatcher.InvokeAsync(() =>
            {
                ownedSequence = GetClipboardSequenceNumber();
                return System.Windows.Clipboard.ContainsText()
                    ? System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText)
                    : string.Empty;
            }, DispatcherPriority.Send, CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            return copiedText;
        }
        catch (ExternalException exception)
        {
            throw new CompanionException(
                ErrorCategory.ComposerAccess,
                "The Windows clipboard is busy.",
                exception);
        }
        finally
        {
            await RestoreClipboardAsync(clipboard, ownedSequence, copiedText).ConfigureAwait(false);
        }
    }

    private async Task RestoreClipboardAsync(
        ClipboardSnapshot snapshot,
        uint ownedSequence,
        string? temporaryText)
    {
        if (ownedSequence == 0)
        {
            return;
        }

        var delays = new[] { 0, 50, 150 };
        foreach (var delay in delays)
        {
            if (delay > 0)
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }

            if (!await ClipboardStillOwnedAsync(ownedSequence, temporaryText).ConfigureAwait(false))
            {
                return;
            }

            var restored = await dispatcher.InvokeAsync(() =>
                ClipboardStillOwned(ownedSequence, temporaryText) && snapshot.TryRestore());
            if (restored)
            {
                return;
            }
        }
    }

    private async Task<bool> ClipboardStillOwnedAsync(uint sequence, string? temporaryText) =>
        await dispatcher.InvokeAsync(() => ClipboardStillOwned(sequence, temporaryText));

    private static bool ClipboardStillOwned(uint sequence, string? temporaryText)
    {
        if (GetClipboardSequenceNumber() == sequence)
        {
            return true;
        }

        if (temporaryText is null)
        {
            return false;
        }

        try
        {
            return System.Windows.Clipboard.ContainsText() &&
                string.Equals(
                    System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText),
                    temporaryText,
                    StringComparison.Ordinal);
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    private async Task<ClipboardSnapshot> CaptureClipboardAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dispatcher.InvokeAsync(
                ClipboardSnapshot.Capture,
                DispatcherPriority.Send,
                cancellationToken);
        }
        catch (ExternalException exception)
        {
            throw new CompanionException(
                ErrorCategory.ComposerAccess,
                "The Windows clipboard is busy.",
                exception);
        }
    }

    private FocusContext ValidateContext()
    {
        var windowHandle = GetForegroundWindow();
        if (windowHandle == 0)
        {
            throw new CompanionException(
                ErrorCategory.UnsupportedWindow,
                "No foreground window is available.");
        }

        GetWindowThreadProcessId(windowHandle, out var processId);
        ConversationClientProfile client;
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            var matchedClient = ConversationClientRegistry.MatchProcessName(process.ProcessName);
            if (matchedClient is null)
            {
                if (ConversationClientRegistry.IsClaudeCodeCli(
                        process.ProcessName,
                        process.MainWindowTitle))
                {
                    throw new CompanionException(
                        ErrorCategory.UnsupportedTerminal,
                        "Claude Code CLI requires a terminal-specific input workflow.");
                }

                throw new CompanionException(
                    ErrorCategory.UnsupportedWindow,
                    "Focus a supported desktop client composer before using the translation hotkey.");
            }

            client = matchedClient;
        }
        catch (ArgumentException exception)
        {
            throw new CompanionException(
                ErrorCategory.UnsupportedWindow,
                "The active supported client window is no longer available.",
                exception);
        }

        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused.Current.ProcessId != processId || !LooksEditable(focused))
            {
                throw new CompanionException(
                    ErrorCategory.UnsupportedFocus,
                    "Place the text cursor in the supported client's message composer first.");
            }

            return new FocusContext(windowHandle, focused, client);
        }
        catch (ElementNotAvailableException exception)
        {
            throw new CompanionException(
                ErrorCategory.UnsupportedFocus,
                "The focused message composer is unavailable.",
                exception);
        }
    }

    private static bool LooksEditable(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) &&
            valueObject is ValuePattern valuePattern &&
            !valuePattern.Current.IsReadOnly)
        {
            return true;
        }

        return element.TryGetCurrentPattern(TextPattern.Pattern, out _) &&
            (element.Current.ControlType == ControlType.Edit ||
             element.Current.ControlType == ControlType.Document);
    }

    private static IReadOnlyList<ReadCandidate> ReadWithAutomation(AutomationElement element)
    {
        var candidates = new List<ReadCandidate>(2);
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) &&
                valueObject is ValuePattern valuePattern)
            {
                candidates.Add(new ReadCandidate("ValuePattern", valuePattern.Current.Value));
            }
        }
        catch (InvalidOperationException)
        {
            // Try the independent TextPattern provider.
        }
        catch (ElementNotAvailableException)
        {
            return candidates;
        }

        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textObject) &&
                textObject is TextPattern textPattern)
            {
                candidates.Add(new ReadCandidate("TextPattern", textPattern.DocumentRange.GetText(-1)));
            }
        }
        catch (InvalidOperationException)
        {
            // Clipboard reading is the final fallback.
        }
        catch (ElementNotAvailableException)
        {
            // The next polling attempt reacquires the focused element.
        }

        return candidates;
    }

    private static bool TrySetWithAutomation(AutomationElement element, string text)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) &&
                valueObject is ValuePattern valuePattern &&
                !valuePattern.Current.IsReadOnly)
            {
                valuePattern.SetValue(text);
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }

        return false;
    }

    private static async Task WaitForModifiersReleasedAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (!IsKeyDown(VirtualKeyControl) &&
                !IsKeyDown(VirtualKeyShift) &&
                !IsKeyDown(VirtualKeyMenu))
            {
                return;
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        throw new CompanionException(
            ErrorCategory.ComposerAccess,
            "Release Ctrl, Shift, and Alt before the message is processed.");
    }

    private static bool HashEquals(string left, string right)
    {
        var leftHash = SHA256.HashData(Encoding.UTF8.GetBytes(left));
        var rightHash = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    private static bool IsKeyDown(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static void EnsureWindow(nint actual, nint expected)
    {
        if (actual != expected)
        {
            throw new CompanionException(
                ErrorCategory.UnsupportedWindow,
                "The supported client lost focus before the operation completed; nothing was sent.");
        }
    }

    private static void EnsureContext(
        FocusContext context,
        nint expectedWindow,
        ConversationClient expectedClient)
    {
        EnsureWindow(context.WindowHandle, expectedWindow);
        if (context.Client.Client != expectedClient)
        {
            throw new CompanionException(
                ErrorCategory.UnsupportedWindow,
                "The active client changed before the operation completed; nothing was sent.");
        }
    }

    private static void PressChord(byte modifier, byte key)
    {
        keybd_event(modifier, 0, 0, 0);
        keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, KeyEventKeyUp, 0);
        keybd_event(modifier, 0, KeyEventKeyUp, 0);
    }

    private static void PressKey(byte key)
    {
        keybd_event(key, 0, 0, 0);
        keybd_event(key, 0, KeyEventKeyUp, 0);
    }

    private sealed record FocusContext(
        nint WindowHandle,
        AutomationElement FocusedElement,
        ConversationClientProfile Client);
    private sealed record ReadCandidate(string Method, string Text);

    private sealed class ClipboardSnapshot
    {
        private readonly System.Windows.DataObject _data;

        private ClipboardSnapshot(System.Windows.DataObject data)
        {
            _data = data;
        }

        public static ClipboardSnapshot Capture()
        {
            var snapshot = new System.Windows.DataObject();
            var source = System.Windows.Clipboard.GetDataObject();
            if (source is not null)
            {
                foreach (var format in source.GetFormats(false))
                {
                    try
                    {
                        snapshot.SetData(format, source.GetData(format, false));
                    }
                    catch (ExternalException)
                    {
                        // Preserve every immediately accessible format.
                    }
                }
            }

            return new ClipboardSnapshot(snapshot);
        }

        public bool TryRestore()
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(_data, true);
                return true;
            }
            catch (ExternalException)
            {
                return false;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);
}
