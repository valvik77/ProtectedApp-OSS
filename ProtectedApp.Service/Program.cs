using ProtectedApp.Service;

GuardianConstants.EnsurePrivateStateDirectories();
DirectoryOpusCompatibility.DisablePageHeapIfInstalled();

if (args.Any(value => value.Equals("--remove-gates", StringComparison.OrdinalIgnoreCase)))
{
    ExecutionGateManager.RemoveAllManagedEntries();
    return;
}

if (args.Any(value => value.Equals("--restore-folders", StringComparison.OrdinalIgnoreCase)))
{
    var restoreOptions = new GuardianOptions(ResolveAppPath(args), DiagnosticMode: false);
    var restoreStore = new GuardianPolicyStore();
    var folderProtection = new FolderProtectionService(restoreStore, restoreOptions,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<FolderProtectionService>.Instance);
    folderProtection.RestoreAll();
    return;
}

if (args.Any(value => value.Equals("--health-watch", StringComparison.OrdinalIgnoreCase)))
{
    await GuardianHealthCheck.RunContinuouslyAsync(ResolveAppPath(args));
    return;
}

if (args.Any(value => value.Equals("--health-check", StringComparison.OrdinalIgnoreCase)))
{
    await GuardianHealthCheck.RunAsync(ResolveAppPath(args));
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = GuardianConstants.DisplayName);
builder.Services.AddSingleton(new GuardianOptions(ResolveAppPath(args),
    args.Any(value => value.Equals("--diagnostic-console", StringComparison.OrdinalIgnoreCase))));
builder.Services.AddSingleton<GuardianPolicyStore>();
builder.Services.AddSingleton<AuthenticationThrottle>();
builder.Services.AddSingleton<ExecutionGateManager>();
builder.Services.AddSingleton<FolderProtectionService>();
builder.Services.AddSingleton<GuardianEnforcer>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<FolderProtectionService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<GuardianEnforcer>());
builder.Services.AddHostedService<GuardianIpcServer>();
builder.Services.AddHostedService<TamperWebhookNotifier>();
await builder.Build().RunAsync();

static string ResolveAppPath(string[] args)
{
    var marker = Array.FindIndex(args, value => value.Equals("--app", StringComparison.OrdinalIgnoreCase));
    if (marker >= 0 && marker + 1 < args.Length) return Path.GetFullPath(args[marker + 1]);
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "ProtectedApp.exe"));
}
