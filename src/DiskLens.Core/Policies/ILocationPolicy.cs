namespace DiskLens.Core.Policies;

public interface ILocationPolicy
{
    LocationAssessment AssessPath(string path, bool isDirectory, FileAttributes attributes);

    bool IsProtectedLocation(string path);

    IReadOnlyList<string> GetRiskTags(string path, bool isDirectory, FileAttributes attributes);
}
