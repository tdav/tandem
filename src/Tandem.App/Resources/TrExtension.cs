namespace Tandem.App.Resources;

/// <summary>Markup access to <see cref="Loc"/>: <c>{loc:Tr Key=Some_Key}</c> with
/// <c>xmlns:loc="using:Tandem.App.Resources"</c>. The text is read once, when the markup loads.</summary>
public sealed class TrExtension
{
    public string Key { get; set; } = "";

    public string ProvideValue(IServiceProvider? serviceProvider) => Loc.Get(Key);
}
