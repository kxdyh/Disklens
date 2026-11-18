using System.Text;
using System.Text.RegularExpressions;

namespace DiskLens.Core.SharedKnowledge;

/// <summary>Published, plain-text notices. This response contains no management credentials.</summary>
public sealed record SharedAnnouncement(string Id, string TitleZh, string BodyZh,
    string TitleEn, string BodyEn, int Priority, DateTimeOffset PublishedAt,
    DateTimeOffset UpdatedAt, int Revision);

public sealed record SharedAnnouncementFeed(int SchemaVersion, IReadOnlyList<SharedAnnouncement> Items)
{
    public static SharedAnnouncementFeed Empty { get; } = new(1, []);

    public bool IsValid()
    {
        if (SchemaVersion != 1 || Items is null || Items.Count > 50) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        return Items.All(item => item is not null && ids.Add(item.Id ?? "")
            && Regex.IsMatch(item.Id ?? "", @"\A[a-f0-9]{8}-[a-f0-9]{4}-[1-5][a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}\z")
            && ValidText(item.TitleZh, 240, required: true) && ValidText(item.BodyZh, 8000, required: true)
            && ValidText(item.TitleEn, 240, required: false) && ValidText(item.BodyEn, 8000, required: false)
            && item.Priority is >= -100 and <= 100 && item.Revision >= 1
            && item.PublishedAt > DateTimeOffset.UnixEpoch && item.UpdatedAt >= item.PublishedAt
            && item.UpdatedAt <= DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private static bool ValidText(string? text, int maximumBytes, bool required) => text is not null
        && (!required || !string.IsNullOrWhiteSpace(text)) && Encoding.UTF8.GetByteCount(text) <= maximumBytes
        && !text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'));
}
