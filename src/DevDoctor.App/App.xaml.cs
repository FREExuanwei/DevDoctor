using System.Windows;
using DevDoctor.App.Services;
using DevDoctor.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using DevDoctor.Core;
using DevDoctor.Infrastructure;

namespace DevDoctor.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _services = ApplicationServices.Create();
            _ = _services.GetRequiredService<WorkspaceLease>();
            var data = _services.GetRequiredService<AppDataWorkspace>(); string? notice = null;
            try
            {
                notice = await _services.GetRequiredService<BackupService>().RecoverInterruptedAsync();
                await data.InitializeAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevDoctor", "settings.json"));
                await _services.GetRequiredService<IHistoryStore>().EnsureAsync();
            }
            catch (Exception ex) when (ex is UserDataException or IOException or UnauthorizedAccessException) { notice = "本地数据需要处理，诊断仍可使用：" + HistoryViewModel.Explain(ex); }
            var vm = _services.GetRequiredService<MainViewModel>(); await vm.InitializeAsync();
            if (notice is not null) vm.SetStartupNotice(notice);
            MainWindow = _services.GetRequiredService<MainWindow>(); MainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex is UserDataException data ? data.Message : $"DevDoctor 启动未完成（{UiText.ExceptionName(ex)}）。请确认应用文件完整，并参阅 README。", "DevDoctor", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { _services?.Dispose(); base.OnExit(e); }
}
