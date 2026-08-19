using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace WordMcp.Storage;

/// <summary>
/// Opens untrusted Linux paths one component at a time and snapshots the already-open file.
/// No path-based metadata check is used as an authorization decision.
/// </summary>
public static class LinuxFileIdentity
{
    private const int AtEmptyPath = 0x1000;
    private const int OpenReadOnly = 0;
    private const int OpenNonBlocking = 0x800;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const uint StatxBasicStats = 0x07ff;
    private const uint RequiredStatxFields = 0x03c5;
    private const ushort FileTypeMask = 0xf000;
    private const ushort RegularFile = 0x8000;

    public sealed record Identity(
        ulong Device,
        ulong Inode,
        ulong Size,
        long ModifiedSeconds,
        uint ModifiedNanoseconds,
        long ChangedSeconds,
        uint ChangedNanoseconds);

    public sealed record Snapshot(string Path, string Sha256, long Bytes, Identity SourceIdentity);

    /// <summary>Reads stable metadata through the opened descriptor, rejecting links and non-regular files.</summary>
    public static Identity InspectUnderRoot(string root, IReadOnlyList<string> relativeSegments)
    {
        using var opened = OpenUnderRoot(root, relativeSegments);
        return opened.Identity;
    }

    /// <summary>Verifies that every directory component is opened with O_NOFOLLOW.</summary>
    public static void EnsureDirectoryUnderRoot(string root, IReadOnlyList<string> relativeSegments)
    {
        EnsureLinux();
        if (!Path.IsPathFullyQualified(root))
        {
            throw new SafeFileOpenException("The configured input root is not absolute.");
        }

        ValidateRelativeSegments(relativeSegments);
        var rootSegments = Path.GetFullPath(root)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        using var filesystemRoot = OpenDirectoryDescriptor("/");
        SafeFileHandle currentDirectory = filesystemRoot;
        var ownedDirectories = new List<SafeFileHandle>();
        try
        {
            foreach (var segment in rootSegments.Concat(relativeSegments))
            {
                var next = OpenDirectoryAt(currentDirectory, segment);
                ownedDirectories.Add(next);
                currentDirectory = next;
            }
        }
        finally
        {
            for (var index = ownedDirectories.Count - 1; index >= 0; index--)
            {
                ownedDirectories[index].Dispose();
            }
        }
    }

    /// <summary>
    /// Copies from the same descriptor that was inspected. A preselected identity can close the
    /// enumeration/open race, and a second descriptor check closes concurrent mutation races.
    /// </summary>
    public static async Task<Snapshot> CopySnapshotAsync(
        string root,
        IReadOnlyList<string> relativeSegments,
        string destinationPath,
        long maximumBytes,
        Identity? expectedIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        if (!Path.IsPathFullyQualified(destinationPath))
        {
            throw new ArgumentException("The snapshot destination must be absolute.", nameof(destinationPath));
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(destinationDirectory) || !Directory.Exists(destinationDirectory))
        {
            throw new DirectoryNotFoundException("The snapshot destination directory does not exist.");
        }

        using var opened = OpenUnderRoot(root, relativeSegments);
        if (expectedIdentity is not null && opened.Identity != expectedIdentity)
        {
            throw new SafeFileOpenException("The selected input changed before it could be opened.");
        }

        if (opened.Identity.Size > (ulong)maximumBytes)
        {
            throw new SafeFileOpenException("The selected input exceeds the configured size limit.");
        }

        FileStream? destination = null;
        try
        {
            destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                var read = await opened.Stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total = checked(total + read);
                if (total > maximumBytes)
                {
                    throw new SafeFileOpenException("The selected input exceeds the configured size limit.");
                }

                hash.AppendData(buffer.AsSpan(0, read));
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Dispose();
            destination = null;

            var finalIdentity = ReadIdentity(opened.Stream.SafeFileHandle);
            if (finalIdentity != opened.Identity || (ulong)total != opened.Identity.Size)
            {
                throw new SafeFileOpenException("The selected input changed while it was being copied.");
            }

            return new Snapshot(
                destinationPath,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                total,
                opened.Identity);
        }
        catch
        {
            destination?.Dispose();
            TryDeleteOwnedSnapshot(destinationPath);
            throw;
        }
    }

