using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Phaket.Core.Domain;
using Phaket.Core.Execution;
using Phaket.Core.Execution.SaveBackup;
using Phaket.Services;
using Phaket.ViewModels;

namespace Phaket.Tests;

// PAK-141: an async test's continuations must share the UI thread the rest of the suite
// leaves in place. Without this the test method's continuation can land on a pool thread
// while the view model's await resumes on the UI thread — and the two then disagree about
// when Load finished. Every wait here drains the UI queue while it waits.
internal static class UiTest
{
    // waitFor: a task the view model awaits on something else than the UI thread (the file
    // conflict dialog waits for the user's answer, which arrives through a command).
    public static T Run<T>(Func<Task<T>> work, int timeoutSeconds = 20)
    {
        var task = work();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(task.IsCompleted, "the UI work did not finish");
        Dispatcher.UIThread.RunJobs();
        return task.GetAwaiter().GetResult();
    }

    public static void Run(Func<Task> work, int timeoutSeconds = 20) =>
        Run(async () => { await work(); return true; }, timeoutSeconds);

    // Waits for a settled condition (a dialog opened, a continuation resumed) as the UI queue
    // keeps running; an unrelated await never blocks the test thread.
    public static void Until(Func<bool> condition, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(condition(), "the awaited condition did not hold");
    }
}