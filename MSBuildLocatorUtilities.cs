using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis;

namespace MermaidUmlGeneratorTool.Utilities
{
    public static class MSBuildLocatorUtilities
    {
        /// <summary>
        /// Registers the MSBuild instance and creates an MSBuildWorkspace.
        /// </summary>
        public static MSBuildWorkspace CreateWorkspace()
        {
            if (!MSBuildLocator.IsRegistered)
            {
                var instance = MSBuildLocator.QueryVisualStudioInstances().FirstOrDefault();
                if (instance == null)
                    throw new InvalidOperationException("No MSBuild instances found on this machine.");

                MSBuildLocator.RegisterInstance(instance);
            }

            var workspace = MSBuildWorkspace.Create();
            workspace.WorkspaceFailed += (sender, e) =>
            {
                Console.Error.WriteLine($"[Workspace Error] {e.Diagnostic}");
            };

            return workspace;
        }

        /// <summary>
        /// Loads a Roslyn Project from a given .csproj path using MSBuildWorkspace.
        /// </summary>
        /// <param name="workspace">An initialized MSBuildWorkspace</param>
        /// <param name="projectPath">Full path to the .csproj file</param>
        public static async Task<Project> GetProjectFromPath(MSBuildWorkspace workspace, string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
                throw new FileNotFoundException("Project file not found", projectPath);

            var project = await workspace.OpenProjectAsync(projectPath);
            if (project == null)
                throw new InvalidOperationException($"Unable to load project from path: {projectPath}");

            return project;
        }
    }
}