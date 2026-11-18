namespace DiskLens.Core.SharedKnowledge;

/// <summary>Reviewed fixed directories, anchored to the actual OS/profile location by the extractor.</summary>
internal static class SharedStandardPurposeCatalog
{
    // These are synthetic roles, never arbitrary local names or a global path-token expansion.
    internal const string RoleTokens = "os-windows os-windows-system32 os-windows-syswow64 os-windows-winsxs "
        + "os-windows-assembly os-windows-fonts os-windows-installer os-windows-dotnet os-windows-systemapps "
        + "os-windows-softwaredistribution os-windows-systemresources os-windows-serviceprofiles "
        + "os-windows-speech-onecore os-windows-speech os-windows-boot os-windows-prefetch os-windows-uus "
        + "os-windows-cursors os-windows-apppatch os-windows-rescache os-windows-inf os-windows-inputmethod "
        + "os-windows-panther os-windows-globalization os-windows-servicing os-windows-appcompat os-windows-web "
        + "os-windows-containers os-windows-ime os-windows-media os-windows-immersivecontrolpanel "
        + "os-windows-shellexperiences os-windows-logs os-windows-inboxapps os-windows-diagnostics "
        + "os-windows-provisioning os-windows-shellcomponents os-windows-ocr os-windows-diagtrack "
        + "os-windows-security os-windows-pla os-windows-printdialog os-windows-help os-windows-resources "
        + "os-profile-tool-cache os-profile-tool-nuget os-profile-tool-gradle os-profile-tool-codex "
        + "os-profile-tool-vscode os-profile-tool-docker os-profile-tool-cargo os-profile-tool-rustup "
        + "os-profile-tool-npm os-profile-tool-m2 os-profile-tool-p2 os-profile-tool-virtualenvs "
        + "os-profile-tool-insightface os-profile-tool-android os-profile-tool-claude os-profile-tool-cursor";

    internal static readonly (string Name, string Role)[] WindowsDirectories =
    [
        ("System32", "os-windows-system32"), ("SysWOW64", "os-windows-syswow64"), ("WinSxS", "os-windows-winsxs"),
        ("assembly", "os-windows-assembly"), ("Fonts", "os-windows-fonts"), ("Installer", "os-windows-installer"),
        ("Microsoft.NET", "os-windows-dotnet"), ("SystemApps", "os-windows-systemapps"),
        ("SoftwareDistribution", "os-windows-softwaredistribution"), ("SystemResources", "os-windows-systemresources"),
        ("ServiceProfiles", "os-windows-serviceprofiles"), ("Speech_OneCore", "os-windows-speech-onecore"),
        ("Speech", "os-windows-speech"), ("Boot", "os-windows-boot"), ("Prefetch", "os-windows-prefetch"),
        ("UUS", "os-windows-uus"), ("Cursors", "os-windows-cursors"), ("apppatch", "os-windows-apppatch"),
        ("rescache", "os-windows-rescache"), ("INF", "os-windows-inf"), ("InputMethod", "os-windows-inputmethod"),
        ("Panther", "os-windows-panther"), ("Globalization", "os-windows-globalization"),
        ("servicing", "os-windows-servicing"), ("appcompat", "os-windows-appcompat"), ("Web", "os-windows-web"),
        ("Containers", "os-windows-containers"), ("IME", "os-windows-ime"), ("Media", "os-windows-media"),
        ("ImmersiveControlPanel", "os-windows-immersivecontrolpanel"), ("ShellExperiences", "os-windows-shellexperiences"),
        ("Logs", "os-windows-logs"), ("InboxApps", "os-windows-inboxapps"), ("diagnostics", "os-windows-diagnostics"),
        ("Provisioning", "os-windows-provisioning"), ("ShellComponents", "os-windows-shellcomponents"),
        ("OCR", "os-windows-ocr"), ("DiagTrack", "os-windows-diagtrack"), ("security", "os-windows-security"),
        ("PLA", "os-windows-pla"), ("PrintDialog", "os-windows-printdialog"), ("Help", "os-windows-help"),
        ("Resources", "os-windows-resources"),
    ];

    internal static readonly (string Name, string Role)[] ProfileDirectories =
    [
        (".cache", "os-profile-tool-cache"), (".nuget", "os-profile-tool-nuget"),
        (".gradle", "os-profile-tool-gradle"), (".codex", "os-profile-tool-codex"),
        (".vscode", "os-profile-tool-vscode"), (".docker", "os-profile-tool-docker"),
        (".cargo", "os-profile-tool-cargo"), (".rustup", "os-profile-tool-rustup"),
        (".npm", "os-profile-tool-npm"), (".m2", "os-profile-tool-m2"), (".p2", "os-profile-tool-p2"),
        (".virtualenvs", "os-profile-tool-virtualenvs"), (".insightface", "os-profile-tool-insightface"),
        (".android", "os-profile-tool-android"), (".claude", "os-profile-tool-claude"),
        (".cursor", "os-profile-tool-cursor"),
    ];
}
