using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace GithubWatcher.Desktop.Services;

public static class ThemeManager
{
    private const string PersonalizeKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize";

    public static void ApplySystemTheme(ResourceDictionary resources)
    {
        var isLightTheme = IsLightTheme();

        resources["AppBackgroundBrush"] = CreateBrush(isLightTheme ? System.Windows.Media.Color.FromRgb(0xF3, 0xF3, 0xF3) : System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20));
        resources["SurfaceBrush"] = CreateBrush(isLightTheme ? System.Windows.Media.Colors.White : System.Windows.Media.Color.FromRgb(0x1E, 0x1E, 0x1E));
        resources["WindowBorderBrush"] = CreateBrush(isLightTheme ? System.Windows.Media.Color.FromRgb(0xD0, 0xD0, 0xD0) : System.Windows.Media.Color.FromRgb(0x2D, 0x2D, 0x30));
        resources["ControlBackgroundBrush"] = CreateBrush(isLightTheme ? System.Windows.Media.Colors.White : System.Windows.Media.Color.FromRgb(0x25, 0x25, 0x25));
        resources["ControlBorderBrush"] = CreateBrush(isLightTheme ? System.Windows.Media.Color.FromRgb(0xC8, 0xC8, 0xC8) : System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x3A));
        resources["PrimaryTextBrush"] = CreateBrush(isLightTheme ? System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20) : System.Windows.Media.Colors.White);
        resources["SecondaryTextBrush"] = CreateBrush(isLightTheme ? System.Windows.Media.Color.FromRgb(0x66, 0x66, 0x66) : System.Windows.Media.Color.FromRgb(0xB0, 0xB0, 0xB0));
        resources["StatusBadgeBrush"] = CreateBrush(isLightTheme ? System.Windows.Media.Color.FromRgb(0xE5, 0xE5, 0xE5) : System.Windows.Media.Color.FromRgb(0x2D, 0x2D, 0x30));

        AppLogger.Log($"Theme applied: {(isLightTheme ? "Light" : "Dark")}");
    }

    private static bool IsLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false);
        var value = key?.GetValue("AppsUseLightTheme");
        return value is int intValue ? intValue != 0 : true;
    }

    private static SolidColorBrush CreateBrush(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
