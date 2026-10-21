using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ArcadeOS.Services;

namespace ArcadeOS;
public partial class MainWindow
{
    private readonly ControllerInputService controllerInput;
    private readonly DispatcherTimer controllerTimer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private bool mappingButtons;
    private int mappingStep;
    private string? mappingKind;
    private ControllerBindings pendingBindings = new();
    private readonly int[] capturedButtons = new int[4];
    private static readonly CabinetInput[] MappingActions = [CabinetInput.Select, CabinetInput.Back, CabinetInput.Start, CabinetInput.Menu];
    private void InitializeControllers()
    {
        Loaded += (_, _) => controllerTimer.Start(); Closed += (_, _) => controllerTimer.Stop();
        controllerTimer.Tick += (_, _) =>
        {
            bool active = !windowClosed && !launching && IsActive && IsVisible;
            var input = controllerInput.Poll(active && !mappingButtons, Environment.TickCount64);
            if (HandleScreensaverController(active)) return;
            if (ControllerOverlay.Visibility == Visibility.Visible)
            {
                ControllerDevices.Text = controllerInput.Devices.Count == 0 ? "No joystick detected. Connect the USB controller; detection updates automatically. Keyboard controls still work." :
                    "Detected: " + string.Join(" · ", controllerInput.Devices.Select(d => d.Name).Distinct());
                if (mappingButtons && active) { if (controllerInput.CaptureButton() is { } button) CaptureControllerButton(button.Kind, button.Button); return; }
            }
            if (!active || input == CabinetInput.None) return;
            // One action per poll prevents a Select + Start combination opening and launching a game together.
            foreach (var action in new[] { CabinetInput.Back, CabinetInput.Menu, CabinetInput.Select, CabinetInput.Start, CabinetInput.Up, CabinetInput.Down, CabinetInput.Left, CabinetInput.Right })
                if ((input & action) != 0) { DispatchControllerInput(action); break; }
        };
    }
    private void DispatchControllerInput(CabinetInput action)
    {
        var key = action switch
        {
            CabinetInput.Up => Key.Up, CabinetInput.Down => Key.Down, CabinetInput.Left => Key.Left, CabinetInput.Right => Key.Right,
            CabinetInput.Select => Key.Enter, CabinetInput.Back => Key.Escape, CabinetInput.Start => Key.D1, CabinetInput.Menu => Key.Tab, _ => Key.None
        };
        if (key == Key.None || launching || !IsActive) return;
        var source = PresentationSource.FromVisual(this); if (source is null) return;
        OnKeyDown(this, new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }
    private void OpenControllerSetup(object sender, RoutedEventArgs e)
    {
        StopGamePreview(); mappingButtons = false; ControllerMessage.Text = "Move the stick or D-pad to navigate. Default buttons: 1 Select, 2 Back, 8 Start, 7 Menu. Xbox defaults: A Select, B Back, Start Launch, Back/View Menu.\n\nChoose Map buttons if your encoder uses a different layout. Mapping changes Arkade only.";
        ControllerMapButton.Content = "MAP BUTTONS"; ControllerMapButton.IsEnabled = true;
        UtilityOverlay.IsEnabled = false; ControllerOverlay.Visibility = Visibility.Visible; ControllerOverlay.UpdateLayout(); ControllerMapButton.Focus();
    }
    private void BeginControllerMapping(object sender, RoutedEventArgs e)
    {
        mappingButtons = true; mappingStep = 0; mappingKind = null; Array.Clear(capturedButtons);
        controllerInput.BeginCapture(); ControllerMapButton.IsEnabled = false;
        ControllerMessage.Text = "Release the buttons, then press the button you want for SELECT.\n\nPress each requested button once. Esc on the keyboard cancels mapping.";
    }
    private void CaptureControllerButton(string kind, int button)
    {
        if (mappingKind != null && kind != mappingKind) return;
        if (capturedButtons.Take(mappingStep).Contains(button)) { ControllerMessage.Text = "That button is already assigned. Press a different button for " + MappingActions[mappingStep].ToString().ToUpperInvariant() + "."; return; }
        mappingKind = kind; capturedButtons[mappingStep] = button; pendingBindings = pendingBindings.Set(MappingActions[mappingStep], button); mappingStep++;
        if (mappingStep < MappingActions.Length) { ControllerMessage.Text = "Now press the button you want for " + MappingActions[mappingStep].ToString().ToUpperInvariant() + "."; return; }
        try { controllerInput.SaveBindings(kind, pendingBindings); ControllerMessage.Text = $"Buttons saved: Select {pendingBindings.Select}, Back {pendingBindings.Back}, Start {pendingBindings.Start}, Menu {pendingBindings.Menu}.\n\nYou can now navigate Arkade with the joystick. Keyboard controls remain available."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ControllerMessage.Text = "Could not save the mapping. " + ex.Message; }
        mappingButtons = false; ControllerMapButton.IsEnabled = true; ControllerBackButton.Focus();
    }
    private void CloseControllerSetup(object sender, RoutedEventArgs e)
    {
        mappingButtons = false; ControllerOverlay.Visibility = Visibility.Collapsed; UtilityOverlay.IsEnabled = true; ControllerSetupButton.Focus();
    }
    private void HandleControllerSetupKey(KeyEventArgs e)
    {
        if (!e.IsRepeat && e.Key == Key.Escape) CloseControllerSetup(this, e);
        else if (!mappingButtons)
        {
            if (!e.IsRepeat && e.Key is Key.Enter or Key.Space or Key.D1 or Key.NumPad1) ClickFocused(ControllerMapButton, ControllerBackButton);
            else if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Tab) MoveButtonFocus(e.Key is Key.Up or Key.Left ? -1 : 1, ControllerMapButton, ControllerBackButton);
        }
        e.Handled = true;
    }
}
