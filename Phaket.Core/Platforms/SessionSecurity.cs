using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Phaket.Core.Omrl;

namespace Phaket.Core.Platforms;

// Session directories and token files must be private BEFORE any secret is written into them,
// not made private afterwards (PAK-65). Privacy that cannot be established is a refusal, not a
// warning: a token is never written where another account could read it (PAK-66, fail closed).
internal static class SessionSecurity
{
    const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    // Stands in for a filesystem that accepts chmod and ignores it (tests only). AsyncLocal, so a
    // test running in parallel with this one never sees another test's override.
    internal static readonly AsyncLocal<Action<string, UnixFileMode>?> SetModeOverride = new();

    // Creates (or reuses) the directory that holds per-run sessions and restricts it first, so the
    // brief mode gap while a session directory is created inside it is not reachable by others.
    public static void EnsurePrivateRoot(string root)
    {
        Directory.CreateDirectory(root);
        MakeDirectoryPrivate(root);
    }

    public static void MakeDirectoryPrivate(string path)
    {
        if (OperatingSystem.IsWindows()) Restrict(path, isDirectory: true);
        else Apply(path, PrivateDirectory);
    }

    public static FileStream CreatePrivateFile(string path)
    {
        // FileMode.CreateNew: the file is created empty, then restricted, only then written.
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            if (OperatingSystem.IsWindows()) Restrict(path, isDirectory: false);
            else Apply(path, PrivateFile);
        }
        catch
        {
            // A refused restriction must not leave the file behind: it was created with the umask,
            // which may be world-readable, and the caller never gets to write into it anyway.
            stream.Dispose();
            try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
        return stream;
    }

    // Order directory -> file -> content is unchanged: the mode is verified before any secret
    // byte is written.
    private static void Apply(string path, UnixFileMode privateMode)
    {
        if (!OperatingSystem.IsWindows())
        {
        try
        {
            (SetModeOverride.Value ?? File.SetUnixFileMode)(path, privateMode);
            // Whatever the mount still grants on top of the private mode is not ours to keep:
            // a chmod the filesystem silently ignored shows up here as a refusal. Only the rwx
            // bits are compared, so a setuid/setgid parent is not mistaken for a public one.
            const UnixFileMode AccessBits = (UnixFileMode)(512 - 1);
            var granted = File.GetUnixFileMode(path) & AccessBits;
            if ((granted & ~privateMode) != 0)
                throw new PhaketException(ErrorCodes.SessionPrivacyFailed, ("path", path),
                    ("detail", $"kept {granted} instead of {privateMode}"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or PlatformNotSupportedException or ArgumentException or NotSupportedException)
        {
            throw Refused(path, e);
        }
        }
    }

    // shortcut: needs NTFS. A volume without ACL support, or a rewrite the platform refuses,
    // cannot be made private, so the caller is stopped instead of inheriting the profile's
    // broader ACL. Verify on a Windows machine.
    [SupportedOSPlatform("windows")]
    static void Restrict(string path, bool isDirectory)
    {
        try
        {
            var user = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("no interactive user identity");
            if (isDirectory)
            {
                var acl = new DirectorySecurity();
                acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
                    AccessControlType.Allow));
                new DirectoryInfo(path).SetAccessControl(acl);
            }
            else
            {
                var acl = new FileSecurity();
                acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(acl);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException
            or System.Security.SecurityException or ArgumentException or InvalidOperationException
            or NotSupportedException)
        {
            throw Refused(path, e);
        }
    }

    private static PhaketException Refused(string path, Exception cause) =>
        new(ErrorCodes.SessionPrivacyFailed, ("path", path), ("detail", cause.Message));
}
