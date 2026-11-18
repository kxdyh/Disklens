using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DiskLens.App.Services.SharedKnowledge;

namespace DiskLens.App.Tests;

public sealed class WindowsSoftwareIdentityResolverTests
{
    [Fact]
    public void Registered_product_company_and_signer_create_only_a_public_identity_digest()
    {
        var result = Identity("Acme Writer", "Acme, Inc.", "3.2.0", "Acme Writer", "ACME Incorporated", "3.8.0", "Acme Inc.");
        Assert.True(result.Valid);
        Assert.Equal("3", result.Major);
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("acme\0acme writer"))).ToLowerInvariant();
        Assert.Equal("app-" + expected, result.Key);
        Assert.Matches("^app-[0-9a-f]{64}$", result.Key);
    }

    [Theory]
    [InlineData("Microsoft Visual Studio Code (User)", "Visual Studio Code")]
    [InlineData("Visual Studio Code 1.95.0 (x64)", "Visual Studio Code")]
    [InlineData("Visual Studio Code (System) (x64)", "Visual Studio Code")]
    public void Registration_decoration_does_not_change_product_ownership(string displayName, string productName)
    {
        Assert.True(Identity(displayName, "Microsoft Corporation", "1.95.0", productName,
            "Microsoft Corporation", "1.95.5", "Microsoft Corporation").Valid);
    }

    [Fact]
    public void Product_identity_is_stable_across_installation_decoration_patch_versions_and_unicode_width()
    {
        var original = Identity("Acme Writer", "Acme Inc", "3.2", "Acme Writer", "Acme Inc", "3.2", "Acme Inc");
        var decorated = Identity("ＡＣＭＥ Writer 3.7 (x64)", "ACME Incorporated", "3.7",
            "ＡＣＭＥ   Writer", "Acme, Inc.", "3.9", "Acme Inc");
        Assert.True(original.Valid);
        Assert.True(decorated.Valid);
        Assert.Equal(original.Key, decorated.Key);
        Assert.Equal(original.Major, decorated.Major);
    }

    [Theory]
    [InlineData("Acme Reader", "Acme Inc", "3.2", "Acme Writer", "Acme Inc", "3.2", "Acme Inc")]
    [InlineData("Acme Writer", "Other Inc", "3.2", "Acme Writer", "Acme Inc", "3.2", "Acme Inc")]
    [InlineData("Acme Writer", "Acme Inc", "3.2", "Acme Writer", "Other Inc", "3.2", "Acme Inc")]
    [InlineData("Acme Writer", "Acme Inc", "3.2", "Acme Writer", "Acme Inc", "3.2", "Acme Tools Inc")]
    [InlineData("Acme Writer", "Acme Inc", "4.0", "Acme Writer", "Acme Inc", "3.2", "Acme Inc")]
    [InlineData("Acme Writer", "Acme Inc", "unknown", "Acme Writer", "Acme Inc", "3.2", "Acme Inc")]
    [InlineData("Acme Writer", "Acme Inc", "3.2", "Acme Writer", "Acme Inc", "unknown", "Acme Inc")]
    [InlineData("Acme Writer Deluxe", "Acme Inc", "3.2", "Acme Writer", "Acme Inc", "3.2", "Acme Inc")]
    public void Any_mismatch_or_unknown_version_abstains(string name, string publisher, string registeredVersion,
        string product, string company, string version, string signer)
    {
        var result = Identity(name, publisher, registeredVersion, product, company, version, signer);
        Assert.False(result.Valid);
        Assert.Empty(result.Key);
        Assert.Empty(result.Major);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Every_independent_evidence_field_is_required(int missingIndex)
    {
        string?[] arguments = ["Acme Writer", "Acme Inc", "3.2", "Acme Writer", "Acme Inc", "3.2", "Acme Inc"];
        arguments[missingIndex] = null;
        var result = Identity(arguments[0], arguments[1], arguments[2], arguments[3], arguments[4], arguments[5], arguments[6]);
        Assert.False(result.Valid);
    }

    [Fact]
    public void Different_products_and_publishers_have_separate_digests()
    {
        var writer = Identity("Writer", "Acme Inc", "3.2", "Writer", "Acme Inc", "3.2", "Acme Inc");
        var reader = Identity("Reader", "Acme Inc", "3.2", "Reader", "Acme Inc", "3.2", "Acme Inc");
        var other = Identity("Writer", "Other Inc", "3.2", "Writer", "Other Inc", "3.2", "Other Inc");
        Assert.True(writer.Valid && reader.Valid && other.Valid);
        Assert.NotEqual(writer.Key, reader.Key);
        Assert.NotEqual(writer.Key, other.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Acme\r\nWriter")]
    [InlineData("Acme\tWriter")]
    [InlineData("Acme\u202eWriter")]
    [InlineData("Acme\ue000Writer")]
    [InlineData("C:\\Users\\private\\Writer")]
    [InlineData("https://private/Writer")]
    [InlineData("C：Users＼private＼Writer")]
    [InlineData("...")]
    public void Empty_unsafe_or_path_shaped_public_metadata_is_rejected(string? input)
    {
        object?[] arguments = [input, null];
        Assert.False((bool)Method("TryNormalizePublicMetadata").Invoke(null, arguments)!);
        Assert.Equal("", arguments[1]);
    }

    [Fact]
    public void Oversized_metadata_and_each_product_evidence_field_are_rejected()
    {
        var oversized = new string('a', 257);
        object?[] normalization = [oversized, null];
        Assert.False((bool)Method("TryNormalizePublicMetadata").Invoke(null, normalization)!);
        for (var i = 0; i < 7; i++)
        {
            string?[] arguments = ["Acme Writer", "Acme Inc", "3.2", "Acme Writer", "Acme Inc", "3.2", "Acme Inc"];
            arguments[i] = oversized;
            Assert.False(Identity(arguments[0], arguments[1], arguments[2], arguments[3], arguments[4], arguments[5], arguments[6]).Valid);
        }
    }

    [Theory]
    [InlineData("0003.2", true, "3")]
    [InlineData("3", true, "3")]
    [InlineData(" 3.2 ", true, "3")]
    [InlineData("v3.2", false, "")]
    [InlineData("3.unknown", false, "")]
    [InlineData("3..2", false, "")]
    [InlineData("3.2.1-beta.1", true, "3")]
    [InlineData("10000.2", false, "")]
    [InlineData("3\r\n", false, "")]
    public void Major_version_is_a_bounded_canonical_number(string version, bool expected, string major)
    {
        object?[] arguments = [version, null];
        Assert.Equal(expected, (bool)Method("TryMajor").Invoke(null, arguments)!);
        Assert.Equal(major, arguments[1]);
    }

    [Theory]
    [InlineData(@"C:\Apps\Acme\cache", @"C:\Apps\Acme", @"C:\Apps\Acme")]
    [InlineData(@"C:\Apps\Acme2\cache", @"C:\Apps\Acme", null)]
    [InlineData(@"C:\Apps\Acme\cache", @"C:\Apps\Other", null)]
    public void Registered_root_requires_a_real_path_boundary(string candidate, string root, string? expected)
    {
        Assert.Equal(expected, SelectRoot(candidate, root));
    }

    [Fact]
    public void Nested_registered_roots_and_parent_candidates_containing_another_installation_abstain()
    {
        string[] roots = [@"C:\Apps\Suite", @"C:\Apps\Suite\OtherProduct"];
        Assert.Null(SelectRoot(@"C:\Apps\Suite\OtherProduct\cache", roots));
        Assert.Null(SelectRoot(@"C:\Apps\Suite", roots));
        Assert.Null(SelectRoot(@"C:\Apps", roots));
        Assert.Equal(roots[0], SelectRoot(@"C:\Apps\Suite\cache", roots));
    }

    [Fact]
    public void Duplicate_registry_views_do_not_create_competing_roots()
    {
        Assert.Equal(@"C:\Apps\Acme", SelectRoot(@"C:\Apps\Acme\cache", @"C:\Apps\Acme", @"c:\apps\ACME"));
    }

    [Theory]
    [InlineData(@"\\server\share\app")]
    [InlineData(@"\\?\C:\Apps\Acme")]
    [InlineData(@"C:\Apps\Acme:stream")]
    [InlineData(@"Apps\Acme")]
    public void Network_device_stream_and_relative_paths_are_not_local_installations(string path)
    {
        Assert.False((bool)Method("SafePath").Invoke(null, [path])!);
    }

    private static (bool Valid, string Key, string Major) Identity(string? name, string? publisher,
        string? registeredVersion, string? product, string? company, string? version, string? signer)
    {
        object?[] arguments = [name, publisher, registeredVersion, product, company, version, signer, null, null];
        var valid = (bool)Method("TryCreatePublicIdentity").Invoke(null, arguments)!;
        return (valid, (string)arguments[7]!, (string)arguments[8]!);
    }

    private static string? SelectRoot(string candidate, params string[] roots)
        => (string?)Method("SelectUncontestedRoot").Invoke(null, [candidate, roots]);

    private static MethodInfo Method(string name)
        => typeof(WindowsSoftwareIdentityResolver).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
}
