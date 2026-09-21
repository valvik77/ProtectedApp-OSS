using System.Runtime.InteropServices;
using System.Text;

namespace ProtectedApp.Shared;

/// <summary>
/// Full-path normalization for text that someone else controls -- a process image path, a
/// command line, a string sent over the pipe -- inside the SYSTEM service.
/// <see cref="Path.GetFullPath(string)"/> expands 8.3 aliases by itself whenever the string
/// contains a "~". That reads the file system, and for a UNC path it contacts the server: about
/// 21 s of blocking when it does not answer, and an authentication attempt with the computer
/// account when it does. A process started from a share, or a Gate started with a crafted
/// argument, is enough to trigger it. These methods never reach the network.
/// </summary>
public static class SafePath
{
    private const char Placeholder = '';
    private const uint DriveFixed = 3;

    /// <summary>
    /// The full path, with an existing 8.3 alias on a fixed local drive resolved to its long
    /// name; a path anywhere else is normalized as written.
    /// </summary>
    public static string GetFullPath(string path, string? basePath = null) =>
        Canonicalize(GetLexicalFullPath(path, basePath));

    /// <summary>Only normalizes ".", ".." and separators; never reads the file system.</summary>
    public static string GetLexicalFullPath(string path, string? basePath = null)
    {
        // Without a "~" .NET does not look anything up, so hiding it keeps this purely lexical.
        var hidden = path.Replace('~', Placeholder);
        var full = basePath is null
            ? Path.GetFullPath(hidden)
            : Path.GetFullPath(hidden, basePath.Replace('~', Placeholder));
        return full.Replace(Placeholder, '~');
    }

    /// <summary>Whether <paramref name="fullPath"/> is on a fixed local disk (never a share).</summary>
    public static bool IsOnFixedLocalDrive(string fullPath) =>
        fullPath.Length >= 3 && char.IsAsciiLetter(fullPath[0]) && fullPath[1] == ':' && fullPath[2] == '\\'
        && GetDriveType(fullPath[..3]) == DriveFixed;

    // Only a path that can hold an alias -- it has a "~" -- on a fixed local drive is looked up.
    private static string Canonicalize(string fullPath)
    {
        if (fullPath.IndexOf('~') < 0 || !IsOnFixedLocalDrive(fullPath)) return fullPath;
        var capacity = 260;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetLongPathName(fullPath, buffer, buffer.Capacity);
            if (length == 0) return fullPath;
            if (length < buffer.Capacity) return buffer.ToString();
            // A too-small buffer returns the required size; include the terminator.
            capacity = checked((int)length + 1);
        }
        return fullPath;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, int longPathBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string rootPathName);
}
