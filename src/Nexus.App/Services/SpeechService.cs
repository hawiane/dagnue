using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace Nexus.App.Services;

public sealed class SpeechService : IAsyncDisposable
{
    private SpeechRecognizer? _recognizer;

    public async Task<string?> ListenOnceAsync(CancellationToken cancellationToken = default)
    {
        _recognizer?.Dispose();
        _recognizer = new SpeechRecognizer(new Language("en-US"));
        _recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "nexus-dictation"));
        var compilation = await _recognizer.CompileConstraintsAsync();
        if (compilation.Status != SpeechRecognitionResultStatus.Success)
            throw new InvalidOperationException("Windows speech recognition is not available.");
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _recognizer.RecognizeAsync();
        return result.Status == SpeechRecognitionResultStatus.Success ? result.Text : null;
    }

    public ValueTask DisposeAsync()
    {
        _recognizer?.Dispose();
        return ValueTask.CompletedTask;
    }
}
