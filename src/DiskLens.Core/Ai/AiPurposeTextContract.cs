namespace DiskLens.Core.Ai;

public static class AiPurposeTextContract
{
    public const int MaximumShortPurposeLength = 12;
    public const int MaximumReasonLength = 30;
    public const int MaximumShortPurposeEnLength = 80;
    public const int MaximumReasonEnLength = 240;

    public static bool IsValidShortPurpose(string? value) => IsPlainTextWithin(value, MaximumShortPurposeLength);
    public static bool IsValidReason(string? value) => IsPlainTextWithin(value, MaximumReasonLength);
    public static bool IsValidShortPurposeEn(string? value) => IsEnglishTextWithin(value, MaximumShortPurposeEnLength);
    public static bool IsValidReasonEn(string? value) => IsEnglishTextWithin(value, MaximumReasonEnLength);

    private static bool IsEnglishTextWithin(string? value, int maximumLength) =>
        IsPlainTextWithin(value, maximumLength) && value!.All(character => character is >= ' ' and <= '~'
            || IsEnglishTypography(character));

    internal static bool IsEnglishTypography(char character) =>
        character is '\u00A0' or '\u202F' or '\u2026'
            or >= '\u2010' and <= '\u2015' or >= '\u2018' and <= '\u201F';

    // Normalization is used only for privacy screening. Original model text stays verbatim.
    internal static string CreateSensitiveScreeningText(string text) => string.Concat(text.Select(character =>
        character is '\u00A0' or '\u202F' ? ' ' : character is >= '\u2010' and <= '\u2015' ? '-' : character));

    private static bool IsPlainTextWithin(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl)
        && value.EnumerateRunes().Count() <= maximumLength;
}
