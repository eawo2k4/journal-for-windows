using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WindowsJournal.Models;
using WindowsJournal.Services;

namespace WindowsJournal.Controls;

/// <summary>
/// The "what do you want to focus on" choices, shared by the welcome slideshow and Settings.
/// Each choice saves right away.
/// </summary>
public sealed partial class FocusPicker : UserControl
{
    private readonly List<(ToggleButton Button, EntryCategory Value)> _choices = [];

    /// <summary>Raised when the writer picks or unpicks something.</summary>
    public event EventHandler? Changed;

    public FocusPicker()
    {
        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 182, ItemHeight = 44 };
        foreach (var option in CategoryOption.All)
        {
            var button = new ToggleButton
            {
                Content = $"{option.Emoji}  {option.Label}",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, 0, 6, 6),
                IsChecked = SettingsService.Current.FocusAreas.Contains(option.Value),
            };
            AutomationProperties.SetName(button, option.Label);
            button.Click += (_, _) => Save();
            _choices.Add((button, option.Value));
            grid.Children.Add(button);
        }

        Content = grid;
    }

    private void Save()
    {
        SettingsService.Current.FocusAreas = [.. _choices.Where(c => c.Button.IsChecked == true).Select(c => c.Value)];
        SettingsService.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
