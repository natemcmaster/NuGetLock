using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.Extensions.CommandLineUtils;
using NuGet.Common;
using NuGet.LibraryModel;
using NuGet.Packaging;
using NuGet.ProjectModel;

namespace NuGetLock
{
    internal class Program
    {
        private const string LockFileName = "nuget.lock";

        private const int OK = 0;
        private const int Error = 1;

        public static int Main(string[] args)
        {
            var app = new CommandLineApplication()
            {
                Name = "dotnet nugetlock",
                FullName = "NuGet Lockfile Generator",
                Description = "Produces a lockfile for NuGet for repeatable restores",
            };

            app.VersionOption("--version", GetVersion());
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
                    ? OK
                    : Error;
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

            var lockFilePath = Path.Combine(dir, LockFileName);
            var assetsFile = LockFileUtilities.GetLockFile(assetsFilePath, NullLogger.Instance);

            var proj = new XElement("Project");
            var doc = new XDocument(proj);

            // details
            var props = new XElement("PropertyGroup");
            props.Add(new XElement("MSBuildAllProjects", "$(MSBuildAllProjects);$(MSBuildThisFileFullPath)"));
            props.Add(new XElement("NuGetLockFileVersion", GetVersion()));
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
                var spec = assetsFile.PackageSpec.GetTargetFramework(target.TargetFramework);
                var itemGroup = new XElement("ItemGroup", new XAttribute("Condition", $"'$(TargetFramework)' == '{target.TargetFramework.GetShortFolderName()}'"));
                proj.Add(itemGroup);

                foreach (var library in target.Libraries.OrderBy(l => l.Name).Where(l => !l.Type.Equals("project", StringComparison.Ordinal)))
                {
                    // TODO re-add PrivateAssets, ExcludeAssets, etc. where appropriate

                    var reference = new XElement("PackageReference", new XAttribute("Include", library.Name), new XAttribute("Version", library.Version.ToNormalizedString()));
                    var refSpec = spec.Dependencies.FirstOrDefault(l => l.Name.Equals(library.Name, StringComparison.OrdinalIgnoreCase));
                    if (refSpec != null)
                    {
                        if (refSpec.AutoReferenced)
                        {
                            reference.Add(new XAttribute("IsImplicitlyDefined", "true"));
                        }

                        if (refSpec.NoWarn.Any())
                        {
                            reference.Add(new XAttribute("NoWarn", refSpec.NoWarn.Aggregate(string.Empty, (a, b) => $"{a};{b}")));
                        }

                        if (refSpec.SuppressParent == LibraryIncludeFlags.All)
                        {
                            reference.Add(new XAttribute("PrivateAssets", "All"));
                        }

                        if (refSpec.IncludeType != LibraryIncludeFlags.All)
                        {
                            reference.Add(
                                new XAttribute("IncludeAssets", LibraryIncludeFlagUtils.GetFlagString(refSpec.IncludeType).Replace(", ", ";")));
                        }
                    }
                    else
                    {
                        bool IsEmptyFile(LockFileItem item)
                        {
                            return Path.GetFileName(item.Path).Equals("_._", StringComparison.Ordinal);
                        }

                        // Attempt to make this as similar to a transitive dependency as possible

                        // This info is just for us. No one uses it (yet).
                        reference.Add(new XAttribute("Transitive", "true"));

                        // Add PrivateAssets="All" to ensure only top-level dependencies end up in the generated nuspec
                        reference.Add(new XAttribute("PrivateAssets", "All"));

                        // in some cases, the parent package may exclude assets from their nuspec.
                        // We don't want to change the compile graph by lifting this to be a top-level PackageRef
                        var excludeFlags = LibraryIncludeFlags.None;
                        if (library.CompileTimeAssemblies.Count(IsEmptyFile) == 1)
                        {
                            excludeFlags |= LibraryIncludeFlags.Compile;
                        }

                        if (library.RuntimeAssemblies.Count(IsEmptyFile) == 1)
                        {
                            excludeFlags |= LibraryIncludeFlags.Runtime;
                        }

                        if (library.NativeLibraries.Count(IsEmptyFile) == 1)
                        {
                            excludeFlags |= LibraryIncludeFlags.Native;
                        }

                        if (library.Build.Count(IsEmptyFile) == 1
                            || library.BuildMultiTargeting.Count(IsEmptyFile) == 1)
                        {
                            excludeFlags |= LibraryIncludeFlags.Build;
                        }

                        reference.Add(new XAttribute("ExcludeAssets", LibraryIncludeFlagUtils.GetFlagString(excludeFlags)));
                    }

                    itemGroup.Add(reference);
                }

            }

#if NETCOREAPP1_0
            using (var stream = File.Open(lockFilePath, FileMode.Create))
            {
                doc.Save(stream);
            }
#elif NETCOREAPP2_0
            doc.Save(lockFilePath);
#else
#error Update target frameworks
#endif
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.Write($"Generated lock file: ");
            Console.ResetColor();
            Console.WriteLine(lockFilePath);
            Console.WriteLine("This file should be commited to source control.");
            return true;
        }

        private static string GetVersion()
        {
            return typeof(Program).GetTypeInfo()
                .Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                .InformationalVersion;
        }
    }
}
