using System;
using System.IO;
using System.Reflection;

namespace BimOnNavisPlugin
{
    // No Autodesk/shared types on the module initializer's declaring type.
    internal static class AssemblyBootstrap
    {
        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void Initialize()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                var name = new AssemblyName(args.Name).Name + ".dll";
                var path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, name);
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            Log("module_loaded");
        }
        internal static void Log(string message)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BimOnAI", "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "navis-startup-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".log"), DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
