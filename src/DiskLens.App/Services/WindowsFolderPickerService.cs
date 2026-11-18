using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace DiskLens.App.Services;

public sealed class WindowsFolderPickerService : IFolderPickerService
{
    public string? PickFolder(string initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = L.T("选择要扫描的位置"),
            Multiselect = false,
        };

        var startingDirectory = Directory.Exists(initialDirectory)
            ? initialDirectory
            : File.Exists(initialDirectory)
                ? Path.GetDirectoryName(initialDirectory)
                : null;
        if (!string.IsNullOrWhiteSpace(startingDirectory) && Directory.Exists(startingDirectory))
        {
            dialog.InitialDirectory = startingDirectory;
        }

        var owner = Application.Current?.MainWindow;
        var accepted = owner is { IsVisible: true }
            ? dialog.ShowDialog(owner)
            : dialog.ShowDialog();

        return accepted == true && Directory.Exists(dialog.FolderName)
            ? dialog.FolderName
            : null;
    }
}
