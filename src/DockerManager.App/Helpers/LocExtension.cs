using System.Windows.Data;
using System.Windows.Markup;
using DockerManager.App.Services;

namespace DockerManager.App.Helpers;

[MarkupExtensionReturnType(typeof(string))]
public class LocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;
    public string? StringFormat { get; set; }

    public LocExtension() { }

    public LocExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrWhiteSpace(Key)) return string.Empty;

        var binding = new System.Windows.Data.Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = System.Windows.Data.BindingMode.OneWay,
            StringFormat = StringFormat
        };

        return binding.ProvideValue(serviceProvider);
    }
}
