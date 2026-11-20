# Local packages

A build of [ideablade-validation](https://github.com/Breeze/ideablade-validation) that the server
uses until the library is published to NuGet:

- `IdeaBlade.Validation.0.1.0-preview.3.nupkg` - the `IdeaBlade.Validation` package, found
  through the `local` source in `nuget.config`.

The clients take the matching npm package, `@ideablade/validation`, from their own `packages`
folder.

**Updating it.**  Build it from the library with a new prerelease version - never the same version
twice: NuGet caches a package by id and version (`~/.nuget/packages`) and would keep using the old
one.

```
cd ideablade-validation/dotnet/src/IdeaBlade.Validation
dotnet pack -c Release -p:Version=0.1.0-preview.4 -o <this folder>
```

Then delete the old file here and change the version in `TempHire.Server/TempHire.Server.csproj`.
Update the clients' npm package to the same version at the same time.

**Once the library is published**, delete this folder and the `local` source in `nuget.config`,
and reference the published version instead.
