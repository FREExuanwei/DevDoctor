namespace DevDoctor.Core;

public sealed record CppInstallation(IReadOnlyList<string> Compilers, IReadOnlyList<string> WindowsSdks, IReadOnlyList<string> CMakePaths);
public interface IToolchainInstallationReader
{
    CppInstallation Inspect(string installationPath);
}
