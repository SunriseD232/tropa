using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropa.Core.Compatibility;
using Tropa.Core.Model;

namespace Tropa.App.ViewModels;

/// <summary>
/// Строка настроек. Ключ справки (<see cref="InfoKey"/>) — как в info.ru.json, ключ совместимости
/// (<see cref="CompatKey"/>) — как в CompatRules: если пункт сейчас недоступен, он выключен
/// и под ним написано почему (docs/06-features.md, «Общие принципы»).
/// </summary>
internal abstract partial class SettingRow(string title, string infoKey, string? compatKey, string? hint) : ObservableObject
{
    public string Title { get; } = title;

    public string InfoKey { get; } = infoKey;

    public string CompatKey { get; } = compatKey ?? infoKey;

    public string? Hint { get; } = hint;

    /// <summary>Сохранить изменение: передаётся разделом, чтобы строка не знала о движке.</summary>
    public Action<Func<AppSettings, AppSettings>>? Commit { get; set; }

    public Action<string, string?>? ShowInfo { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable), nameof(Note))]
    public partial string? DisabledReason { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Note))]
    public partial string? Error { get; set; }

    public bool IsAvailable => DisabledReason is null;

    /// <summary>Под пунктом: ошибка ввода, причина недоступности или подсказка.</summary>
    public string? Note => Error ?? (DisabledReason is null ? Hint : "Сейчас недоступно: " + DisabledReason);

    public bool NoteIsWarning => Error is not null || DisabledReason is not null;

    protected bool Loading { get; private set; }

    public void Load(AppSettings settings, CompatResult compat)
    {
        Loading = true;
        try
        {
            DisabledReason = compat.Disabled.TryGetValue(CompatKey, out var reason) ? reason : null;
            OnLoad(settings);
            Error = null;
            OnPropertyChanged(nameof(NoteIsWarning));
        }
        finally
        {
            Loading = false;
        }
    }

    protected abstract void OnLoad(AppSettings settings);

    protected void Save(Func<AppSettings, AppSettings> change)
    {
        if (!Loading)
            Commit?.Invoke(change);
    }

    [RelayCommand]
    private void Info() => ShowInfo?.Invoke(InfoKey, DisabledReason);
}

internal sealed partial class ToggleRow(string title, string infoKey, Func<AppSettings, bool> get, Func<AppSettings, bool, AppSettings> set,
    string? compatKey = null, string? hint = null) : SettingRow(title, infoKey, compatKey, hint)
{
    [ObservableProperty]
    public partial bool Value { get; set; }

    protected override void OnLoad(AppSettings settings) => Value = get(settings);

    partial void OnValueChanged(bool value) => Save(s => set(s, value));
}

internal sealed partial class ChoiceRow(string title, string infoKey, IReadOnlyList<string> options, Func<AppSettings, int> get,
    Func<AppSettings, int, AppSettings> set, string? compatKey = null, string? hint = null) : SettingRow(title, infoKey, compatKey, hint)
{
    public IReadOnlyList<string> Options { get; } = options;

    [ObservableProperty]
    public partial int Index { get; set; }

    protected override void OnLoad(AppSettings settings) => Index = Math.Clamp(get(settings), 0, Options.Count - 1);

    partial void OnIndexChanged(int value)
    {
        if (value >= 0)
            Save(s => set(s, value));
    }
}

/// <summary>Текст или число. Сохраняется, когда поле теряет фокус; неверное значение не сохраняется.</summary>
internal sealed partial class TextRow(string title, string infoKey, Func<AppSettings, string> get, Func<AppSettings, string, AppSettings> set,
    Func<string, string?> validate, string? compatKey = null, string? hint = null, bool multiline = false, string? placeholder = null)
    : SettingRow(title, infoKey, compatKey, hint)
{
    public bool Multiline { get; } = multiline;

    public string? Placeholder { get; } = placeholder;

    [ObservableProperty]
    public partial string Value { get; set; } = "";

    protected override void OnLoad(AppSettings settings) => Value = get(settings);

    partial void OnValueChanged(string value)
    {
        if (Loading)
            return;
        Error = validate(value);
        OnPropertyChanged(nameof(NoteIsWarning));
        if (Error is null)
            Save(s => set(s, value.Trim()));
    }

    public static TextRow Number(string title, string infoKey, Func<AppSettings, int> get, Func<AppSettings, int, AppSettings> set,
        Func<int, string?> validate, string? compatKey = null, string? hint = null) =>
        new(title, infoKey, s => get(s).ToString(CultureInfo.InvariantCulture),
            (s, v) => set(s, int.Parse(v, CultureInfo.InvariantCulture)),
            v => int.TryParse(v.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? validate(n) : "Нужно целое число.",
            compatKey, hint);
}

/// <summary>Пункт-кнопка: экспорт, просмотр конфига, переход на другой экран.</summary>
internal sealed class ActionRow(string title, string infoKey, string? hint, params (string Label, IRelayCommand Command)[] actions)
    : SettingRow(title, infoKey, null, hint)
{
    public IReadOnlyList<(string Label, IRelayCommand Command)> Actions { get; } = actions;

    public string? FirstLabel => Actions.Count > 0 ? Actions[0].Label : null;

    public IRelayCommand? FirstCommand => Actions.Count > 0 ? Actions[0].Command : null;

    public string? SecondLabel => Actions.Count > 1 ? Actions[1].Label : null;

    public IRelayCommand? SecondCommand => Actions.Count > 1 ? Actions[1].Command : null;

    public bool HasSecond => Actions.Count > 1;

    protected override void OnLoad(AppSettings settings)
    {
    }
}

/// <summary>Строка только для чтения (версии ядер и т. п.).</summary>
internal sealed partial class LabelRow(string title, string infoKey, Func<string> text) : SettingRow(title, infoKey, null, null)
{
    [ObservableProperty]
    public partial string Text { get; set; } = "";

    protected override void OnLoad(AppSettings settings) => Text = text();
}

internal sealed partial class SettingsSection(string key, string title, string? description, IReadOnlyList<SettingRow> rows) : ObservableObject
{
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    public string Key { get; } = key;

    public string Title { get; } = title;

    public string? Description { get; } = description;

    public IReadOnlyList<SettingRow> Rows { get; } = rows;
}
