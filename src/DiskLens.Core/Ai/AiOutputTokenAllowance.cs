namespace DiskLens.Core.Ai;

/// <summary>Leaves room for four purpose texts without exceeding common provider output limits.</summary>
internal static class AiOutputTokenAllowance
{
    public const int MaximumOutputTokens = 8192;
    private const int FramingTokens = 512;
    private const int TokensPerBilingualItem = 512;
    public const int MaximumRecordsPerRequest = (MaximumOutputTokens - FramingTokens) / TokensPerBilingualItem;

    public static int ForRecordCount(int count) =>
        (int)Math.Clamp(FramingTokens + Math.Max(0L, count) * TokensPerBilingualItem,
            FramingTokens, MaximumOutputTokens);
}
