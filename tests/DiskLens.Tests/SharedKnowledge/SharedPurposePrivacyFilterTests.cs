using DiskLens.Core.SharedKnowledge;

namespace DiskLens.Tests.SharedKnowledge;

public sealed partial class SharedOriginalPurposeTests
{
    [Theory]
    [InlineData(@"C:\Program Files\Adobe\Cache", "Adobe缓存", "Adobe应用生成的临时缓存", "Adobe cache", "Temporary Adobe application cache")]
    [InlineData(@"C:\Windows\System32\Cache", "系统缓存", "System32系统组件的临时缓存", "System32 cache", "Temporary cache for System32 components")]
    [InlineData(@"C:\tools\ain\Cache", "运行组件", "应用运行需要的组件", "Runtime components", "Files required to maintain application runtime")]
    public async Task Privacy_filter_preserves_public_terms_verbatim_without_embedded_substring_false_positives(
        string path, string brief, string detail, string briefEn, string detailEn)
    {
        var consent = new Consent(); var candidate = Candidate(path); var client = new Client();
        var original = Result(path) with { ShortPurpose = brief, Reason = detail, ShortPurposeEn = briefEn, ReasonEn = detailEn };
        await new SharedKnowledgePipeline(consent, new Extractor(), client).ContributeAsync([candidate], [original], Provenance(consent));
        var contribution = Assert.Single(client.Contributions);
        Assert.Equal(brief, contribution.ShortPurpose); Assert.Equal(detail, contribution.Reason);
        Assert.Equal(briefEn, contribution.ShortPurposeEn); Assert.Equal(detailEn, contribution.ReasonEn);
        Assert.Equal(detail, original.Reason);
    }

    [Theory]
    [InlineData(@"C:\Users\Alice\Cache", "Alice应用缓存", "Temporary Alice application cache")]
    [InlineData(@"D:\ProjectNova\Cache", "ProjectNova应用缓存", "Temporary ProjectNova application cache")]
    [InlineData(@"D:\PrivateProject\Cache", "应用缓存", "PrivateProjectCache application data")]
    [InlineData(@"D:\PrivateProject.txt", "PrivateProject应用缓存", "Temporary application cache")]
    [InlineData(@"C:\Users\Adobe\Cache", "Adobe应用缓存", "Temporary Adobe application cache")]
    [InlineData(@"C:\Users\ain\Cache", "应用缓存", "Files required to maintain application runtime")]
    public async Task Privacy_filter_keeps_category_only_for_private_context_without_changing_local_analysis(
        string path, string detail, string detailEn)
    {
        var consent = new Consent(); var candidate = Candidate(path); var client = new Client();
        var original = Result(path) with { Reason = detail, ReasonEn = detailEn };
        await new SharedKnowledgePipeline(consent, new Extractor(), client).ContributeAsync([candidate], [original], Provenance(consent));
        var contribution = Assert.Single(client.Contributions);
        Assert.Equal(original.PurposeCategory, contribution.Purpose);
        Assert.Equal(original.SuggestedAction.ToString(), contribution.Action);
        Assert.Equal(original.OriginalAnalysisId, contribution.AnalysisId);
        Assert.Equal("High", contribution.ConfidenceLevel);
        Assert.Empty(contribution.ShortPurpose); Assert.Empty(contribution.Reason);
        Assert.Empty(contribution.ShortPurposeEn); Assert.Empty(contribution.ReasonEn);
        Assert.Equal(detail, original.Reason); Assert.Equal(detailEn, original.ReasonEn);
    }

    [Theory]
    [InlineData("Temporary API Key data")]
    [InlineData("Temporary sk-secret-key data")]
    [InlineData("Cache for user@example.com")]
    [InlineData("Cache at C:\\Users\\Alice")]
    public async Task Privacy_filter_still_rejects_secrets_and_invalid_formats_instead_of_sending_categories(string detailEn)
    {
        var consent = new Consent(); var candidate = Candidate(); var client = new Client();
        await new SharedKnowledgePipeline(consent, new Extractor(), client).ContributeAsync([candidate],
            [Result(candidate.Path) with { ReasonEn = detailEn }], Provenance(consent));
        Assert.Empty(client.Contributions);
    }
}
