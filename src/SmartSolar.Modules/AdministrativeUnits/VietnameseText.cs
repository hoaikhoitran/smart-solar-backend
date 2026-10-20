using System.Globalization;
using System.Text;

namespace SmartSolar.Modules.AdministrativeUnits;

/// <summary>Accent-insensitive search keys for Vietnamese names ("Phường Thủ Đức" → "phuong thu duc").</summary>
public static class VietnameseText
{
    /// <summary>Removes diacritics (đ/Đ → d), lower-cases (invariant) and collapses whitespace; null/blank → "".</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var lastWasSpace = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            var mapped = c switch { 'đ' or 'Đ' => 'd', _ => char.ToLowerInvariant(c) };
            if (char.IsWhiteSpace(mapped))
            {
                if (lastWasSpace) continue;
                lastWasSpace = true;
                builder.Append(' ');
                continue;
            }
            lastWasSpace = false;
            builder.Append(mapped);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
