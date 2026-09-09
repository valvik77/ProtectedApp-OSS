using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace ProtectedApp.Service;

internal static class GuardianSystemDiagnostics
{
    public static GuardianTaskDiagnostic CheckHealthTask(string guardianExecutable, string appPath)
    {
        object? schedulerObject = null;
        object? folderObject = null;
        object? taskObject = null;
        object? definitionObject = null;
        object? actionsObject = null;
        object? principalObject = null;
        object? settingsObject = null;
        object? triggersObject = null;
        try
        {
            var expectedGuardianPath = Path.GetFullPath(guardianExecutable);
            var expectedArguments = BuildHealthWatchArguments(appPath);
            var schedulerType = Type.GetTypeFromProgID("Schedule.Service");
            if (schedulerType is null) return new(false, "La API del Programador de tareas no está disponible.");
            schedulerObject = Activator.CreateInstance(schedulerType);
            if (schedulerObject is null) return new(false, "No se pudo crear el cliente del Programador de tareas.");
            dynamic scheduler = schedulerObject;
            scheduler.Connect();
            folderObject = scheduler.GetFolder("\\");
            dynamic folder = folderObject;
            taskObject = folder.GetTask(GuardianConstants.HealthTaskName);
            dynamic task = taskObject;
            if (!(bool)task.Enabled) return new(false, "La tarea existe, pero está deshabilitada.");

            definitionObject = task.Definition;
            dynamic definition = definitionObject;
            principalObject = definition.Principal;
            dynamic principal = principalObject;
            if (!IsSystemPrincipal(principal))
                return new(false, "La tarea no se ejecuta como SYSTEM con el nivel requerido.");

            settingsObject = definition.Settings;
            dynamic settings = settingsObject;
            if (!HasExpectedSettings(settings))
                return new(false, "La tarea no conserva su configuración de ejecución y recuperación esperada.");

            actionsObject = definition.Actions;
            dynamic actions = actionsObject;
            if ((int)actions.Count != 1)
                return new(false, "La tarea debe tener exactamente una acción de supervisión.");
            object? actionObject = null;
            try
            {
                actionObject = actions.Item(1);
                dynamic action = actionObject;
                if (Convert.ToInt32(action.Type) != 0
                    || !PathsEqual(Convert.ToString(action.Path), expectedGuardianPath)
                    || !string.Equals((Convert.ToString(action.Arguments) ?? string.Empty).Trim(),
                        expectedArguments, StringComparison.OrdinalIgnoreCase))
                    return new(false, "La acción de la tarea no coincide con Guardian y --app esperados.");
            }
            catch (RuntimeBinderException)
            {
                return new(false, "La tarea no contiene una acción ejecutable válida.");
            }
            finally { ReleaseCom(actionObject); }

            triggersObject = definition.Triggers;
            dynamic triggers = triggersObject;
            if (!HasExpectedTriggers(triggers))
                return new(false, "La tarea no conserva los disparadores de arranque y repetición por minuto.");

            return new(true, "La tarea SYSTEM coincide con el principal, acción, configuración y disparadores esperados.");
        }
        catch (COMException ex)
        {
            return new(false, $"El Programador de tareas devolvió 0x{ex.HResult:X8}.");
        }
        catch (Exception ex) { return new(false, ex.Message); }
        finally
        {
            ReleaseCom(actionsObject);
            ReleaseCom(triggersObject);
            ReleaseCom(settingsObject);
            ReleaseCom(principalObject);
            ReleaseCom(definitionObject);
            ReleaseCom(taskObject);
            ReleaseCom(folderObject);
            ReleaseCom(schedulerObject);
        }
    }

    public static string? GetInstalledProtectionVersion()
    {
        try
        {
            return File.Exists(GuardianConstants.VersionPath)
                ? File.ReadAllText(GuardianConstants.VersionPath).Trim()
                : null;
        }
        catch { return null; }
    }

