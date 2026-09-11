using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace CodexQuotaWidget;

internal static class ThemeService
{
    public static bool Apply(string mode)
    {
        var dark = mode == "Dark" || (mode == "System" && IsSystemDark());
        var colors = dark
            ? new Dictionary<string, string>
            {
                ["BadgeBackground"] = "#FF292C32", ["BadgeBorder"] = "#FF3B3F47",
                ["BadgeText"] = "#FFF2F3F5", ["Chevron"] = "#FF9DA2AC",
                ["PopupBackground"] = "#FF23262B", ["PopupBorder"] = "#FF3A3E46",
                ["TextPrimary"] = "#FFF1F2F4", ["TextSecondary"] = "#FFB3B7BF",
                ["TextTertiary"] = "#FF858A94", ["CardBackground"] = "#FF2D3036",
                ["ButtonBackground"] = "#FF34383F", ["ButtonHover"] = "#FF41464F",
                ["ErrorBackground"] = "#FF4A3425", ["ErrorText"] = "#FFFFC277"
            }
            : new Dictionary<string, string>
            {
                ["BadgeBackground"] = "#FFF7F7F8", ["BadgeBorder"] = "#FFDCDDE0",
                ["BadgeText"] = "#FF3F434A", ["Chevron"] = "#FF92969D",
                ["PopupBackground"] = "#FFFCFCFD", ["PopupBorder"] = "#FFE0E1E3",
                ["TextPrimary"] = "#FF24262B", ["TextSecondary"] = "#FF62666E",
                ["TextTertiary"] = "#FF8A8E96", ["CardBackground"] = "#FFF3F3F4",
                ["ButtonBackground"] = "#FFF0F0F1", ["ButtonHover"] = "#FFE5E5E7",
                ["ErrorBackground"] = "#FFFFF4E5", ["ErrorText"] = "#FF9A5B12"
            };

        foreach (var pair in colors)
        {
            var brush = new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(pair.Value));
            brush.Freeze();
            System.Windows.Application.Current.Resources[pair.Key] = brush;
        }

        return dark;
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}
