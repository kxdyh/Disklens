using DiskLens.Core.Models;

namespace DiskLens.Tests.Ui;

[CollectionDefinition("Display language", DisableParallelization = true)]
public sealed class DisplayLanguageCollection { }

[Collection("Display language")]
public sealed class LocalizedNodeNameTests
{
    [Fact]
    public void English_only_translates_synthetic_overflow_names_and_preserves_real_file_names()
    {
        var previous = L.Current.Language;
        L.SetLanguage("en");
        try
        {
            foreach (var name in new[] { "设置", "用户资料", "其余 2 项", "扫描完成.txt" })
            {
                var file = File(name);
                Assert.Equal(name, L.NodeName(file));
                Assert.Equal(name, L.NodeName(file with { IsDirectory = true, Attributes = FileAttributes.Directory }));
            }
            var overflow = File("其余 2 项") with
            {
                IsDirectory = true,
                RiskTags = [DisplayNodeMetadata.AggregateRiskTag, DisplayNodeMetadata.OverflowRiskTag],
                Children = [File("设置"), File("用户资料")],
            };
            Assert.Equal("2 more items", L.NodeName(overflow));
            Assert.Equal("其余 2 项", overflow.Name);
        }
        finally { L.SetLanguage(previous); }
    }

    private static FileSystemNode File(string name) => new($@"E:\fixture\{name}", name,
        Path.GetExtension(name), 1024, FileAttributes.Normal, null, null, false, false, [], []);
}
