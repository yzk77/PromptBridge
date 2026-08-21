using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ChineseToChatGPT.Core;

public sealed class TranslationWorkflow(
    IComposerAdapter composer,
    ITranslationService translator,
    IDiagnosticLogger logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PreviewState? _preview;

    public async Task<WorkflowResult> ExecuteAsync(SendMode sendMode, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new WorkflowResult(WorkflowOutcome.Busy);
        }

        var stopwatch = Stopwatch.StartNew();
        var inputLength = 0;
        try
        {
            var snapshot = await composer.CaptureAsync(cancellationToken).ConfigureAwait(false);
            inputLength = snapshot.Text.Length;
            if (string.IsNullOrWhiteSpace(snapshot.Text))
            {
                _preview = null;
                return new WorkflowResult(WorkflowOutcome.Empty);
            }

            if (sendMode == SendMode.Preview &&
                _preview is not null &&
                _preview.WindowHandle == snapshot.WindowHandle &&
                CryptographicOperations.FixedTimeEquals(Hash(snapshot.Text), _preview.TextHash))
            {
                await composer.SendAsync(snapshot.WindowHandle, cancellationToken).ConfigureAwait(false);
                _preview = null;
                logger.Event("sent_preview", inputLength, stopwatch.ElapsedMilliseconds);
                return new WorkflowResult(WorkflowOutcome.Sent, inputLength);
            }

            _preview = null;
            var translated = MessageFormatter.ContainsChinese(snapshot.Text)
                ? await translator.TranslateToEnglishAsync(snapshot.Text, cancellationToken).ConfigureAwait(false)
                : snapshot.Text;
            var outgoing = MessageFormatter.FormatForClient(translated, snapshot.Client);

            var replacementStarted = false;
            try
            {
                replacementStarted = true;
                var write = await composer.ReplaceAndVerifyAsync(
                    snapshot,
                    outgoing,
                    cancellationToken).ConfigureAwait(false);
                if (!write.Success)
                {
                    throw new CompanionException(
                        ErrorCategory.ReplacementVerification,
                        "The translated draft could not be verified.");
                }

                if (sendMode == SendMode.Immediate)
                {
                    await composer.SendAsync(snapshot.WindowHandle, cancellationToken).ConfigureAwait(false);
                    logger.Event("translated_and_sent", inputLength, stopwatch.ElapsedMilliseconds);
                    return new WorkflowResult(WorkflowOutcome.Sent, inputLength);
                }

                _preview = new PreviewState(snapshot.WindowHandle, Hash(outgoing));
                logger.Event("translated_preview", inputLength, stopwatch.ElapsedMilliseconds);
                return new WorkflowResult(WorkflowOutcome.Previewed, inputLength);
            }
            catch (Exception replacementException)
            {
                if (replacementStarted)
                {
                    try
                    {
                        var recovery = await composer.RestoreAndVerifyAsync(
                            snapshot,
                            CancellationToken.None).ConfigureAwait(false);
                        if (!recovery.Success)
                        {
                            throw new CompanionException(
                                ErrorCategory.ComposerRecovery,
                                "The translated draft was not verified and the original draft could not be confirmed as restored.",
                                replacementException);
                        }
                    }
                    catch (CompanionException recoveryException)
                        when (recoveryException.Category == ErrorCategory.ComposerRecovery)
                    {
                        throw;
                    }
                    catch (Exception recoveryException)
                    {
                        throw new CompanionException(
                            ErrorCategory.ComposerRecovery,
                            "The translated draft was not verified and restoring the original draft failed.",
                            recoveryException);
                    }
                }

                throw;
            }
        }
        catch (CompanionException exception)
        {
            logger.Error(exception.Category, inputLength);
            throw;
        }
        catch (Exception exception)
        {
            logger.Error(ErrorCategory.Unknown, inputLength);
            throw new CompanionException(ErrorCategory.Unknown, "An unexpected error occurred.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static byte[] Hash(string value)
    {
        var normalized = ComposerTextNormalizer.NormalizeComposerText(value);
        return SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
    }

    private sealed record PreviewState(nint WindowHandle, byte[] TextHash);
}
