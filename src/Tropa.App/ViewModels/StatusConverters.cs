using Avalonia.Data.Converters;

namespace Tropa.App.ViewModels;

/// <summary>Цвет плашки статуса сервера по его виду: ok / warn / bad.</summary>
internal static class StatusConverters
{
    public static readonly IValueConverter IsOk = new FuncValueConverter<string?, bool>(k => k == "ok");
    public static readonly IValueConverter IsWarn = new FuncValueConverter<string?, bool>(k => k == "warn");
    public static readonly IValueConverter IsBad = new FuncValueConverter<string?, bool>(k => k == "bad");
}
