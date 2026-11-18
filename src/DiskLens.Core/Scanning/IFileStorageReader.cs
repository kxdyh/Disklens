using DiskLens.Core.Models;

namespace DiskLens.Core.Scanning;

public interface IFileStorageReader
{
    FileStorageInfo Read(FileInfo fileInfo);

    // Metadata obtained from a backup-intent directory handle must not be read
    // again through ordinary FileInfo access, which may be denied by the ACL.
    FileStorageInfo Read(string path, long logicalBytes, FileAttributes attributes, uint? reparseTag = null)
        => Read(new FileInfo(path));
}
