using System.Buffers.Binary;
using System.Diagnostics;
using DiskLens.Core.Scanning;

namespace DiskLens.Tests.Scanning;

public sealed class WindowsBackupDirectoryReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiskLens.Backup.Tests"), Guid.NewGuid().ToString("N"));

    [Fact]
    public void Ordinary_directory_should_read_every_page_without_admin_or_a_privilege_scope()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        var expected = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 200; i++)
        {
            var name = $"{i:D4}-{new string('n', 215)}.bin";
            using var file = File.Create(Path.Combine(_root, name));
            expected.Add(name);
        }

        var dataName = "中文-é-😀.bin";
        File.WriteAllBytes(Path.Combine(_root, dataName), new byte[12_345]);
        expected.Add(dataName);
        Directory.CreateDirectory(Path.Combine(_root, "child"));
        expected.Add("child");

        var reader = new WindowsBackupDirectoryReader();
        Assert.True(reader.TryRead(_root, out var entries, out var error), $"Win32 error {error}");
        Assert.Equal(expected.Order(), entries.Select(entry => entry.Name).Order());
        Assert.Equal(expected.Count, entries.Count);
        var data = Assert.Single(entries, entry => entry.Name == dataName);
        Assert.Equal(12_345, data.LogicalSizeBytes);
        Assert.True(data.AllocatedBytes >= 12_345);
        Assert.NotNull(data.CreatedAt);
        Assert.NotNull(data.ModifiedAt);
        Assert.NotNull(data.Identity);
        Assert.True(Assert.Single(entries, entry => entry.Name == "child").Attributes.HasFlag(FileAttributes.Directory));
    }

    [Fact]
    public void Empty_directory_should_succeed_and_failed_file_or_missing_path_should_publish_no_entries()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        var reader = new WindowsBackupDirectoryReader();
        Assert.True(reader.TryRead(_root, out var empty, out var error), $"Win32 error {error}");
        Assert.Empty(empty);
        var filePath = Path.Combine(_root, "file.bin");
        File.WriteAllBytes(filePath, [1]);
        Assert.False(reader.TryRead(filePath, out var fileEntries, out error));
        Assert.NotEqual(0, error);
        Assert.Empty(fileEntries);
        Assert.False(reader.TryRead(Path.Combine(_root, "missing"), out var missing, out error));
        Assert.NotEqual(0, error);
        Assert.Empty(missing);
    }

    [Fact]
    public void Metadata_should_read_a_file_and_directory_without_enumerating_their_contents()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        var filePath = Path.Combine(_root, "metadata.bin");
        File.WriteAllBytes(filePath, new byte[17_321]);
        var reader = new WindowsBackupDirectoryReader();
        Assert.True(reader.TryReadMetadata(filePath, out var file, out var error), $"Win32 error {error}");
        Assert.NotNull(file);
        Assert.Equal("metadata.bin", file.Name);
        Assert.Equal(17_321, file.LogicalSizeBytes);
        Assert.True(file.AllocatedBytes >= file.LogicalSizeBytes);
        Assert.False(file.Attributes.HasFlag(FileAttributes.Directory));
        Assert.NotNull(file.Identity);
        Assert.NotNull(file.CreatedAt);
        Assert.NotNull(file.ModifiedAt);

        Assert.True(reader.TryReadMetadata(_root, out var directory, out error), $"Win32 error {error}");
        Assert.NotNull(directory);
        Assert.True(directory.Attributes.HasFlag(FileAttributes.Directory));
        Assert.Equal(Path.GetFileName(_root), directory.Name);
        Assert.NotEqual(file.Identity, directory.Identity);

        Assert.True(reader.TryRead(_root, out var children, out error), $"Win32 error {error}");
        Assert.Equal(file.Identity, Assert.Single(children).Identity);
        Assert.False(reader.TryReadMetadata(Path.Combine(_root, "missing"), out var missing, out error));
        Assert.Null(missing);
        Assert.NotEqual(0, error);
    }

    [Fact]
    public void Metadata_cancellation_should_stop_before_any_native_read()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            new WindowsBackupDirectoryReader().TryReadMetadata(_root, out _, out _, source.Token));
    }

    [Fact]
    public void A_confirmed_directory_junction_should_return_an_explicit_stop_without_enumerating_its_target()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        File.WriteAllBytes(Path.Combine(target, "must-not-be-enumerated.bin"), [1]);
        var junction = Path.Combine(_root, "junction");
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "/c", "mklink", "/J", junction, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(10_000));
        Assert.True(process.ExitCode == 0, process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd());

        try
        {
            var reader = new WindowsBackupDirectoryReader();
            Assert.True(reader.TryReadMetadata(junction, out var metadata, out var error), $"Win32 error {error}");
            Assert.NotNull(metadata);
            Assert.True(metadata.Attributes.HasFlag(FileAttributes.ReparsePoint));
            Assert.False(reader.TryRead(junction, out var entries, out error));
            Assert.Equal(WindowsBackupDirectoryReader.ErrorStoppedOnReparsePoint, error);
            Assert.Empty(entries);
        }
        finally
        {
            // Remove the link before its target so recursive fixture cleanup never
            // encounters a dangling mount-point reparse record.
            Directory.Delete(junction);
        }
    }

    [Fact]
    public void Cancellation_should_stop_before_any_native_directory_read()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            new WindowsBackupDirectoryReader().TryRead(_root, out _, out _, source.Token));
    }

    [Fact]
    public void Decoder_should_preserve_raw_utf16_and_combine_volume_with_full_file_identity()
    {
        var name = "raw-\ud800-name";
        var buffer = Entry(name);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(72), 0x1234);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(80), 0x5678);
        Assert.True(WindowsBackupDirectoryReader.TryParseBuffer(buffer, 0xABCD, out var entries));
        var entry = Assert.Single(entries);
        Assert.Equal(name, entry.Name);
        Assert.Equal("000000000000ABCD:00000000000056780000000000001234", entry.Identity);
        Assert.Null(entry.CreatedAt);
        Assert.Null(entry.ModifiedAt);
    }

    [Theory]
    [InlineData(0, 0xFFFFFFF8)] // Offset beyond the supplied buffer.
    [InlineData(0, 88)] // Offset overlaps the current file name.
    [InlineData(0, 91)] // Unaligned offset.
    [InlineData(60, 0)]
    [InlineData(60, 3)] // Odd UTF-16 byte length.
    [InlineData(60, 65536)]
    public void Decoder_should_reject_malformed_offsets_and_name_lengths(int fieldOffset, uint value)
    {
        var buffer = Entry("file.bin");
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(fieldOffset), value);
        Assert.False(WindowsBackupDirectoryReader.TryParseBuffer(buffer, null, out var entries));
        Assert.Empty(entries);
    }

    [Fact]
    public void Decoder_should_not_publish_the_first_page_item_when_a_later_entry_is_invalid()
    {
        var first = Entry("ok.bin", padded: true);
        var second = Entry("bad.bin");
        BinaryPrimitives.WriteUInt32LittleEndian(first, (uint)first.Length);
        BinaryPrimitives.WriteInt64LittleEndian(second.AsSpan(48), -1);
        Assert.False(WindowsBackupDirectoryReader.TryParseBuffer(first.Concat(second).ToArray(), null, out var entries));
        Assert.Empty(entries);
        Assert.False(WindowsBackupDirectoryReader.TryParseBuffer(new byte[87], null, out entries));
        Assert.Empty(entries);
    }

    [Theory]
    [InlineData("bad\\name")]
    [InlineData("bad/name")]
    [InlineData("bad\0name")]
    public void Decoder_should_reject_names_that_are_not_a_single_directory_entry(string name)
    {
        Assert.False(WindowsBackupDirectoryReader.TryParseBuffer(Entry(name), null, out var entries));
        Assert.Empty(entries);
    }

    private static byte[] Entry(string name, bool padded = false)
    {
        var length = 88 + name.Length * 2;
        var buffer = new byte[padded ? (length + 7) & ~7 : length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(56), (uint)FileAttributes.Normal);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(60), (uint)(name.Length * 2));
        for (var i = 0; i < name.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(88 + i * 2), name[i]);
        }
        return buffer;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
