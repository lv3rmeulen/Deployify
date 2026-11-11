---
title: "Plugin targets"
description: "Using a target that ships as a plugin."
---

Any other platform can be added as an out-of-process plugin: an executable named
`deployify-target-<id>` that Deployify finds in the project's `.deployify/plugins` folder,
`~/.deployify/plugins` or on `PATH`, and talks to over JSON-RPC. Set `"Target": "<id>"` and
use it like a built-in target. See [docs/plugins.md](/Deployify/extending/plugins/) for using, locking,
writing (with `Deployify.Plugin.Sdk`) and testing plugins.
