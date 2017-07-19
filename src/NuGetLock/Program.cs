﻿using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Extensions.CommandLineUtils;
using NuGet.Common;
using NuGet.LibraryModel;
using NuGet.Packaging;
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

                        // There was no top-level package reference. Attempt to make this as similar to a transitive dependency as possible
                        reference.Add(new XAttribute("Transitive", "true"));

                        // IsImplicitlyDefined is Visual Studio is magic
                        // Add PrivateAssets="All" to ensure only top-level dependencies end up in the generated nuspec
                        reference.Add(new XAttribute("PrivateAssets", "All"));

                        var excludeFlags = LibraryIncludeFlags.None;
                        if (library.CompileTimeAssemblies.Count(IsEmptyFile) == 1)
                        {
                            // in some cases, the parent package may exclude compile assets from their nuspec.
                            // We don't want to change the compile graph by lifting this to be a top-level PackageRef
                            excludeFlags |= LibraryIncludeFlags.Compile;
                        }

                        // in some cases, the parent package may exclude assets from their nuspec.
                        // We don't want to change the compile graph by lifting this to be a top-level PackageRef
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

            // TODO lock sources

            doc.Save(lockFilePath);
            Console.WriteLine($"Generated lock file: {lockFilePath}");
            Console.WriteLine("This file should be commited to source control.");
            return true;
        }
    }
}
