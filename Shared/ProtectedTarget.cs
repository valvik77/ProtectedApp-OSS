namespace ProtectedApp.Shared;

public static class ProtectedTarget
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".exe", ".bat", ".py" };

    public static bool IsSupported(string path) =>
        !string.IsNullOrWhiteSpace(path) && SupportedExtensions.Contains(Path.GetExtension(path));

    public static bool IsScript(string path) =>
        Path.GetExtension(path) is var extension
        && (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".py", StringComparison.OrdinalIgnoreCase));

    public static bool IsPotentialScriptHost(string executablePath)
    {
        var name = Path.GetFileName(executablePath);
        return name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("py.exe", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("python", StringComparison.OrdinalIgnoreCase)
            || name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static bool CommandLineReferences(string? commandLine, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || !IsScript(targetPath)) return false;
        var target = Path.GetFullPath(targetPath).Replace('/', '\\');
        var source = commandLine.Replace('/', '\\');
        var offset = 0;
        while ((offset = source.IndexOf(target, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = offset == 0 ? '\0' : source[offset - 1];
            var end = offset + target.Length;
            var after = end == source.Length ? '\0' : source[end];
            if (IsArgumentBoundary(before) && IsArgumentBoundary(after)) return true;
            offset = end;
        }
        return false;
    }

    public static (string Executable, string Arguments) GetLaunchCommand(string targetPath)
    {
        var fullPath = Path.GetFullPath(targetPath);
        var extension = Path.GetExtension(fullPath);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return (fullPath, string.Empty);

        var commandProcessor = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            return (commandProcessor, $"/d /c call \"{fullPath}\"");
        if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase))
        {
            var pythonLauncher = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "py.exe");
            return File.Exists(pythonLauncher)
                ? (pythonLauncher, $"\"{fullPath}\"")
                : (commandProcessor, $"/d /c python \"{fullPath}\"");
        }
        throw new NotSupportedException($"El tipo de archivo {extension} no se puede proteger.");
    }

    private static bool IsArgumentBoundary(char value) =>
        value == '\0' || value == '"' || value == '\'' || value == '=' || value == '&'
        || value == '(' || value == ')' || char.IsWhiteSpace(value);
}
