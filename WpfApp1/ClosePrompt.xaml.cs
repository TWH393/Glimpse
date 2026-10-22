using System.Windows;

namespace WpfApp1;

internal enum CloseDecision { Cancel, Exit, Minimize }

public partial class ClosePrompt : Window
{
    internal CloseDecision Decision { get; private set; } = CloseDecision.Cancel;
    public ClosePrompt()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Appearance.UpdateTitleBar(this,
            (Owner as MainWindow)?.IsDarkMode == true);
    }
    private void Exit_Click(object sender, RoutedEventArgs e) { Decision = CloseDecision.Exit; DialogResult = true; }
    private void Minimize_Click(object sender, RoutedEventArgs e) { Decision = CloseDecision.Minimize; DialogResult = true; }
}