    public static bool TryRepairHealthTask(string guardianExecutable, string appPath, out string detail)
    {
        object? schedulerObject = null;
        object? folderObject = null;
        object? definitionObject = null;
        object? bootTriggerObject = null;
        object? recurringTriggerObject = null;
        object? actionObject = null;
        object? registeredTaskObject = null;
        try
        {
            var guardianPath = Path.GetFullPath(guardianExecutable);
            var resolvedAppPath = Path.GetFullPath(appPath);
            if (!File.Exists(guardianPath) || !File.Exists(resolvedAppPath))
            {
                detail = "No se pueden restaurar los archivos esperados de Guardian.";
                return false;
            }

            var schedulerType = Type.GetTypeFromProgID("Schedule.Service");
            if (schedulerType is null)
            {
                detail = "La API del Programador de tareas no está disponible.";
                return false;
            }
            schedulerObject = Activator.CreateInstance(schedulerType);
            if (schedulerObject is null)
            {
                detail = "No se pudo crear el cliente del Programador de tareas.";
                return false;
            }

            dynamic scheduler = schedulerObject;
            scheduler.Connect();
            folderObject = scheduler.GetFolder("\\");
            dynamic folder = folderObject;
            definitionObject = scheduler.NewTask(0);
            dynamic definition = definitionObject;
            definition.RegistrationInfo.Description = "Rearma las puertas, bloquea la sesión y reinicia ProtectedApp Guardian si se detiene.";
            definition.Principal.UserId = "SYSTEM";
            definition.Principal.LogonType = 5; // TASK_LOGON_SERVICE_ACCOUNT
            definition.Principal.RunLevel = 1; // TASK_RUNLEVEL_HIGHEST
            definition.Settings.Enabled = true;
            definition.Settings.StartWhenAvailable = true;
            definition.Settings.MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW
            definition.Settings.ExecutionTimeLimit = "PT0S";
            definition.Settings.RestartCount = 3;
            definition.Settings.RestartInterval = "PT1M";

            bootTriggerObject = definition.Triggers.Create(8); // TASK_TRIGGER_BOOT
            dynamic bootTrigger = bootTriggerObject;
            bootTrigger.Enabled = true;
            recurringTriggerObject = definition.Triggers.Create(1); // TASK_TRIGGER_TIME
            dynamic recurringTrigger = recurringTriggerObject;
            recurringTrigger.StartBoundary = DateTime.Now.AddMinutes(1).ToString("yyyy-MM-dd'T'HH:mm:ss");
            recurringTrigger.Repetition.Interval = "PT1M";
            recurringTrigger.Repetition.StopAtDurationEnd = false;
            recurringTrigger.Enabled = true;

            actionObject = definition.Actions.Create(0); // TASK_ACTION_EXEC
            dynamic action = actionObject;
            action.Path = guardianPath;
            action.Arguments = BuildHealthWatchArguments(resolvedAppPath);

            registeredTaskObject = folder.RegisterTaskDefinition(GuardianConstants.HealthTaskName,
                definitionObject, 6, "SYSTEM", null, 5, null); // TASK_CREATE_OR_UPDATE
            dynamic registeredTask = registeredTaskObject;
            registeredTask.Run(null);
            detail = "La tarea SYSTEM se registró de nuevo con la acción de supervisión esperada.";
            return CheckHealthTask(guardianPath, resolvedAppPath).Healthy;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
        finally
        {
            ReleaseCom(registeredTaskObject);
            ReleaseCom(actionObject);
            ReleaseCom(recurringTriggerObject);
            ReleaseCom(bootTriggerObject);
            ReleaseCom(definitionObject);
            ReleaseCom(folderObject);
            ReleaseCom(schedulerObject);
        }
    }

    internal static string BuildHealthWatchArguments(string appPath) =>
        $"--health-watch --app \"{Path.GetFullPath(appPath)}\"";

    private static bool IsSystemPrincipal(dynamic principal)
    {
        var userId = Convert.ToString(principal.UserId) ?? string.Empty;
        return (userId.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)
                || userId.Equals("S-1-5-18", StringComparison.OrdinalIgnoreCase))
            && Convert.ToInt32(principal.LogonType) == 5
            && Convert.ToInt32(principal.RunLevel) == 1;
    }

    private static bool HasExpectedSettings(dynamic settings) =>
        (bool)settings.Enabled
        && (bool)settings.StartWhenAvailable
        && Convert.ToInt32(settings.MultipleInstances) == 2
        && string.Equals(Convert.ToString(settings.ExecutionTimeLimit), "PT0S", StringComparison.OrdinalIgnoreCase)
        && Convert.ToInt32(settings.RestartCount) == 3
        && string.Equals(Convert.ToString(settings.RestartInterval), "PT1M", StringComparison.OrdinalIgnoreCase);

    private static bool HasExpectedTriggers(dynamic triggers)
    {
        if ((int)triggers.Count != 2) return false;
        var bootFound = false;
        var recurringFound = false;
        for (var index = 1; index <= (int)triggers.Count; index++)
        {
            object? triggerObject = null;
            try
            {
                triggerObject = triggers.Item(index);
                dynamic trigger = triggerObject;
                if (!(bool)trigger.Enabled) return false;
                var type = Convert.ToInt32(trigger.Type);
                if (type == 8) bootFound = true; // TASK_TRIGGER_BOOT
                else if (type == 1
                    && string.Equals(Convert.ToString(trigger.Repetition.Interval), "PT1M",
                        StringComparison.OrdinalIgnoreCase)
                    && !(bool)trigger.Repetition.StopAtDurationEnd)
                    recurringFound = true; // TASK_TRIGGER_TIME
                else return false;
            }
            catch (RuntimeBinderException) { return false; }
            finally { ReleaseCom(triggerObject); }
        }
        return bootFound && recurringFound;
    }

    private static bool PathsEqual(string? left, string? right)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
                && string.Equals(Path.GetFullPath(left.Trim().Trim('"')), Path.GetFullPath(right.Trim().Trim('"')),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}

internal sealed record GuardianTaskDiagnostic(bool Healthy, string Detail);
