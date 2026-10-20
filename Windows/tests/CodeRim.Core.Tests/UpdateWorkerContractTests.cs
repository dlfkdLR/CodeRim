using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Services;

namespace CodeRim.Core.Tests;

public sealed class UpdateWorkerContractTests
{
    [Fact]
    public void AbsentPinCannotAuthorizeAPublisher()
    {
        Assert.Null(PublisherPin.FromBuildMetadata([])); Assert.Null(PublisherPin.FromBuildMetadata([null])); Assert.Null(PublisherPin.FromBuildMetadata([""]));
        var bytes = Encoding.UTF8.GetBytes("public SPKI fixture bytes, not a certificate"); var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var pin = PublisherPin.FromBuildMetadata([hash]); Assert.NotNull(pin); Assert.True(pin.Matches(bytes)); Assert.False(pin.Matches([1, 2, 3]));
        Assert.Throws<InvalidDataException>(() => PublisherPin.FromBuildMetadata([hash, hash]));
    }
    [Theory]
    [InlineData("true")][InlineData("unsigned")][InlineData("*")][InlineData(" ")][InlineData("a")]
    public void InvalidCompiledPinFailsClosed(string value) => Assert.Throws<InvalidDataException>(() => PublisherPin.FromBuildMetadata([value]));
    [Fact]
    public void PublicPackageBindingRoundTripsWithoutBecomingPublisherAuthentication()
    {
        var request = ValidRequest(); var decoded = UpdateWorkerRequest.Parse(JsonSerializer.Serialize(request)); Assert.Equal(request, decoded);
    }
    [Theory]
    [InlineData("OperationId", "../bad")][InlineData("Version", "2.1")][InlineData("Version", "02.1.0")]
    [InlineData("Version", "2.1.0.0")][InlineData("Version", "2.1.0-preview")][InlineData("Architecture", "x86")]
    [InlineData("Sha256", "bad")][InlineData("Sha256", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void InvalidIdentityIsRejected(string property, string value)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(ValidRequest()))!; node[property] = value;
        Assert.Throws<InvalidDataException>(() => UpdateWorkerRequest.Parse(node.ToJsonString()));
    }
    [Theory]
    [InlineData("Size", 0)][InlineData("Size", 536870913)][InlineData("ReleaseId", 0)][InlineData("AssetId", -1)]
    public void InvalidAssetBoundsAreRejected(string property, long value)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(ValidRequest()))!; node[property] = value;
        Assert.Throws<InvalidDataException>(() => UpdateWorkerRequest.Parse(node.ToJsonString()));
    }
    [Theory]
    [InlineData("{\"Status\":0,\"OperationId\":null}")][InlineData("{\"Status\":999,\"OperationId\":null}")]
    [InlineData("{\"Status\":6,\"OperationId\":\"../bad\"}")][InlineData("{\"Status\":6,\"OperationId\":null,\"Trusted\":true}")]
    [InlineData("{\"Status\":6,\"Status\":0,\"OperationId\":null}")]
    public void WorkerCannotReportUnboundSuccessOrUnknownFields(string json) => Assert.Throws<InvalidDataException>(() => UpdateWorkerSupervisor.ParseResult(json));
    [Fact]
    public void DistinctStatesHaveDistinctCallerMessages()
    {
        var states = new[] { UpdateExecutionStatus.Applied, UpdateExecutionStatus.AppliedCleanupPending, UpdateExecutionStatus.RolledBack,
            UpdateExecutionStatus.TimedOutRecoveryRequired, UpdateExecutionStatus.UnmanagedInstallation, UpdateExecutionStatus.SigningNotConfigured };
        Assert.Equal(states.Length, states.Select(status => new UpdateExecutionResult(status).Message).Distinct().Count());
    }
    [Fact]
    public void SignedManifestMustExcludeSelfAndIncludeBothProducts()
    {
        var manifest = InstallFixture.Manifest("2.2.1", "x64", new() { ["CodeRim.exe"] = [1], ["CodeRimCLI.exe"] = [2] });
        var bound = SignedInstallPayload.BindManifest(manifest.ToJson(), "x64", 3, new string('a', 64));
        Assert.Equal(3, bound.Files.Count); Assert.Equal(3, bound.Files.Single(file => file.Path == SignedInstallPayload.WorkerName).Size);
        Assert.Throws<InvalidDataException>(() => SignedInstallPayload.BindManifest(bound.ToJson(), "x64", 3, new string('a', 64)));
        Assert.Throws<InvalidDataException>(() => SignedInstallPayload.BindManifest(manifest.ToJson(), "arm64", 3, new string('a', 64)));
        Assert.Throws<InvalidDataException>(() => SignedInstallPayload.BindManifest(InstallFixture.Manifest().ToJson(), "arm64", 3, new string('a', 64)));
    }
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(4)]
    public void CopyRejectsUnexpectedLength(int length)
    {
        using var input = new MemoryStream(new byte[length]); using var output = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => SignedInstallPayload.CopyAndHash(input, output, 3, Convert.ToHexStringLower(SHA256.HashData(new byte[3]))));
        Assert.True(output.Length <= 3);
    }
    [Fact]
    public void CopyRejectsTamperAndAcceptsOnlyExactBytes()
    {
        var bytes = new byte[] { 1, 2, 3 }; var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var good = new MemoryStream(bytes); using var output = new MemoryStream(); SignedInstallPayload.CopyAndHash(good, output, bytes.Length, hash); Assert.Equal(bytes, output.ToArray());
        using var bad = new MemoryStream([1, 2, 4]); Assert.Throws<InvalidDataException>(() => SignedInstallPayload.CopyAndHash(bad, Stream.Null, bytes.Length, hash));
    }
    [Fact]
    public void InvocationPreservesU1AssetIdentityAndProvidesRecoveryId()
    {
        var package = new ReleasePackage(11, 22, new Version(2, 2, 1), "x64", 123, new string('a', 64), new Uri("https://github.com/dlfkdLR/CodeRim/releases/download/v2.2.1/CodeRim-Windows-2.2.1-x64.zip"));
        var invocation = UpdateWorkerInvocation.ForDownloadedPackage(new(package, Path.GetFullPath("synthetic-download.zip")));
        var request = UpdateWorkerRequest.Parse(invocation.Arguments[1]);
        Assert.Equal(invocation.OperationId, request.OperationId); Assert.Equal(package.AssetId, request.AssetId); Assert.Equal(package.ReleaseId, request.ReleaseId);
        Assert.Equal(package.Sha256, request.Sha256); Assert.Equal(package.Size, request.Size);
        Assert.Equal(new[] { "recover", invocation.OperationId }, UpdateWorkerInvocation.ForRecovery(invocation.OperationId).Arguments);
        Assert.Throws<ArgumentException>(() => UpdateWorkerInvocation.ForRecovery("../bad"));
    }
    [Fact]
    public void ResultMustMatchRequestedOperation()
    {
        var result = new UpdateExecutionResult(UpdateExecutionStatus.Applied, "11111111111111111111111111111111");
        Assert.Equal(UpdateExecutionStatus.RecoveryRequired, UpdateWorkerSupervisor.BindResult(result, ValidRequest().OperationId).Status);
        var timeout = UpdateWorkerSupervisor.BindResult(new(UpdateExecutionStatus.TimedOutRecoveryRequired), ValidRequest().OperationId);
        Assert.Equal(ValidRequest().OperationId, timeout.OperationId); Assert.Equal(UpdateExecutionStatus.TimedOutRecoveryRequired, timeout.Status);
    }
    internal static UpdateWorkerRequest ValidRequest() => new("0123456789abcdef0123456789abcdef", 11, 22, "2.2.1", "x64", 123, new string('a', 64));
}
