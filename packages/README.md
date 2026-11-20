# Local packages

A build of [ideablade-validation](https://github.com/Breeze/ideablade-validation) that the server
uses until the library is published to NuGet:

- `IdeaBlade.Validation.0.1.0-preview.4.nupkg` - the `IdeaBlade.Validation` package, found
  through the `local` source in `nuget.config`.

The clients take the matching npm package, `@ideablade/validation`, from their own `packages`
folder.

**Updating it.**  Build it from the library with a new prerelease version - never the same version
twice: NuGet caches a package by id and version (`~/.nuget/packages`) and would keep using the old
one.

```
cd ideablade-validation/dotnet/src/IdeaBlade.Validation
dotnet pack -c Release -p:Version=0.1.0-preview.5 -o <this folder>
```

Then delete the old file here and change the version in `TempHire.Server/TempHire.Server.csproj`.
Update the clients' npm package to the same version at the same time.

**The schema link.**  `$schema` in `TempHire.Server/Validation/validation.metadata.json` points at the
library's schema on GitHub, pinned to the commit the package was built from, so the editor checks
the rules the package runs.  Change the commit in that URL to the new build's.  (Not a relative path
into a client's `node_modules`: Visual Studio reports "Error loading schema" for it, and it leads
nowhere in a clone of this repository on its own.)

**Once the library is published**, delete this folder and the `local` source in `nuget.config`,
and reference the published version instead.
