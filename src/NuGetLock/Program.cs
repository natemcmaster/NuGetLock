using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Extensions.CommandLineUtils;
using NuGet.Common;
using NuGet.ProjectModel;

namespace NuGetLock
{
    public class Program
    {
        public static int Main(string[] args)
        {
            var app = new CommandLineApplication()
            {
                Name = "nuget-lock",
                Description = "Produces a lockfile for NuGet for repeatable restores"
            };

            app.HelpOption("-h|--help");
            // TODO support invoking on a solution or multiple projects
            var project = app.Option("-p|--project <PATH>", "The path to the MSBuild project to lock", CommandOptionType.SingleValue);

            app.OnExecute(() =>
            {
                string projectDir;
                if (project.HasValue())
                {
                    var val = Path.GetFullPath(project.Value());

                    projectDir = Directory.Exists(val)
                        ? val
                        : Path.GetDirectoryName(val);
                }
                else
                {
                    projectDir = Directory.GetCurrentDirectory();
                }

                // naive. Might be somewhere else depending of BaseIntermediateOutputPath
                var assetsFile = Path.Combine(projectDir, "obj", "project.assets.json");

                return GenerateLockFile(projectDir, assetsFile)
                    ? 0
                    : 1;
            });

            return app.Execute(args);
        }


        // These are warnings about changes NuGet may make to dependency versions during restore,
        // such as bumping versions, downgrading, etc.
        private static NuGetLogCode[] FatalErrors =
            {
                NuGetLogCode.NU1101,
                NuGetLogCode.NU1102,
                NuGetLogCode.NU1103,
                NuGetLogCode.NU1105,
                NuGetLogCode.NU1601,
                NuGetLogCode.NU1602,
                NuGetLogCode.NU1603,
                NuGetLogCode.NU1604,
                NuGetLogCode.NU1605,
                NuGetLogCode.NU1607,
            };


        private static bool GenerateLockFile(string dir, string assetsFilePath)
        {
            if (!File.Exists(assetsFilePath))
            {
                // TODO auto-restore?
                Console.Error.WriteLine("Could not find project.assets.json. Make sure to run 'dotnet restore' first before locking the file");
                return false;
            }

            var lockFilePath = Path.Combine(dir, "packages.lock.props");
            var assetsFile = LockFileUtilities.GetLockFile(assetsFilePath, NullLogger.Instance);

            var proj = new XElement("Project");
            var doc = new XDocument(proj);

            // details
            var props = new XElement("PropertyGroup");
            props.Add(new XElement("MSBuildAllProjects", "$(MSBuildAllProjects);$(MSBuildThisFileFullPath)"));
            props.Add(new XElement("NuGetLockFileImported", "true"));
            // force NuGet to error out if there are issues
            props.Add(new XElement("WarningsAsErrors", "$(WarningsAsErrors)" + FatalErrors.Aggregate(string.Empty, (sum, piece) => $"{sum};{piece}")));
            proj.Add(props);

            // clear all package reference items
            proj.Add(new XElement("ItemGroup",
                new XElement("PackageReference", new XAttribute("Remove", "@(PackageReference)"))));

            // also naive. Multiple targets make exist if there are runtime identifiers
            foreach (var target in assetsFile.Targets.OrderBy(t => t.TargetFramework.GetShortFolderName()))
            {
                var itemGroup = new XElement("ItemGroup", new XAttribute("Condition", $"'$(TargetFramework)' == '{target.TargetFramework.GetShortFolderName()}'"));
                proj.Add(itemGroup);

                foreach (var library in target.Libraries.OrderBy(l => l.Name).Where(l => !l.Type.Equals("project", StringComparison.Ordinal)))
                {
                    // TODO add IsImplicitlyDefined=true packages, e.g. Microsoft.NETCore.App
                    // TODO re-add PrivateAssets, ExcludeAssets, etc. where appropriate
                    var reference = new XElement("PackageReference", new XAttribute("Include", library.Name), new XAttribute("Version", library.Version.ToNormalizedString()));
                    itemGroup.Add(reference);
                }

            }

            // TODO lock sources

            doc.Save(lockFilePath);
            Console.WriteLine($"Generated lock file: {lockFilePath}");
            return true;
        }
    }
}
