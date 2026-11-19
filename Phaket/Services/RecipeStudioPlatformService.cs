using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace Phaket.Services;

public sealed class RecipeStudioPlatformService
{
    public async Task<bool> CopyTextAsync(string text)
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window?.Clipboard is not { } clipboard) return false;
        await clipboard.SetTextAsync(text);
        return true;
    }

    public async Task<bool> ExportOmrlAsync(string fileName, string contents)
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window?.StorageProvider is not { } storage) return false;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LocalizationService.Instance["Studio.ExportOmrl"],
            SuggestedFileName = fileName,
            DefaultExtension = "omrl",
            FileTypeChoices = [new FilePickerFileType("Open Mod Recipe Language") { Patterns = ["*.omrl", "*.json"] }],
        });
        if (file is null) return false;
        await using var writer = new StreamWriter(await file.OpenWriteAsync(), new UTF8Encoding(false));
        await writer.WriteAsync(contents.AsMemory());
        return true;
    }
}
