---
title: Working on the docs and samples
description: How the docs site and samples are built, and what CI checks.
---

The site is [Astro Starlight](https://starlight.astro.build/) in `docs/`, published to GitHub
Pages by `.github/workflows/docs.yml` on every push to `main`.

```bash
cd docs
npm ci
npm run dev        # http://localhost:4321/Deployify/
```

Pages are Markdown (or MDX) under `docs/src/content/docs/`. The sidebar is set in
`docs/astro.config.mjs`; add a new page there too.

## Generated pages

The [CLI reference](/Deployify/reference/cli/) is generated from the command definitions, so don't
edit `reference/cli/*.md` by hand. After changing a command or option:

```bash
dotnet run --project tools/Deployify.DocGen -- docs/src/content/docs/reference/cli
```

CI regenerates the pages and fails when they differ from what's committed.

## Code from the samples

Code shown in a walkthrough is imported from the sample, not pasted, so it is always code that
builds:

```mdx
import { Code } from '@astrojs/starlight/components';
import appHost from '../../../../../samples/hello-aspire/HelloAspire.AppHost/AppHost.cs?raw';

<Code code={appHost} lang="csharp" title="AppHost.cs" />
```

## Samples

Each sample under `samples/` is a self-contained solution (its own `Directory.Build.props` and
`Directory.Packages.props`) with a README. `.github/workflows/samples.yml`:

- on every pull request, builds each sample and runs `deployify generate` against it;
- nightly (and on demand), publishes and deploys the Compose samples on the runner's Docker,
  checks that they answer, and destroys them.

A new sample needs a row in `samples/README.md` and on the [Samples](/Deployify/samples/) page,
and an entry in the workflow's matrix.
