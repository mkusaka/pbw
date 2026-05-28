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

        var toggle = new CheckBox
        {
            Name = "ToggleBox",
            Content = "Toggle",
            IsChecked = false,
            Width = 260,
            Height = 28,
            Margin = new Thickness(12)
        };
        AutomationProperties.SetAutomationId(toggle, "ToggleBox");
        AutomationProperties.SetName(toggle, "ToggleBox");
        toggle.Checked += (_, _) => File.WriteAllText(outputPath, "toggle:true");
        toggle.Unchecked += (_, _) => File.WriteAllText(outputPath, "toggle:false");

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

        var slider = new Slider
        {
            Name = "RangeSlider",
            Minimum = 0,
            Maximum = 100,
            Value = 25,
            Width = 260,
            Height = 32,
            Margin = new Thickness(12),
            TickFrequency = 5
        };
        AutomationProperties.SetAutomationId(slider, "RangeSlider");
        AutomationProperties.SetName(slider, "RangeSlider");
        slider.ValueChanged += (_, _) => File.WriteAllText(outputPath, "range:" + slider.Value.ToString("0"));

        var ocrText = new TextBlock
        {
            Text = "PBW OCR 12345",
            FontSize = 32,
            Margin = new Thickness(12),
            Width = 360,
            Height = 48
        };
        AutomationProperties.SetAutomationId(ocrText, "OcrText");
        AutomationProperties.SetName(ocrText, "PBW OCR 12345");

        var panel = new StackPanel();
        panel.Children.Add(ocrText);
        panel.Children.Add(input);
        panel.Children.Add(toggle);
        panel.Children.Add(slider);
        panel.Children.Add(button);

        var window = new Window
        {
            Title = "pbw-integration-" + Environment.ProcessId,
            Width = 420,
            Height = 340,
            Content = panel,
            Topmost = false
        };
        AutomationProperties.SetAutomationId(window, "MainWindow");
        app.MainWindow = window;
        app.Run(window);
        return 0;
    }
}
