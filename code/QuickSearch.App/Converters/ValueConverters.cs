using System.Globalization;
using System.Windows;
using System.Windows.Data;
using QuickSearch.Core.Search;
using Binding = System.Windows.Data.Binding;

namespace QuickSearch.App.Converters;

/// <summary>
/// Maps a search result's type to a Segoe Fluent Icons / MDL2 Assets glyph character.
/// Used with a TextBlock that has FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets".
/// </summary>
public class SearchResultTypeToIconConverter : IValueConverter
{
    // Segoe Fluent Icons / MDL2 Assets glyph codes:
    // E8B7 = FolderOpen,  E7C3 = Page/Document,  E774 = Globe,  E71D = App (grid)
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is SearchResultType type)
        {
            return type switch
            {
                SearchResultType.Folder    => "\uE8B7",  // Folder
                SearchResultType.File      => "\uE7C3",  // Document/Page
                SearchResultType.WebSearch => "\uE774",  // Globe
                SearchResultType.Application => "\uE71D", // App grid
                _ => "\uE8A5"                             // Generic file
            };
        }
        return "\uE8A5";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility visibility)
        {
            return visibility == Visibility.Visible;
        }
        return false;
    }
}

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value == null || string.IsNullOrWhiteSpace(value.ToString())
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return false;

        return value.ToString() == parameter.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue && boolValue && parameter != null)
        {
            return Enum.Parse(targetType, parameter.ToString()!);
        }
        return Binding.DoNothing;
    }
}
