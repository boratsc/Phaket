using System.Diagnostics;
using Phaket.Core.Omrl;

namespace Phaket.Core.Platforms;

// Real DepotDownloader process with interactive stdin: the library install and the mod
// provision both need the tool's password / Steam Guard prompts answered line by line.
public sealed class DepotProcessRunner : IDepotProcessRunner
{
    public async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, IDictionary<string, string?> environment, Func<bool, string, Func<string, Task>, Task> output, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start) ?? throw new PhaketException("DEPOTDOWNLOADER_INSTALL_FAILED");
        using var gate = new SemaphoreSlim(1);
        async Task Read(StreamReader stream, bool stderr)
        {
            try
            {
                var buffer = new char[1024];
                while (await stream.ReadAsync(buffer.AsMemory(), ct) is var n && n > 0)
                {
                    await gate.WaitAsync(ct);
                    try { await output(stderr, new string(buffer, 0, n), s => process.StandardInput.WriteLineAsync(s)); }
                    finally { gate.Release(); }
                }
            }
            catch { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
        }
        var reads = Task.WhenAll(Read(process.StandardOutput, false), Read(process.StandardError, true));
        try
        {
            var wait = process.WaitForExitAsync(ct);
            var first = await Task.WhenAny(reads, wait);
            if (first == reads) await reads;
            await wait; await reads;
            return process.ExitCode;
        }
        catch { try { process.Kill(true); await process.WaitForExitAsync(); } catch (InvalidOperationException) { } throw; }
    }
}