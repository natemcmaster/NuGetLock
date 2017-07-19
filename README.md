NuGetLock
=========

A tool for producing a lockfile that makes NuGet restore more deterministic.

## Install

Edit your csproj to include this, and then execute `dotnet restore`:

```xml
  <ItemGroup>
    <DotNetCliToolReference Include="NuGetLock" Version="0.1.0" />
  </ItemGroup>
```

## Usage

### Lock

1. Restore your project normally.
2. Add this to **bottom** your csproj file.
    ```xml
    <Project>
        <!-- ...  -->

        <!-- Make sure this is imported after all PackageReferences in your project  -->
        <Import Project="packages.lock.props" Condition="Exists('packages.lock.props')" />
    </Project>
    ```
3. Generate the lock file
    ```
    dotnet nugetlock
    ```
4. Commit your lock file
    ```
    git add packages.lock.props
    ```


### Unlock

To reset the lock, delete `packages.lock.props` and `obj/project.assets.json`.
