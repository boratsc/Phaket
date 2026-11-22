using System.Text;

namespace Phaket.Core.Platforms;

// DepotDownloader asks for Steam credentials on its standard streams. The library
// install and the mod provision run the same tool, so both read its prompts through
// this one parser instead of keeping two copies that could drift apart.
public sealed class DepotPromptReader
{
    public const string MobileConfirmation = "STEAM GUARD! Use the Steam Mobile App to confirm your sign in...";
    private const int MaxBufferedLine = 8192;
    private const int MaxRetainedLines = 100;
    private readonly StringBuilder[] buffers = [new(), new()];
    private readonly bool[] discard = [false, false];
    private readonly Queue<string> lines = [];

    // Completed output lines (bounded), used for the readable error tail.
    public IReadOnlyCollection<string> Lines => lines;

    // Feeds one character of the tool output. Returns a prompt when the current line is
    // one the tool is waiting an answer for; the buffer is cleared so the answer is not
    // mistaken for more prompt text. A completed line is returned for progress reporting.
    public DepotPrompt? Append(bool stderr, char ch, out string? completedLine)
    {
        completedLine = null;
        var index = stderr ? 1 : 0;
        if (discard[index])
        {
            if (ch == '\n') discard[index] = false;
            return null;
        }
        var buffer = buffers[index];
        buffer.Append(ch);
        var current = buffer.ToString();
        var prompt = !stderr && current.StartsWith("Enter account password for ", StringComparison.Ordinal) && current.EndsWith(": ", StringComparison.Ordinal)
            ? DepotPrompt.Password
            : stderr && current.StartsWith("STEAM GUARD! Please enter ", StringComparison.Ordinal) && current.EndsWith(": ", StringComparison.Ordinal)
                ? DepotPrompt.SteamGuard
                : (DepotPrompt?)null;
        if (prompt is not null)
        {
            buffer.Clear();
            return prompt;
        }
        if (ch == '\n')
        {
            completedLine = current;
            lines.Enqueue(current);
            while (lines.Count > MaxRetainedLines) lines.Dequeue();
            buffer.Clear();
        }
        else if (buffer.Length >= MaxBufferedLine)
        {
            buffer.Clear();
            discard[index] = true;
        }
        return null;
    }

    // A prompt is answered without a trailing newline, so the last partial line is only
    // useful for the error tail.
    public void Flush()
    {
        foreach (var buffer in buffers)
            if (buffer.Length > 0)
            {
                lines.Enqueue(buffer.ToString());
                buffer.Clear();
            }
    }
}