using Microsoft.Win32;
using System.Windows;

namespace DevDoctor.App.Services;

public interface IUserFileDialogs
{
    string? Save(string title, string extension, string suggestedName);
    string? OpenBackup();
    bool Confirm(string title, string message);
}
public sealed class UserFileDialogs : IUserFileDialogs
{
    public string? Save(string title, string extension, string suggestedName)
    {
        var dialog = new SaveFileDialog { Title = title, DefaultExt = extension, AddExtension = true, Filter = $"{extension.ToUpperInvariant()} 文件|*{extension}", FileName = suggestedName, OverwritePrompt = true };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }
    public string? OpenBackup()
    {
        var dialog = new OpenFileDialog { Title = "选择 DevDoctor 本地备份", Filter = "DevDoctor 备份 ZIP|*.zip", CheckFileExists = true, Multiselect = false };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }
    public bool Confirm(string title, string message) => new ConfirmationWindow(title, message) { Owner = Application.Current.MainWindow }.ShowDialog() == true;
}
