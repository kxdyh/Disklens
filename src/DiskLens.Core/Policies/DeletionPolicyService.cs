namespace DiskLens.Core.Policies;

public sealed class DeletionPolicyService
{
    public DeletionPolicyService(ILocationPolicy locationPolicy)
    {
        // Location risk still informs scanning and AI advice. It must not veto
        // an explicitly confirmed, manual move to the Windows Recycle Bin.
        ArgumentNullException.ThrowIfNull(locationPolicy);
    }

    public DeletionDecision Evaluate(string path, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new DeletionDecision(
                CanOpenLocation: false,
                CanRecycle: false,
                Reason: "路径为空，无法执行操作。");
        }

        if (IsFileSystemRoot(path))
        {
            return new DeletionDecision(
                CanOpenLocation: true,
                CanRecycle: false,
                Reason: "根目录不允许直接回收。");
        }

        return new DeletionDecision(
            CanOpenLocation: true,
            CanRecycle: true,
            Reason: isDirectory ? "目录可手动送入回收站。" : "文件可手动送入回收站。");
    }

    private static bool IsFileSystemRoot(string path)
    {
        var trimmedPath = path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(trimmedPath))
        {
            return false;
        }

        var normalized = trimmedPath + Path.DirectorySeparatorChar;
        var root = Path.GetPathRoot(normalized)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !string.IsNullOrWhiteSpace(root)
               && string.Equals(trimmedPath, root, StringComparison.OrdinalIgnoreCase);
    }
}
