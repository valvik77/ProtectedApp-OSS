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

    /// <summary>
    /// Whether a process command line runs <paramref name="targetPath"/>. A script named
    /// relatively (<c>python backup.py</c>) is resolved against the directory the process was
    /// launched from; without it (<c>null</c>) only full paths can match. The directory is a
    /// required argument so that no caller can forget it and silently miss relative names.
    /// </summary>
    public static bool CommandLineReferences(string? commandLine, string targetPath, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || !IsScript(targetPath)) return false;
        var target = SafePath.GetFullPath(targetPath).Replace('/', '\\');
        return ContainsFullPath(commandLine.Replace('/', '\\'), target)
            || IndexOfScriptArgument(WindowsCommandLine.Split(commandLine), target, workingDirectory, out _) >= 0;
    }

    /// <summary>
    /// Arguments for launching the protected script again after the user approved it, or
    /// null when the command line does not run it. Only the script itself, the arguments
    /// that follow it, and harmless interpreter options (<c>-u</c>, <c>-X utf8</c>,
    /// <c>py -3.11</c>) are kept. Anything before the script that could execute other code
    /// (<c>-c</c>, <c>-m</c>, shell wrappers) is dropped, so approving one script can never
    /// run a different payload.
    /// </summary>
    public static string? BuildCanonicalScriptArguments(string? commandLine, string targetPath,
        string? workingDirectory, bool keepInterpreterOptions)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || !IsScript(targetPath)) return null;
        var target = SafePath.GetFullPath(targetPath).Replace('/', '\\');
        var arguments = WindowsCommandLine.Split(commandLine);
        var index = IndexOfScriptArgument(arguments, target, workingDirectory, out var wholeArgument);
        if (index < 0) return ContainsFullPath(commandLine.Replace('/', '\\'), target) ? Quote(target) : null;

        // Rebuilt from the parsed arguments, so what the script receives is exactly what the
        // original command line meant, whatever quoting it used. The first argument is the
        // interpreter itself.
        var kept = new List<string>();
        for (var i = 1; keepInterpreterOptions && i < index; i++)
        {
            var kind = ClassifyInterpreterOption(arguments[i]);
            if (kind == InterpreterOption.Unsafe) continue;
            kept.Add(WindowsCommandLine.Quote(arguments[i]));
            if (kind == InterpreterOption.TakesValue && i + 1 < index)
                kept.Add(WindowsCommandLine.Quote(arguments[++i]));
        }
        kept.Add(Quote(target));
        // Arguments after the script are what the script itself would have received. They
        // are kept only when the script was a whole argument, not text inside another one.
        if (wholeArgument)
            for (var i = index + 1; i < arguments.Length; i++) kept.Add(WindowsCommandLine.Quote(arguments[i]));
        return string.Join(' ', kept);
    }

    public static bool IsPythonInterpreter(string executablePath)
    {
        var name = Path.GetFileName(executablePath);
        return name.StartsWith("python", StringComparison.OrdinalIgnoreCase)
            || name.Equals("py.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pyw.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsFullPath(string source, string target)
    {
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

    // Returns the index of the argument that names the script (after the program, which is
    // argument 0), or -1. wholeArgument is false when the script is only mentioned inside a
    // larger argument: code such as -c "exec(open('backup.py').read())" or a shell string such
    // as cmd /c "python backup.py".
    private static int IndexOfScriptArgument(string[] arguments, string target, string? workingDirectory,
        out bool wholeArgument)
    {
        wholeArgument = false;
        var baseDirectory = !string.IsNullOrWhiteSpace(workingDirectory)
            && Path.IsPathFullyQualified(workingDirectory) ? workingDirectory : null;
        for (var i = 1; i < arguments.Length; i++)
        {
            var text = arguments[i].Replace('/', '\\');
            if (text.StartsWith(@"\\?\", StringComparison.Ordinal)) text = text[4..];
            if (ResolvesTo(text, target, baseDirectory))
            {
                wholeArgument = true;
                return i;
            }
            foreach (var segment in text.Split(SegmentSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment.Length == text.Length) continue;
                if (ResolvesTo(segment, target, baseDirectory)) return i;
            }
        }
        return -1;
    }

    // Characters that end a file name inside code or a shell string. Path separators are
    // deliberately absent so that "sub\backup.py" stays one candidate.
    private static readonly char[] SegmentSeparators =
        [' ', '\t', '\'', '"', '=', '&', '|', ';', '<', '>', '(', ')', ','];

    private static bool ResolvesTo(string candidate, string target, string? workingDirectory)
    {
        if (candidate.Length == 0) return false;
        try
        {
            string full;
            if (Path.IsPathFullyQualified(candidate)) full = SafePath.GetFullPath(candidate);
            else if (workingDirectory is not null) full = SafePath.GetFullPath(candidate, workingDirectory);
            else return false;
            return full.Equals(target, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private enum InterpreterOption { Unsafe, Flag, TakesValue }

    // Options that only change how the interpreter runs the script. -c, -m, -i, - and shell
    // options are deliberately absent: they run code other than the approved script.
    private static InterpreterOption ClassifyInterpreterOption(string argument)
    {
        if (argument.Length < 2 || argument[0] != '-') return InterpreterOption.Unsafe;
        if (argument is "-X" or "-W") return InterpreterOption.TakesValue;
        if (argument[1] is 'X' or 'W') return InterpreterOption.Flag;
        if (argument.AsSpan(1).IndexOfAnyExcept("uBOsSEIqb") < 0) return InterpreterOption.Flag;
        // Windows py launcher selectors: -3, -3.11, -3-64, -V:3.12
        if (IsLauncherSelector(argument)) return InterpreterOption.Flag;
        return InterpreterOption.Unsafe;
    }

    private static bool IsLauncherSelector(string argument)
    {
        if (argument.StartsWith("-V:", StringComparison.Ordinal))
            return argument.Length > 3 && argument.AsSpan(3).IndexOfAnyExcept("0123456789.-_/*abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ") < 0;
        return argument.Length >= 2 && char.IsAsciiDigit(argument[1])
            && argument.AsSpan(1).IndexOfAnyExcept("0123456789.-") < 0;
    }

    private static string Quote(string path) => $"\"{path}\"";

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
