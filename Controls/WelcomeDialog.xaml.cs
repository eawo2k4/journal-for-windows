using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Security.Credentials.UI;
using WindowsJournal.Services;

namespace WindowsJournal.Controls;

/// <summary>
/// First-run welcome slideshow. Illustrations are drawn in XAML with the app's own theme
/// resources, so they match light/dark mode and the user's accent color without screenshots.
/// The footer is part of the content (not the dialog's built-in buttons) so Back can sit on the left.
/// </summary>
public sealed partial class WelcomeDialog : ContentDialog
{
    private bool _settingToggle;

    public WelcomeDialog()
    {
        InitializeComponent();
        Pips.NumberOfPages = Slides.Items.Count;
        SetToggleSilently(SettingsService.Current.RequireWindowsHello);
        Opened += (_, _) => NextButton.Focus(FocusState.Programmatic);
        Slides.Loaded += (_, _) => HideFlipViewArrows(Slides);
        UpdateButtons();
    }

    private int LastIndex => Slides.Items.Count - 1;

    // The footer already has Back and Next, and the FlipView's own hover arrows would cover the focus choices.
    private static void HideFlipViewArrows(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button { Name: "PreviousButtonHorizontal" or "NextButtonHorizontal" or "PreviousButtonVertical" or "NextButtonVertical" } arrow)
            {
                // The FlipView sets their visibility itself on hover, so size them away instead.
                arrow.Width = 0;
                arrow.Height = 0;
                AutomationProperties.SetAccessibilityView(arrow, AccessibilityView.Raw);
            }
            else
            {
                HideFlipViewArrows(child);
            }
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (Slides.SelectedIndex < LastIndex) Slides.SelectedIndex++;
        else Hide();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Slides.SelectedIndex > 0) Slides.SelectedIndex--;
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => Hide();

    // Optional, like Apple's and Google's journal apps: offered here, never forced.
    private async void HelloToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_settingToggle) return;
        HelloMessage.Visibility = Visibility.Collapsed;

        if (HelloToggle.IsOn)
        {
            if (await UserConsentVerifier.CheckAvailabilityAsync() != UserConsentVerifierAvailability.Available)
            {
                ShowHelloMessage("Windows Hello isn't set up on this PC. You can set it up in Windows Settings > Accounts > Sign-in options.");
                SetToggleSilently(false);
                return;
            }

            var result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(App.MainWindow.Hwnd, "Turn on journal lock");
            if (result != UserConsentVerificationResult.Verified)
            {
                SetToggleSilently(false);
                return;
            }

            ShowHelloMessage("Journal will ask for Windows Hello each time it opens.");
        }

        SettingsService.Current.RequireWindowsHello = HelloToggle.IsOn;
        SettingsService.Save();
    }

    private void ShowHelloMessage(string text)
    {
        HelloMessage.Text = text;
        HelloMessage.Visibility = Visibility.Visible;
    }

    private void SetToggleSilently(bool value)
    {
        _settingToggle = true;
        HelloToggle.IsOn = value;
        _settingToggle = false;
    }

    private void Slides_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void Pips_SelectedIndexChanged(PipsPager sender, PipsPagerSelectedIndexChangedEventArgs args)
    {
        if (Slides.SelectedIndex != sender.SelectedPageIndex) Slides.SelectedIndex = sender.SelectedPageIndex;
    }

    private void UpdateButtons()
    {
        if (NextButton is null) return; // SelectionChanged can fire while XAML is still loading

        var index = Math.Max(0, Slides.SelectedIndex);
        Pips.SelectedPageIndex = index;
        BackButton.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
        SkipButton.Visibility = index == LastIndex ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = index == LastIndex ? "Get started" : "Next";
    }
}