    /// <summary>Returns lexical path components only when <paramref name="path"/> is below root.</summary>
    public static IReadOnlyList<string> RelativeSegments(string root, string path)
    {
        EnsureLinux();
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(path))
        {
            throw new SafeFileOpenException("The input path is not absolute.");
        }

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var canonicalPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(canonicalRoot, canonicalPath);
        if (relative == "." || Path.IsPathFullyQualified(relative))
        {
            throw new SafeFileOpenException("The input path is outside the configured root.");
        }

        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments[0] == ".." || segments.Any(segment => segment == ".."))
        {
            throw new SafeFileOpenException("The input path is outside the configured root.");
        }

        ValidateRelativeSegments(segments);
        return segments;
    }

    private static OpenedFile OpenUnderRoot(string root, IReadOnlyList<string> relativeSegments)
    {
        EnsureLinux();
        if (!Path.IsPathFullyQualified(root))
        {
            throw new SafeFileOpenException("The configured input root is not absolute.");
        }

        ValidateRelativeSegments(relativeSegments);
        if (relativeSegments.Count == 0)
        {
            throw new SafeFileOpenException("The input path does not identify a file.");
        }

        var rootSegments = Path.GetFullPath(root)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        using var filesystemRoot = OpenDirectoryDescriptor("/");
        SafeFileHandle currentDirectory = filesystemRoot;
        var ownedDirectories = new List<SafeFileHandle>();
        try
        {
            foreach (var segment in rootSegments)
            {
                var next = OpenDirectoryAt(currentDirectory, segment);
                ownedDirectories.Add(next);
                currentDirectory = next;
            }

            for (var index = 0; index < relativeSegments.Count - 1; index++)
            {
                var next = OpenDirectoryAt(currentDirectory, relativeSegments[index]);
                ownedDirectories.Add(next);
                currentDirectory = next;
            }

            var descriptor = OpenAt(
                checked((int)currentDirectory.DangerousGetHandle()),
                relativeSegments[^1],
                OpenReadOnly | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec);
            if (descriptor < 0)
            {
                throw NativeFailure("The input could not be safely opened.");
            }

            var handle = new SafeFileHandle(descriptor, ownsHandle: true);
            try
            {
                var identity = ReadIdentity(handle);
                var stream = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false);
                return new OpenedFile(stream, identity);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            for (var index = ownedDirectories.Count - 1; index >= 0; index--)
            {
                ownedDirectories[index].Dispose();
            }
        }
    }

    private static SafeFileHandle OpenDirectoryDescriptor(string path)
    {
        var descriptor = Open(path, OpenReadOnly | OpenNonBlocking | OpenDirectory | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0)
        {
            throw NativeFailure("A configured input directory could not be safely opened.");
        }

        return new SafeFileHandle(descriptor, ownsHandle: true);
    }

    private static SafeFileHandle OpenDirectoryAt(SafeFileHandle parent, string segment)
    {
        var descriptor = OpenAt(
            checked((int)parent.DangerousGetHandle()),
            segment,
            OpenReadOnly | OpenNonBlocking | OpenDirectory | OpenNoFollow | OpenCloseOnExec);
        if (descriptor < 0)
        {
            throw NativeFailure("An input path component could not be safely opened.");
        }

        return new SafeFileHandle(descriptor, ownsHandle: true);
    }

    private static Identity ReadIdentity(SafeFileHandle handle)
    {
        var metadata = default(StatxBuffer);
        var descriptor = checked((int)handle.DangerousGetHandle());
        if (Statx(descriptor, string.Empty, AtEmptyPath, StatxBasicStats, ref metadata) != 0)
        {
            throw NativeFailure("Input metadata could not be read from the opened file.");
        }

        if ((metadata.Mask & RequiredStatxFields) != RequiredStatxFields)
        {
            throw new SafeFileOpenException("The filesystem did not provide all required identity metadata.");
        }

        if ((metadata.Mode & FileTypeMask) != RegularFile)
        {
            throw new SafeFileOpenException("The input is not a regular file.");
        }

        if (metadata.LinkCount != 1)
        {
            throw new SafeFileOpenException("Multiply-linked input files are not accepted.");
        }

        var device = ((ulong)metadata.DeviceMajor << 32) | metadata.DeviceMinor;
        return new Identity(
            device,
            metadata.Inode,
            metadata.Size,
            metadata.ModifiedSeconds,
            metadata.ModifiedNanoseconds,
            metadata.ChangedSeconds,
            metadata.ChangedNanoseconds);
    }

    private static void ValidateRelativeSegments(IEnumerable<string> segments)
    {
        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment)
                || segment is "." or ".."
                || segment.Contains(Path.DirectorySeparatorChar)
                || segment.Contains(Path.AltDirectorySeparatorChar)
                || segment.Contains('\0'))
            {
                throw new SafeFileOpenException("The input path contains an unsafe component.");
            }
        }
    }

    private static void EnsureLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Safe input snapshotting requires Linux statx support.");
        }
    }

    private static SafeFileOpenException NativeFailure(string message) =>
        new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    private static void TryDeleteOwnedSnapshot(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class OpenedFile(FileStream stream, Identity identity) : IDisposable
    {
        public FileStream Stream { get; } = stream;

        public Identity Identity { get; } = identity;

        public void Dispose() => Stream.Dispose();
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint LinkCount;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(96)] public long ChangedSeconds;
        [FieldOffset(104)] public uint ChangedNanoseconds;
        [FieldOffset(112)] public long ModifiedSeconds;
        [FieldOffset(120)] public uint ModifiedNanoseconds;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute", Justification = "LibraryImport generation requires unsafe blocks, which this project intentionally disables.")]
    [SuppressMessage("Security", "CA2101:Specify marshaling for P/Invoke string arguments", Justification = "Linux libc path arguments are explicitly marshaled as UTF-8.")]
    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute", Justification = "LibraryImport generation requires unsafe blocks, which this project intentionally disables.")]
    [SuppressMessage("Security", "CA2101:Specify marshaling for P/Invoke string arguments", Justification = "Linux libc path arguments are explicitly marshaled as UTF-8.")]
    [DllImport("libc", EntryPoint = "openat", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int OpenAt(
        int directoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute", Justification = "LibraryImport generation requires unsafe blocks, which this project intentionally disables.")]
    [SuppressMessage("Security", "CA2101:Specify marshaling for P/Invoke string arguments", Justification = "Linux libc path arguments are explicitly marshaled as UTF-8.")]
    [DllImport("libc", EntryPoint = "statx", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Statx(
        int directoryDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        ref StatxBuffer buffer);
}

public sealed class SafeFileOpenException : IOException
{
    public SafeFileOpenException(string message)
        : base(message)
    {
    }

    public SafeFileOpenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
