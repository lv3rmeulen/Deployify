# Deployify plugin index

The public index of third-party target plugins that `deployify plugin search` and
`deployify plugin install` read. It is published at
<https://lv3rmeulen.github.io/Deployify/plugins/index.json>, next to its Sigstore signature
`index.json.sigstore.json`.

## How plugins are trusted

1. **The index is signed.** [`.github/workflows/plugin-index.yml`](../.github/workflows/plugin-index.yml)
   builds `index.json` from the files in [`plugins/`](plugins/) and signs it keyless, from main.
   Deployify only reads an index signed by that workflow, and refuses one that has expired
   (it is rebuilt and re-signed every week).
2. **The index names each plugin's publisher.** Every entry has a `signer`: the identity that
   signs that plugin's packages. Merging an entry here is what vouches for "plugin `example` is
   published by whoever controls this identity".
3. **Packages are signed by their publisher.** Each package is a `.zip` pinned by SHA-256 in the
   index, with a Sigstore bundle that must be signed by the entry's `signer`. Deployify checks
   both before it unpacks anything, and nothing of the plugin runs before that.
4. **The project locks what was installed.** `deployify plugin install` records the plugin version,
   the hash of its files and where it came from in `deployify-plugins.lock.json`.

## Adding a plugin

Open a pull request that adds `plugins/<target-id>.json`:

```json
{
  "name": "Example Cloud",
  "description": "Deploys an Aspire app to Example Cloud.",
  "publisher": "Acme",
  "homepage": "https://github.com/acme/deployify-target-example",
  "license": "MIT",
  "signer": {
    "identityRegexp": "^https://github\\.com/acme/deployify-target-example/\\.github/workflows/release\\.yml@refs/tags/v.*$",
    "issuer": "https://token.actions.githubusercontent.com"
  },
  "versions": [
    {
      "version": "1.0.0",
      "protocol": "1.0",
      "packages": {
        "any": { "url": "https://github.com/acme/deployify-target-example/releases/download/v1.0.0/deployify-target-example-1.0.0-any.zip" }
      }
    }
  ]
}
```

- The file name is the target ID, in lowercase. It can't be a built-in target's.
- `signer` is a keyless identity (`identity` or an anchored `identityRegexp`, and `issuer`) or a
  PEM `publicKey`. Pin it to your repository and release workflow, not your whole organization.
- `version` is exactly what the plugin reports in its handshake (with `Deployify.Plugin.Sdk`, its
  package version).
- `packages` is keyed by runtime ID (`linux-x64`, `win-x64`, `osx-arm64`, …) or `any` for a
  framework-dependent plugin. An `any` package must be published with `-p:UseAppHost=false`.
- `sha256` can be left out: the build fills it in from the download and pins it from then on.
  The bundle is looked for at the package URL + `.sigstore.json` unless `bundle` says otherwise.
- To withdraw a version, add `"yanked": "<why>"`. It is no longer installed by default, and
  installing it by version warns.

The pull request's checks download every package, check its signature and unpack it. See
[Publishing a plugin](https://lv3rmeulen.github.io/Deployify/extending/plugins/#publishing-a-plugin)
for building and signing packages in a release workflow.

## Building it locally

```bash
dotnet run --project tools/Deployify.PluginIndex -- plugin-index/plugins out/plugins
```

This needs cosign. The result is unsigned; for a private index, sign it with
`cosign sign-blob --bundle index.json.sigstore.json index.json` and point Deployify at it with
`--index` and `--index-identity` (or `--index-key`).
