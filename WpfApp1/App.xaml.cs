using System.Windows;
namespace WpfApp1;

public partial class App : Application
{
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is WpfApp1.MainWindow window) window.AllowSystemExit();
        base.OnSessionEnding(e);
    }
}
