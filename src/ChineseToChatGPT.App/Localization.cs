using System.Globalization;
using System.Windows;
using ChineseToChatGPT.Core;

namespace ChineseToChatGPT.App;

internal static class Localization
{
    private const string ResourcePrefix = "Resources/Strings.";

    public static void Apply(AppLanguage language)
    {
        var cultureName = language == AppLanguage.SimplifiedChinese ? "zh-CN" : "en-US";
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;

        for (var index = dictionaries.Count - 1; index >= 0; index--)
        {
            var source = dictionaries[index].Source?.OriginalString;
            if (source?.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase) == true)
            {
                dictionaries.RemoveAt(index);
            }
        }

        dictionaries.Insert(0, new ResourceDictionary
        {
            Source = new Uri($"{ResourcePrefix}{cultureName}.xaml", UriKind.Relative)
        });
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
    }

    public static string Get(string key, params object[] arguments)
    {
        var format = System.Windows.Application.Current.TryFindResource(key)?.ToString() ?? key;
        return arguments.Length == 0
            ? format
            : string.Format(CultureInfo.CurrentCulture, format, arguments);
    }
}
