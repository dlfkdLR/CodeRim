using System.Security.Cryptography;
using System.Text.Json;

namespace CodeRim.Core.Services;

public enum UpdateExecutionStatus
{
    Applied, AppliedCleanupPending, RolledBack, RecoveryRequired, TimedOutRecoveryRequired,
    CancelledRecoveryRequired, CancelledBeforeStart, SigningNotConfigured, UnsupportedPlatform,
    UnmanagedInstallation, UnsafeLocation, PublisherRejected, PackageRejected, InvalidRequest, Prepared, ParentStillRunning
}

/// <summary>No status implies publisher authentication unless the signed worker actually completed it.</summary>
public sealed record UpdateExecutionResult(UpdateExecutionStatus Status, string? OperationId = null)
{
    public string Message => Status switch
    {
        UpdateExecutionStatus.Prepared => "The signed update is verified and ready. Restart CodeRim to install it.",
        UpdateExecutionStatus.ParentStillRunning => "CodeRim did not exit in time. The previous installation was not replaced.",
        UpdateExecutionStatus.Applied => "The verified update was installed.",
        UpdateExecutionStatus.AppliedCleanupPending => "The verified update is installed; old binary cleanup requires recovery.",
        UpdateExecutionStatus.RolledBack => "The previous installation was retained or restored.",
        UpdateExecutionStatus.SigningNotConfigured => "Signed updates are not configured. Use the manual release download.",
        UpdateExecutionStatus.UnmanagedInstallation => "This legacy, unsigned, or portable installation cannot be updated automatically. Use the manual release download.",
        UpdateExecutionStatus.PublisherRejected => "Windows could not verify the pinned publisher. No automatic installation is authorized.",
        UpdateExecutionStatus.UnsafeLocation => "The per-user installation or update location is not protected.",
        UpdateExecutionStatus.UnsupportedPlatform => "This update worker requires Windows x64 or ARM64.",
        UpdateExecutionStatus.PackageRejected => "The release archive or signed payload manifest does not match.",
        UpdateExecutionStatus.CancelledBeforeStart => "The update was cancelled before starting.",
        UpdateExecutionStatus.TimedOutRecoveryRequired => "The update worker exceeded its deadline. Installation state must be recovered before retrying.",
        UpdateExecutionStatus.CancelledRecoveryRequired => "The update worker was stopped. Installation state must be recovered before retrying.",
        UpdateExecutionStatus.RecoveryRequired => "The update did not complete. Preserved transaction files require recovery.",
        _ => "The update request is invalid."
    };
}

/// <summary>Arguments for an already authenticated worker. This request grants no permission to execute unsigned/downloaded code.</summary>
public sealed class UpdateWorkerInvocation
{
    private UpdateWorkerInvocation(string operationId, string[] arguments) { OperationId = operationId; Arguments = Array.AsReadOnly(arguments); }
    public string OperationId { get; }
    public IReadOnlyList<string> Arguments { get; }
    public static UpdateWorkerInvocation ForDownloadedPackage(DownloadedReleasePackage downloaded)
    {
        ArgumentNullException.ThrowIfNull(downloaded);
        if (!Path.IsPathFullyQualified(downloaded.Path)) throw new ArgumentException("An absolute downloaded package path is required.", nameof(downloaded));
        var id = Guid.NewGuid().ToString("N"); var request = UpdateWorkerRequest.From(downloaded, id); request.Validate();
        return new(id, ["apply", JsonSerializer.Serialize(request), downloaded.Path]);
    }
    public static UpdateWorkerInvocation ForRecovery(string operationId)
    {
        if (!Guid.TryParseExact(operationId, "N", out var id) || id.ToString("N") != operationId) throw new ArgumentException("An exact update operation id is required.", nameof(operationId));
        return new(operationId, ["recover", operationId]);
    }
}

// Public release metadata only; neither this request nor its digest authenticates a publisher.
internal sealed record UpdateWorkerRequest(string OperationId, long ReleaseId, long AssetId, string Version, string Architecture, long Size, string Sha256)
{
    internal static UpdateWorkerRequest From(DownloadedReleasePackage downloaded, string operationId) =>
        new(operationId, downloaded.Package.ReleaseId, downloaded.Package.AssetId, downloaded.Package.Version.ToString(3),
            downloaded.Package.Architecture, downloaded.Package.Size, downloaded.Package.Sha256);
    internal void Validate()
    {
        if (!Guid.TryParseExact(OperationId, "N", out var id) || id.ToString("N") != OperationId || ReleaseId <= 0 || AssetId <= 0
            || !System.Version.TryParse(Version, out var version) || version.Build < 0 || version.Revision >= 0 || version.ToString(3) != Version
            || Architecture is not ("x64" or "arm64") || Size is < 1 or > ReleasePackageDownload.MaximumPackageBytes || !PublisherPin.IsHash(Sha256))
            throw new InvalidDataException("Invalid update identity.");
    }
    internal static UpdateWorkerRequest Parse(string text)
    {
        if (text.Length > 4096) throw new InvalidDataException("Update request exceeds its limit.");
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4 });
        InstallPayloadManifest.ExactObject(document.RootElement, "OperationId", "ReleaseId", "AssetId", "Version", "Architecture", "Size", "Sha256");
        var request = document.RootElement.Deserialize<UpdateWorkerRequest>() ?? throw new InvalidDataException("Missing update request.");
        request.Validate(); return request;
    }
}

internal sealed class PublisherPin
{
    private readonly byte[] digest;
    private PublisherPin(byte[] digest) => this.digest = digest;
    // Only the worker entry assembly's compile-time metadata is used by the production entry point.
    internal static PublisherPin? FromBuildMetadata(IEnumerable<string?> values)
    {
        var pins = values.ToArray();
        if (pins.Length == 0 || pins.Length == 1 && string.IsNullOrEmpty(pins[0])) return null;
        if (pins.Length != 1 || !IsHash(pins[0])) throw new InvalidDataException("Invalid compiled publisher pin.");
        return new(Convert.FromHexString(pins[0]!));
    }
    internal bool Matches(ReadOnlySpan<byte> spki) => CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(spki));
    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
