// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ECL.Converters;

/// <summary>Bool to Visibility with an optional inversion; used by the page switcher.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Hides an element when the bound string is null or empty.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool has = !string.IsNullOrWhiteSpace(value as string);
        if (Invert) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Inverts a boolean, for "disable when already active" bindings.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not bool b || !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not bool b || !b;
}

/// <summary>
/// Two-way helper for the navigation rail: binds an int page index to the
/// IsChecked flag of a RadioButton, using ConverterParameter as the page number.
/// </summary>
public sealed class PageToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int page && parameter is string s && int.TryParse(s, out int target) && page == target;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool on && on && parameter is string s && int.TryParse(s, out int target))
            return target;
        return System.Windows.Data.Binding.DoNothing;
    }
}

