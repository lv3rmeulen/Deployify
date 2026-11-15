# Contributing to Deployify

Thanks for helping. Bug reports, fixes, docs and new targets are all welcome.

For security problems, don't open an issue: see [SECURITY.md](SECURITY.md).

## Before you start

- **Bugs**: open an issue with the Deployify version, the target, what you ran, and what
  happened. Leave secrets out of anything you paste.
- **Features and new targets**: open an issue first, so we can agree on the shape before you
  spend time on it. A platform that doesn't fit in the repository can often be a
  [plugin](https://lv3rmeulen.github.io/Deployify/extending/plugins/) instead.
- **Small fixes** (typos, docs, an obvious bug): just send a pull request.

## Building and testing

You need the .NET 10 SDK (the version in [`global.json`](global.json)), and Node.js for the
docs site. Docker helps for the samples but isn't needed for the tests.

```bash
dotnet tool restore
dotnet build Deployify.slnx
dotnet test --solution Deployify.slnx
```

The build treats warnings as errors; keep it at zero. Package versions live in
[`Directory.Packages.props`](Directory.Packages.props) (Central Package Management), so add
`<PackageReference>` items without a version.

To try your build of the CLI:

```bash
dotnet pack src/Deployify/Deployify.csproj -c Release
dotnet tool update --global --add-source ./src/Deployify/nupkg deployify
```

The repository layout is described in
[Project structure](https://lv3rmeulen.github.io/Deployify/contributing/project-structure/), and
adding a built-in target in
[Adding a target](https://lv3rmeulen.github.io/Deployify/contributing/adding-a-target/).

## Pull requests

- Branch from `main` and open the pull request against `main`. CI builds, runs the tests and
  builds the samples.
- Add tests for what you change. Bug fixes need a test that fails without the fix.
- Keep commit messages short, lowercase and imperative, like the history: `add helm target`,
  `fix nomad usage of localhost`.
- Add a line to the `Unreleased` section of [CHANGELOG.md](CHANGELOG.md) for anything a user
  would notice, under `Added`, `Changed`, `Deprecated`, `Removed`, `Fixed` or `Security`.

## Compatibility

Deployify 1.x follows the
[stability policy](https://lv3rmeulen.github.io/Deployify/reference/stability/). If your change
touches any of these, say so in the pull request:

- a CLI command, option, alias, default or exit code;
- a key in `deployify-state.json` or a target's `TargetConfig`;
- a state file (`inventory.json`, `images.json`, `parameters.json`, `lock.json`, the journals):
  raise its constant in `StateFormat` and teach its reader to upgrade the previous version;
- the plugin protocol or `Deployify.Plugin.Contracts`: additions only, and optional;
- resource names or ownership markers on a platform;
- the public API of `Deployify.Hosting`, `Deployify.Plugin.Sdk` or `Deployify.Plugin.Testing`,
  or a `Deployify.Core` type plugins implement or receive (`IDeploymentTarget`,
  `EnvironmentConfig`, …): additions only, and new interface members need a default implementation.

Don't remove or rename any of these. Deprecate instead: keep the old behavior working, warn
on stderr (or mark it `[Obsolete]` in the .NET API) with what to use instead, and list it under `Deprecated` in the changelog. Removal
waits for the next major release.

## Docs

The docs site is in [`docs/`](docs/) (Astro Starlight); see
[Working on the docs and samples](https://lv3rmeulen.github.io/Deployify/contributing/docs/).

- Write in American English.
- Changing a command or option means regenerating the CLI reference:
  `dotnet run --project tools/Deployify.DocGen -- docs/src/content/docs/reference/cli`.
  Don't edit those pages by hand.
- New features need a docs page or a section in an existing one in the same pull request.

## License

By contributing, you agree that your contributions are licensed under the
[MIT License](LICENSE).
