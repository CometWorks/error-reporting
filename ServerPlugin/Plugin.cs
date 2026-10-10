using System;
using VRage.FileSystem;
using VRage.Plugins;

#if !LOCAL_BUILD
using System.Reflection;
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
#endif

namespace ServerPlugin;

/// <summary>Required local diagnostic capture; external sharing belongs to Quasar.</summary>
public sealed class Plugin : IPlugin
{
    public void Init(object gameInstance) => IncidentCapture.Start(MyFileSystem.UserDataPath ?? AppContext.BaseDirectory);
    public void Update() { }
    // Keep process-wide capture active through shutdown, including failures in other plugins' disposal.
    public void Dispose() { }
}
