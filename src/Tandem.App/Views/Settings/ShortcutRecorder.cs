using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Tandem.App.Commands;
using Tandem.App.Input;
using Tandem.App.Resources;

namespace Tandem.App.Views.Settings;

/// <summary>
/// A field that records shortcuts. Recording starts on a click, or on Return or Space while it has the focus (focus
/// alone, as Tab gives it, does not start it); while it records, each key pressed, with its modifiers, becomes a <see cref="KeyChord"/> (<see cref="KeyRouting.ChordFor"/>, so a letter is its US-layout key on
/// any layout) and is told through <see cref="ChordRecorded"/>; no other control sees the key, Tab, Enter and Space
/// included. A modifier pressed alone records nothing. Esc, or the focus leaving, stops recording and records
/// nothing; Return starts it again.
/// </summary>
public sealed class ShortcutRecorder : Border
{
    private readonly TextBlock _text = new()
    {
        TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private bool _isRecording;

    public ShortcutRecorder()
    {
        Focusable = true;
        MinHeight = 32;
        Padding = new Thickness(8, 4);
        CornerRadius = new CornerRadius(4);
        BorderThickness = new Thickness(1);
        Bind(BorderBrushProperty, this.GetResourceObservable("Tandem.DividerBrush"));
        Child = _text;
        ShowState();
        // Before keyboard navigation and the window's default and cancel buttons see the key.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>A key was recorded.</summary>
    public event EventHandler<KeyChord>? ChordRecorded;

    /// <summary>True while key presses are recorded.</summary>
    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (_isRecording == value) return;
            _isRecording = value;
            ShowState();
        }
    }

    private void ShowState()
    {
        _text.Text = Loc.Get(_isRecording ? "Settings_Recorder_Recording" : "Settings_Recorder_Idle");
        BorderThickness = new Thickness(_isRecording ? 2 : 1);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        IsRecording = false;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        IsRecording = true;
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsRecording)
        {
            if (e.Key is Key.Enter or Key.Space && e.KeyModifiers == KeyModifiers.None)
            {
                IsRecording = true;
                e.Handled = true;
            }
            return;
        }
        e.Handled = true;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            IsRecording = false;
            return;
        }
        if (IsModifierKey(e.Key)) return;
        ChordRecorded?.Invoke(this, KeyRouting.ChordFor(e.Key, e.PhysicalKey, e.KeyModifiers));
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt
        or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None;
}
