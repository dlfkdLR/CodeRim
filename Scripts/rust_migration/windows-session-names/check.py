"""Compare WPF Settings session names against the pinned original view using disposable data."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile

here = Path(__file__).resolve().parent
root = here.parents[2]
p = argparse.ArgumentParser(description=__doc__)
p.add_argument("--reference-source", type=Path, required=True)
p.add_argument("--output", type=Path, required=True)
args = p.parse_args()
ref = (args.reference_source / "Windows/src/CodeRim.Windows/Views/UsagePane.cs").read_text(encoding="utf-8")
methods = ref[ref.index("    private string AnalyticsEntityName("):ref.index("    private string SessionListDetail(")]
env = dict(os.environ, DOTNET_TieredCompilation="0", DOTNET_CLI_TELEMETRY_OPTOUT="1")
with tempfile.TemporaryDirectory(prefix="coderim-session-names-", dir=root.parent) as folder:
    work = Path(folder).resolve()
    (work / "Program.cs").write_bytes((here / "Program.cs").read_bytes())
    (work / "Legacy.cs").write_text("""using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using System.Globalization;
sealed class Legacy(string provider)
{
    public sealed record Label(string? SessionTitle, string ProjectName);
    public sealed class Store { public Dictionary<string, Label> CodexAnalyticsLabels { get; } = new(StringComparer.Ordinal); }
    public Store store = new();
    public Dictionary<string, Label> analyticsMetadata = new(StringComparer.Ordinal);
    public string Name(IEnumerable<UsageEvent> events, string id) => AnalyticsEntityName(events, id, true);
    public string Project(IEnumerable<UsageEvent> events, string id) => AnalyticsEntityName(events, id, false);
    public string? Published(string id) => provider == "codex" && store.CodexAnalyticsLabels.GetValueOrDefault(id)?.SessionTitle is {} title
        ? title : analyticsMetadata.GetValueOrDefault(id)?.ProjectName is {Length: > 0} name ? name : null;
""" + methods + "}\n", encoding="utf-8")
    current = (root / "Windows/src/CodeRim.Windows/Views/UsagePane.cs").read_text(encoding="utf-8")
    current_methods = current[current.index("    private string? PublishedSessionName("):current.index("    private string SessionListDetail(")]
    legacy_source = (work / "Legacy.cs").read_text(encoding="utf-8")
    (work / "Current.cs").write_text(legacy_source.replace("sealed class Legacy(", "sealed class Current(")
                                 .replace(methods, current_methods), encoding="utf-8")
    (work / "LabelContract.csproj").write_text('''<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable><LangVersion>12</LangVersion><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
    <ItemGroup><ProjectReference Include="$(CodeRimFixtureSource)/Windows/src/CodeRim.Core/CodeRim.Core.csproj"/></ItemGroup></Project>''', encoding="utf-8")
    out = work / "artifacts"
    subprocess.run(["dotnet","build",work / "LabelContract.csproj","-c","Release","-m:1","-p:BuildInParallel=false",
                    "-p:CodeRimFixtureSource="+str(root),"-p:NuGetAudit=false","--artifacts-path",out],check=True,env=env)
    exe = out / "bin/LabelContract/release/LabelContract.dll"
    result = json.loads(subprocess.check_output(["dotnet",exe],env=env))
    args.output.write_text(json.dumps(result,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
