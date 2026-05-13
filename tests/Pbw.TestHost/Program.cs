using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Pbw.TestHost;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var outputPath = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "pbw-testhost.txt");
        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };

        var input = new TextBox
        {
            Name = "InputBox",
            Text = "initial",
            Width = 260,
            Height = 28,
            Margin = new Thickness(12)
        };
        AutomationProperties.SetAutomationId(input, "InputBox");
        AutomationProperties.SetName(input, "InputBox");

        var button = new Button
        {
            Name = "WriteButton",
            Content = "Write",
            Width = 120,
            Height = 32,
            Margin = new Thickness(12)
        };
        AutomationProperties.SetAutomationId(button, "WriteButton");
        AutomationProperties.SetName(button, "WriteButton");
        button.Click += (_, _) => File.WriteAllText(outputPath, input.Text);

        var panel = new StackPanel();
        panel.Children.Add(input);
        panel.Children.Add(button);

        var window = new Window
        {
            Title = "pbw-integration-" + Environment.ProcessId,
            Width = 420,
            Height = 180,
            Content = panel,
            Topmost = false
        };
        AutomationProperties.SetAutomationId(window, "MainWindow");
        app.MainWindow = window;
        app.Run(window);
        return 0;
    }
}
