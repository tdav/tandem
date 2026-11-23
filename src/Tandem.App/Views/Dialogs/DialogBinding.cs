using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Data.Core;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.MarkupExtensions.CompiledBindings;

namespace Tandem.App.Views.Dialogs;

/// <summary>Creates a binding for a dialog built in C# using statically typed accessors.</summary>
internal static class DialogBinding<TModel> where TModel : INotifyPropertyChanged
{
    public static CompiledBindingExtension Property<TValue>(string name, Func<TModel, TValue> get,
        Action<TModel, TValue>? set = null)
    {
        var property = new ClrPropertyInfo(name, model => get((TModel)model),
            set is null ? null : (model, value) => set((TModel)model, (TValue)value!), typeof(TValue));
        var path = new CompiledBindingPathBuilder()
            .Property(property, PropertyInfoAccessorFactory.CreateInpcPropertyAccessor)
            .Build();
        return new CompiledBindingExtension(path)
        {
            Mode = set is null ? BindingMode.OneWay : BindingMode.TwoWay,
        };
    }
}
