using System.Reflection;
using System.Text.Json;
using CodeRim.Core.Services;

if (OperatingSystem.IsWindows() && args.Length == 3 && args[0] == MsiUpdateExecution.EntryArgument)
{
    try { Console.WriteLine(JsonSerializer.Serialize(await MsiUpdateExecution.ExecuteAsync(args[1], args[2]))); return 0; }
    catch (Exception error) when (error is not OutOfMemoryException) { Console.WriteLine("Installer update validation failed."); return 1; }
}

UpdateExecutionResult result;
try
{
    var values = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
        .Where(attribute => attribute.Key == "CodeRimPublisherSpkiSha256").Select(attribute => attribute.Value);
    var pin = PublisherPin.FromBuildMetadata(values);
    if (pin is null) result = new(UpdateExecutionStatus.SigningNotConfigured);
    else if (!OperatingSystem.IsWindows()) result = new(UpdateExecutionStatus.UnsupportedPlatform);
    else if (args.Length is < 2 or > 4 || args.Any(argument => argument.Length > 32768)) result = new(UpdateExecutionStatus.InvalidRequest);
    else
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Missing worker image.");
        if (args[0] == "--child") result = !WindowsUpdateWorker.IsContained() ? new(UpdateExecutionStatus.InvalidRequest)
            : args[1] is "install" or "recover-install" ? WindowsInitialInstall.Execute(self, args[1..], pin)
            : WindowsUpdateWorker.Execute(args[1..], pin, self);
        else if (args[0] == "--bootstrap-recover-install" && args.Length == 2) result = await WindowsInitialInstall.BootstrapAsync(self, args[1], pin, recover: true);
        else if (args[0] == "--bootstrap-install" && args.Length == 2) result = await WindowsInitialInstall.BootstrapAsync(self, args[1], pin);
        else if (args[0] == "handoff-recover" && args.Length == 2) result = await UpdateRecovery.RunWorkerAsync(self, args[1], pin);
        else if (args[0] == "handoff") result = await UpdateHandoff.RunAsync(self, args, pin);
        else result = await UpdateWorkerSupervisor.RunAsync(self, args, pin, CancellationToken.None);
    }
}
catch (Exception exception) when (UpdateBootstrap.Expected(exception))
{ result = new(UpdateExecutionStatus.RecoveryRequired); }
Console.WriteLine(JsonSerializer.Serialize(new { result.Status, result.OperationId }));
return 0;
