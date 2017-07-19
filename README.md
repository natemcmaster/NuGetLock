NuGetLock
=========

[![AppVeyor][appveyor-badge]](https://ci.appveyor.com/project/natemcmaster/nugetlock)
[![NuGet][nuget-badge]](https://nuget.org/packages/NuGetLock)

[appveyor-badge]: https://img.shields.io/appveyor/ci/natemcmaster/nugetlock.svg?style=flat-square&label=appveyor
[nuget-badge]: https://img.shields.io/nuget/v/NuGetLock.svg?style=flat-square

A tool for producing a lockfile that makes NuGet restore more deterministic.

The lockfile produced integrates automatically with NuGet, Visual Studio, VS Code, etc.
It makes restores more deterministic by turning wildcards into exact version numbers,
and by flattening the package graph.

## Usage

### Install

Edit your csproj to include this, and then execute `dotnet restore`:

```xml
  <ItemGroup>
    <DotNetCliToolReference Include="NuGetLock" Version="0.1.0" />
  </ItemGroup>
```

Sorry, you can't install from the Visual Studio GUI yet because of https://github.com/NuGet/Home/issues/4190.

### Lock

1. Restore your project normally.
2. Add this to **bottom** your csproj file.
    ```xml
    <Project>
        <!-- ...  -->

        <!-- Make sure this is imported after all PackageReferences in your project  -->
        <Import Project="nuget.lock" Condition="Exists('nuget.lock')" />
    </Project>
    ```
3. Generate the lock file
    ```
    dotnet nugetlock
    ```
4. Commit your lock file
    ```
    git add nuget.lock
    ```


### Unlock

To reset the lock, delete `nuget.lock`.
