using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Zapret2UI.Localization;
using Zapret2UI.Models;
using Zapret2UI.Services.Engine;

namespace Zapret2UI.Mvvm;

/// <summary>
/// Two bound values -> Visibility: Visible when they are the SAME instance.
/// Used to flag the currently-running preset inside the preset list.
/// </summary>
public sealed class RefEqualsToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object? p, CultureInfo c) =>
        values.Length == 2 && ReferenceEquals(values[0], values[1]) && values[0] is not null
            ? Visibility.Visible : Visibility.Collapsed;
    public object[] ConvertBack(object? v, Type[] t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Inverts a boolean.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => !(v is true);
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => !(v is true);
}

/// <summary>bool -> Visibility, with optional "invert" parameter.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        bool b = v is true;
        if (p as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        v is Visibility.Visible;
}

/// <summary>EngineState -> status dot colour.</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush RunningBrush = new(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush StartingBrush = new(Color.FromRgb(0xA1, 0xA1, 0xAA));
    private static readonly SolidColorBrush StoppedBrush = new(Color.FromRgb(0x52, 0x52, 0x5B));

    public object Convert(object? v, Type t, object? p, CultureInfo c) => v switch
    {
        EngineState.Running => RunningBrush,
        EngineState.Starting or EngineState.Stopping => StartingBrush,
        _ => StoppedBrush,
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>EngineState -> Russian label.</summary>
public sealed class StateToTextConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v switch
    {
        EngineState.Running => Loc.T("Работает"),
        EngineState.Starting => Loc.T("Запуск…"),
        EngineState.Stopping => Loc.T("Остановка…"),
        _ => Loc.T("Остановлен"),
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>DiagStatus -> short cell label.</summary>
public sealed class DiagStatusToTextConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) => v switch
    {
        DiagStatus.Ok => "OK",
        DiagStatus.Fail => Loc.T("ОШИБКА"),
        DiagStatus.Timeout => Loc.T("таймаут"),
        DiagStatus.NotSupported => Loc.T("н/д"),
        DiagStatus.Running => "…",
        DiagStatus.Skip => "—",
        _ => "·",
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>DiagStatus -> cell foreground colour.</summary>
public sealed class DiagStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Ok = new(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush Bad = new(Color.FromRgb(0xD4, 0xD4, 0xD8));
    private static readonly SolidColorBrush Warn = new(Color.FromRgb(0xA1, 0xA1, 0xAA));
    private static readonly SolidColorBrush Muted = new(Color.FromRgb(0x71, 0x71, 0x7A));

    public object Convert(object? v, Type t, object? p, CultureInfo c) => v switch
    {
        DiagStatus.Ok => Ok,
        DiagStatus.Fail => Bad,
        DiagStatus.Timeout => Warn,
        _ => Muted,
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
