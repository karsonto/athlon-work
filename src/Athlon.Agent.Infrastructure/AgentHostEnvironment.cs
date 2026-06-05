using System.Runtime.InteropServices;
using Athlon.Agent.Core;

namespace Athlon.Agent.Infrastructure;

public sealed class AgentHostEnvironment(IAppPathProvider paths) : IAgentHostEnvironment
{
    public bool IsWindows { get; } = OperatingSystem.IsWindows();

    public string OsDescription { get; } = RuntimeInformation.OSDescription;

    public string OsVersion { get; } = Environment.OSVersion.VersionString;

    public string UserName { get; } = Environment.UserName;

    public string UserDomainName { get; } = Environment.UserDomainName;

    public string MachineName { get; } = Environment.MachineName;

    public string UserProfilePath { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string SystemDirectory { get; } = Environment.SystemDirectory;

    public string ProcessArchitecture { get; } = RuntimeInformation.ProcessArchitecture.ToString();

    public string OsArchitecture { get; } = RuntimeInformation.OSArchitecture.ToString();

    public int ProcessorCount { get; } = Environment.ProcessorCount;

    public string AppDataDirectory { get; } = paths.RootPath;

    public string SkillsDirectory { get; } = paths.SkillsPath;
}
