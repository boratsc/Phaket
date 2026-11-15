using System.Buffers.Binary;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Phaket.Core.Omrl;
using Phaket.Services;
using Phaket.ViewModels;
using Phaket.Views;

namespace Phaket.Tests;

public sealed class LibraryArchitectureTests
{
    [AvaloniaFact]
    public void LibraryRendersInstallationArchitecture()
    {
        var root = Path.Combine(Path.GetTempPath(), "phaket-library-render-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var vm = new LibraryViewModel(_ => { }, TestAccounts.Create(), null);
            var game = vm.Games.First(g => g.Definition.Slug == "among-us");
            game.EpicInstallPath = root;
            var window = new Window { Width = 1280, Height = 800, Content = new LibraryView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Directory.CreateDirectory("/tmp/opencode/phaket-render");
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save("/tmp/opencode/phaket-render/library-arch.png", PngBitmapEncoderOptions.Default);
            window.Close();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ArchitectureUsesSelectedInstallationAndNotifiesWhenItChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "phaket-library-arch-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var bytes = new byte[512];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0), 0x5a4d);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x3c), 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x80), 0x4550);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x84), 0x8664);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x94), 240);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x98), 0x20b);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x98 + 108), 16);
            File.WriteAllBytes(Path.Combine(root, "game.exe"), bytes);
            var item = new GameCatalogItem(new GameDefinition("test", [], MainExecutable: "game.exe"), "", "");
            Assert.Equal(LocalizationService.Instance["Game.ArchitectureUnknown"], item.InstallationArchitecture);
            var changed = false;
            item.PropertyChanged += (_, e) => changed |= e.PropertyName == nameof(item.InstallationArchitecture);
            item.EpicInstallPath = root;
            Assert.True(changed);
            Assert.Equal("x64", item.InstallationArchitecture);
            File.WriteAllText(Path.Combine(root, "game.exe"), "invalid");
            Assert.Equal(LocalizationService.Instance["Game.ArchitectureUnknown"], item.InstallationArchitecture);
        }
        finally { Directory.Delete(root, true); }
    }
}
