using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Terebi.Web;

/// <summary>
/// Talks to the File Transformation plugin (IAmParadox27) purely through reflection, because every
/// Jellyfin plugin lives in its own AssemblyLoadContext and a compile-time reference would load a
/// second copy of its assembly. We hand it a delegate that rewrites index.html in memory.
/// </summary>
public static class FileTransformationBridge
{
    private const string AssemblyName = "Jellyfin.Plugin.FileTransformation";
    private const string PluginTypeName = "Jellyfin.Plugin.FileTransformation.FileTransformationPlugin";
    private const string WriteServiceTypeName = "Jellyfin.Plugin.FileTransformation.Library.IWebFileTransformationWriteService";
    private const string DelegateTypeName = "Jellyfin.Plugin.FileTransformation.Library.TransformFile";

    public static bool IsInstalled() => FindAssembly() is not null;

    /// <summary>
    /// Registers (or re-registers) a transformation of the given file pattern.
    /// </summary>
    /// <param name="id">Stable id of our transformation.</param>
    /// <param name="fileNamePattern">Regex matched against the requested web path, e.g. "index.html".</param>
    /// <param name="handler">Static method with signature Task (string path, Stream contents).</param>
    /// <param name="serviceProvider">Host service provider used to resolve the write service.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>True when registered.</returns>
    public static bool Register(Guid id, string fileNamePattern, MethodInfo handler, IServiceProvider serviceProvider, ILogger logger)
    {
        try
        {
            var assembly = FindAssembly();
            if (assembly is null)
            {
                logger.LogInformation("Terebi: File Transformation plugin not found; web UI features are off.");
                return false;
            }

            var writeServiceType = assembly.GetType(WriteServiceTypeName, throwOnError: false);
            var delegateType = assembly.GetType(DelegateTypeName, throwOnError: false);
            if (writeServiceType is null || delegateType is null)
            {
                logger.LogWarning("Terebi: File Transformation plugin has an unexpected API; web UI features are off.");
                return false;
            }

            var writeService = serviceProvider.GetService(writeServiceType);
            if (writeService is null)
            {
                logger.LogWarning("Terebi: File Transformation write service is not registered in the host container; web UI features are off.");
                return false;
            }

            var callback = Delegate.CreateDelegate(delegateType, handler);
            var method = writeServiceType.GetMethod("UpdateTransformation") ?? writeServiceType.GetMethod("AddTransformation");
            if (method is null)
            {
                logger.LogWarning("Terebi: File Transformation write service has no registration method; web UI features are off.");
                return false;
            }

            method.Invoke(writeService, [id, fileNamePattern, callback]);
            logger.LogInformation("Terebi: registered {Pattern} transformation with File Transformation.", fileNamePattern);
            return true;
        }
        catch (Exception ex) when (ex is TargetInvocationException or ArgumentException or InvalidOperationException)
        {
            logger.LogError(ex, "Terebi: registering with File Transformation failed.");
            return false;
        }
    }

    public static void Unregister(Guid id, IServiceProvider serviceProvider, ILogger logger)
    {
        try
        {
            var writeServiceType = FindAssembly()?.GetType(WriteServiceTypeName, throwOnError: false);
            var writeService = writeServiceType is null ? null : serviceProvider.GetService(writeServiceType);
            writeServiceType?.GetMethod("RemoveTransformation")?.Invoke(writeService, [id]);
        }
        catch (Exception ex) when (ex is TargetInvocationException or ArgumentException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Terebi: unregistering from File Transformation failed (ignored).");
        }
    }

    private static Assembly? FindAssembly()
    {
        // Prefer the copy that actually owns a live plugin instance.
        foreach (var context in AssemblyLoadContext.All)
        {
            foreach (var assembly in context.Assemblies)
            {
                if (!string.Equals(assembly.GetName().Name, AssemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var instance = assembly.GetType(PluginTypeName, throwOnError: false)?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (instance is not null)
                {
                    return assembly;
                }
            }
        }

        return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, AssemblyName, StringComparison.OrdinalIgnoreCase));
    }
}
