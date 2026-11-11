# Deployify docs

Source of <https://lv3rmeulen.github.io/Deployify/>, built with [Astro Starlight](https://starlight.astro.build/).

```bash
npm ci
npm run dev      # http://localhost:4321/Deployify/
npm run build    # static site in dist/
```

- Pages: `src/content/docs/` (sidebar order in `astro.config.mjs`)
- `src/content/docs/reference/cli/` is generated — after changing a command, run
  `dotnet run --project tools/Deployify.DocGen -- docs/src/content/docs/reference/cli` from the repository root
- Walkthroughs import their code from `../samples` so it always builds

More: [Working on the docs and samples](https://lv3rmeulen.github.io/Deployify/contributing/docs/).
