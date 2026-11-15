using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Phaket.Core.Platforms;

// Session directories and token files must be private BEFORE any secret is written into them,
// not made private afterwards (PAK-65).
internal static class SessionSecurity
{
    const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

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
        else
        {
            try { File.SetUnixFileMode(path, PrivateDirectory); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        }
    }

    public static FileStream CreatePrivateFile(string path)
    {
        // FileMode.CreateNew: the file is created empty, then restricted, only then written.
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        if (OperatingSystem.IsWindows()) Restrict(path, isDirectory: false);
        else
        {
            try { File.SetUnixFileMode(path, PrivateFile); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        }
        return stream;
    }

    // shortcut: needs NTFS. On a non-NTFS volume, or when the platform refuses the rewrite, the
    // inherited user-profile ACL (user/SYSTEM/Administrators) stands. Verify on a Windows machine.
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
            or NotSupportedException) { }
    }
}
