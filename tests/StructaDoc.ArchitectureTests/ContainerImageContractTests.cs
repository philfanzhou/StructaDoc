namespace StructaDoc.ArchitectureTests;

public sealed class ContainerImageContractTests
{
    private static readonly string Dockerfile = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Dockerfile"));
    private static readonly string PowerShellBuildScript = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "scripts", "build-container.ps1"));
    private static readonly string BashBuildScript = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "scripts", "build-container.sh"));

    [Fact]
    public void Runtime_image_contains_libreoffice_without_python_or_sdk()
    {
        Assert.Contains(
            "FROM ${DOTNET_REGISTRY}/sdk:${DOTNET_VERSION}-noble AS build",
            Dockerfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "FROM ${DOTNET_REGISTRY}/aspnet:${DOTNET_VERSION}-noble AS runtime",
            Dockerfile,
            StringComparison.Ordinal);
        Assert.Contains("libreoffice-writer-nogui", Dockerfile, StringComparison.Ordinal);
        Assert.Contains("libreoffice-calc-nogui", Dockerfile, StringComparison.Ordinal);
        Assert.Contains("libreoffice-impress-nogui", Dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("python3-uno", Dockerfile, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("! command -v python3", Dockerfile, StringComparison.Ordinal);
        Assert.Contains("dotnet --list-sdks", Dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("COPY --from=build /usr/share/dotnet", Dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_image_uses_one_non_root_host_entrypoint_and_persistent_data_volume()
    {
        Assert.Contains("USER ${APP_UID}", Dockerfile, StringComparison.Ordinal);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"StructaDoc.Host.dll\"]", Dockerfile, StringComparison.Ordinal);
        Assert.Contains("VOLUME [\"/data\"]", Dockerfile, StringComparison.Ordinal);
        Assert.Contains("/health/ready", Dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("supervisord", Dockerfile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uvicorn", Dockerfile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_sources_are_explicit_and_network_selection_happens_before_docker_build()
    {
        Assert.Contains("ARG DOTNET_REGISTRY=mcr.microsoft.com/dotnet", Dockerfile, StringComparison.Ordinal);
        Assert.Contains("ARG NUGET_SOURCE=https://api.nuget.org/v3/index.json", Dockerfile, StringComparison.Ordinal);
        Assert.Contains("ARG APT_MIRROR", Dockerfile, StringComparison.Ordinal);
        Assert.Contains("ARG APT_PORTS_MIRROR", Dockerfile, StringComparison.Ordinal);
        Assert.Contains("--source \"${NUGET_SOURCE}\"", Dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("ipinfo", Dockerfile, StringComparison.OrdinalIgnoreCase);

        // The image is the only deployment unit, so the build scripts are the sole place that can
        // redirect a package source. Each overridable source must reach `docker build` explicitly.
        foreach (var buildArgument in new[]
                 {
                     "DOTNET_REGISTRY=",
                     "NUGET_SOURCE=",
                     "APT_MIRROR=",
                     "APT_PORTS_MIRROR=",
                 })
        {
            Assert.Contains(buildArgument, PowerShellBuildScript, StringComparison.Ordinal);
            Assert.Contains(buildArgument, BashBuildScript, StringComparison.Ordinal);
        }

        Assert.Contains("Test-BuildEndpoint", PowerShellBuildScript, StringComparison.Ordinal);
        Assert.Contains("probe_endpoint", BashBuildScript, StringComparison.Ordinal);
        Assert.Contains("https://repo.huaweicloud.com/repository/nuget/v3/index.json", PowerShellBuildScript, StringComparison.Ordinal);
        Assert.Contains("https://repo.huaweicloud.com/repository/nuget/v3/index.json", BashBuildScript, StringComparison.Ordinal);
        Assert.Contains("https://mirrors.tuna.tsinghua.edu.cn/ubuntu", PowerShellBuildScript, StringComparison.Ordinal);
        Assert.Contains("https://mirrors.tuna.tsinghua.edu.cn/ubuntu", BashBuildScript, StringComparison.Ordinal);
        Assert.Contains("'build'", PowerShellBuildScript, StringComparison.Ordinal);
        Assert.Contains("docker @dockerArguments", PowerShellBuildScript, StringComparison.Ordinal);
        Assert.Contains("docker build", BashBuildScript, StringComparison.Ordinal);
        Assert.Contains("--build-arg", PowerShellBuildScript, StringComparison.Ordinal);
        Assert.Contains("--build-arg", BashBuildScript, StringComparison.Ordinal);
    }
}
