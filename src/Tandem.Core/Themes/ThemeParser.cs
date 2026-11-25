using System.Globalization;
using System.Text.Json;

namespace Tandem.Core.Themes;

/// <summary>A theme file that cannot be read as a theme.</summary>
public sealed class ThemeFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads a theme in the design system's <c>tokens.json</c> format:
/// <code>
/// {
///   "name": "Stone",
///   "color":   { "tokens": [ { "name": "surface", "value": { "light": "#bbb8b0", "dark": "#2a2927" } },
///                            { "name": "on-accent", "value": "#ffffff" } ] },
///   "type":    { "families": { "ui": "\"Segoe UI\", sans-serif", "mono": "Consolas, monospace" },
///                "groups": [ { "styles": [ { "name": "item", "family": "ui", "fontSize": "14px",
///                                            "lineHeight": "22px", "fontWeight": 600 } ] } ] },
///   "spacing": { "tokens": [ { "name": "space-2", "value": "8px" } ] },
///   "size":    { "tokens": [ { "name": "row-height", "value": "22px" } ] },
///   "radius":  { "tokens": [ { "name": "radius-button", "value": "3px" } ] }
/// }
/// </code>
/// Every section is optional (missing tokens come from the built-in theme, <see cref="ThemeDefinition.WithFallback"/>);
/// a style's family may also be set on its group. Other members ("usage", "sample", "shadow"…) are ignored.
/// Numbers may be plain or end in "px".
/// </summary>
public static class ThemeParser
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <exception cref="ThemeFormatException">The text is not JSON, or a token is malformed.</exception>
    public static ThemeDefinition Parse(string json, string id, string? sourcePath = null)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, Options);
        }
        catch (JsonException e)
        {
            throw new ThemeFormatException($"Not valid JSON: {e.Message}", e);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ThemeFormatException("The theme is not a JSON object.");

            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String &&
                       !string.IsNullOrWhiteSpace(n.GetString())
                ? n.GetString()!.Trim()
                : id;

            var colors = new Dictionary<string, ThemeColor>(StringComparer.Ordinal);
            foreach (var token in Tokens(root, "color"))
                colors[TokenName(token, "color")] = ColorValue(token);

            var families = new Dictionary<string, string>(StringComparer.Ordinal);
            var styles = new Dictionary<string, ThemeTextStyle>(StringComparer.Ordinal);
            if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Object)
            {
                if (type.TryGetProperty("families", out var fams) && fams.ValueKind == JsonValueKind.Object)
                    foreach (var family in fams.EnumerateObject())
                        if (family.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(family.Value.GetString()))
                            families[family.Name] = family.Value.GetString()!.Trim();
                if (type.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
                    foreach (var group in groups.EnumerateArray())
                    {
                        var groupFamily = String(group, "family") ?? "ui";
                        if (!group.TryGetProperty("styles", out var list) || list.ValueKind != JsonValueKind.Array) continue;
                        foreach (var style in list.EnumerateArray())
                        {
                            var styleName = TokenName(style, "text style");
                            styles[styleName] = new ThemeTextStyle(
                                String(style, "family") ?? groupFamily,
                                Pixels(style, "fontSize", styleName),
                                style.TryGetProperty("lineHeight", out _) ? Pixels(style, "lineHeight", styleName) : 0,
                                style.TryGetProperty("fontWeight", out _) ? (int)Pixels(style, "fontWeight", styleName) : 400);
                        }
                    }
            }

            var sizes = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var section in new[] { "spacing", "size", "radius" })
                foreach (var token in Tokens(root, section))
                {
                    var tokenName = TokenName(token, section);
                    sizes[tokenName] = Pixels(token, "value", tokenName);
                }

            return new ThemeDefinition(id, name, sourcePath, colors, families, styles, sizes);
        }
    }

    /// <summary>"#rgb", "#rrggbb" or "#aarrggbb" made "#rrggbb"/"#aarrggbb" (lower case); null when it is none of
    /// these.</summary>
    public static string? NormalizeColor(string? text)
    {
        if (text is null) return null;
        var value = text.Trim();
        if (value.Length < 2 || value[0] != '#') return null;
        var hex = value[1..];
        if (!hex.All(Uri.IsHexDigit)) return null;
        return hex.Length switch
        {
            3 => "#" + string.Concat(hex.Select(c => new string(c, 2))).ToLowerInvariant(),
            6 or 8 => "#" + hex.ToLowerInvariant(),
            _ => null,
        };
    }

    private static IEnumerable<JsonElement> Tokens(JsonElement root, string section)
    {
        if (!root.TryGetProperty(section, out var element) || element.ValueKind != JsonValueKind.Object) yield break;
        if (!element.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Array) yield break;
        foreach (var token in tokens.EnumerateArray())
            if (token.ValueKind == JsonValueKind.Object) yield return token;
    }

    private static string TokenName(JsonElement token, string what) =>
        String(token, "name") is { Length: > 0 } name
            ? name
            : throw new ThemeFormatException($"A {what} token has no name.");

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static ThemeColor ColorValue(JsonElement token)
    {
        var name = TokenName(token, "color");
        if (!token.TryGetProperty("value", out var value))
            throw new ThemeFormatException($"The color token \"{name}\" has no value.");
        if (value.ValueKind == JsonValueKind.String)
        {
            var both = Color(value.GetString(), name);
            return new ThemeColor(both, both);
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var light = String(value, "light");
            var dark = String(value, "dark");
            if (light is null && dark is null)
                throw new ThemeFormatException($"The color token \"{name}\" has neither a light nor a dark value.");
            var l = Color(light ?? dark, name);
            var d = Color(dark ?? light, name);
            return new ThemeColor(l, d);
        }
        throw new ThemeFormatException($"The color token \"{name}\" has a value that is not a colour.");
    }

    private static string Color(string? text, string name) =>
        NormalizeColor(text) ?? throw new ThemeFormatException($"The color token \"{name}\" is not #rrggbb: \"{text}\".");

    private static double Pixels(JsonElement element, string property, string name)
    {
        if (!element.TryGetProperty(property, out var value))
            throw new ThemeFormatException($"\"{name}\" has no {property}.");
        if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!.Trim();
            if (text.EndsWith("px", StringComparison.OrdinalIgnoreCase)) text = text[..^2].Trim();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
        }
        throw new ThemeFormatException($"The {property} of \"{name}\" is not a number of pixels.");
    }
}
