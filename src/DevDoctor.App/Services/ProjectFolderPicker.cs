using Microsoft.Win32;

namespace DevDoctor.App.Services;

public interface IProjectFolderPicker { string? PickFolder(); }
public sealed class ProjectFolderPicker : IProjectFolderPicker
{
    public string? PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择要只读诊断的本地项目目录", Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
