using Avalonia.Controls;

namespace Tandem.App.Views;

/// <summary>A header or content that shows text exactly as it is. A plain string header or button content goes
/// through AccessText, which takes "_x" as an access key and hides the underscore ("__" shows one); paths, file,
/// folder, volume and application names must never be read that way. In markup, the same is done by giving the
/// control a <see cref="TextBlock"/> as its content.</summary>
public static class LiteralLabel
{
    public static TextBlock For(string text) => new() { Text = text };
}
