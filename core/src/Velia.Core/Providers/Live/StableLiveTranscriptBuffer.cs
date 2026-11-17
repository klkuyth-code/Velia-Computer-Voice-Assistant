using System.Text;

namespace Velia.Core.Providers.Live;

internal sealed class StableLiveTranscriptBuffer(int maximumCharacters)
{
    private readonly object gate = new();
    private readonly StringBuilder text = new();
    private long revision;
    private bool finished;
    private bool overflowed;

    public bool HasText
    {
        get
        {
            lock (gate) return !overflowed && text.Length > 0;
        }
    }

    public string CurrentText
    {
        get
        {
            lock (gate) return overflowed ? "" : text.ToString();
        }
    }

    public void Append(string fragment, bool isFinished)
    {
        if (string.IsNullOrWhiteSpace(fragment)) return;
        lock (gate)
        {
            if (overflowed) return;
            if (text.Length + fragment.Length > maximumCharacters)
            {
                text.Clear();
                finished = false;
                overflowed = true;
                revision++;
                return;
            }
            text.Append(fragment);
            finished = isFinished;
            revision++;
        }
    }

    public async Task<string?> TakeWhenStableAsync(
        TimeSpan quietPeriod,
        TimeSpan maximumWait,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + maximumWait;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TranscriptSnapshot snapshot;
            lock (gate)
            {
                if (overflowed) return null;
                snapshot = new TranscriptSnapshot(revision, text.Length > 0, finished);
            }

            if (snapshot.HasText && snapshot.Finished && TryTake(snapshot.Revision, out var completed))
                return completed;

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return null;
            await Task.Delay(remaining < quietPeriod ? remaining : quietPeriod, cancellationToken)
                .ConfigureAwait(false);

            if (snapshot.HasText && TryTake(snapshot.Revision, out var stable)) return stable;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            text.Clear();
            finished = false;
            overflowed = false;
            revision++;
        }
    }

    private bool TryTake(long expectedRevision, out string? value)
    {
        lock (gate)
        {
            if (overflowed || revision != expectedRevision || text.Length == 0)
            {
                value = null;
                return false;
            }
            value = text.ToString().Trim();
            text.Clear();
            finished = false;
            revision++;
            if (value.Length is > 0 && value.Length <= maximumCharacters) return true;
            value = null;
            return false;
        }
    }

    private sealed record TranscriptSnapshot(long Revision, bool HasText, bool Finished);
}
