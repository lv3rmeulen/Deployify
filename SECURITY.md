# Security policy

## Reporting a vulnerability

Please don't open a public issue, discussion or pull request for a security problem.

Report it privately through GitHub instead:

1. Go to the [Security tab](https://github.com/lv3rmeulen/Deployify/security) of the repository.
2. Click **Report a vulnerability**.
3. Describe the problem, the Deployify version (`deployify --version`), the target and state
   backend involved, and the steps to reproduce it. A minimal AppHost or `deployify-state.json`
   (with the secrets taken out) helps a lot.

Only the maintainers can see the report. You'll get an answer within a month. Once the problem is
confirmed, we agree on a fix and a disclosure date with you, publish a GitHub security advisory
with the fixed release, and credit you in it unless you'd rather not be named.

## Supported versions

| Version | Security fixes |
|---|---|
| Latest 1.x minor release | Yes |
| Older 1.x minor releases | No, upgrade to the latest 1.x |
| 1.0.0 alphas | No |

A security fix may break something the [stability policy](https://lv3rmeulen.github.io/Deployify/reference/stability/)
otherwise promises when there is no safe alternative. The advisory and the changelog then say what
changed and what to do.

## What counts

Deployify handles cloud credentials, registry passwords and generated secrets, and it deletes
things. Reports like these are in scope:

- A secret value showing up where it shouldn't: console output, logs, generated artifacts,
  `explain` output, rollout journals, or a plugin response.
- A secret written in plain text to a file Deployify creates, when the configuration used an
  `env:` reference or a secret backend.
- Deleting, pruning or overwriting resources Deployify doesn't own, or another environment's.
- Getting around the environment lock or the compare-and-swap checks on state writes.
- Getting a plugin started that doesn't match `deployify-plugins.lock.json`.
- Unauthenticated access to the resource server started by `deployify dashboard`.
- Generated artifacts that are less secure than the configuration asked for (for example a
  service exposed publicly when it shouldn't be).

These are not vulnerabilities in Deployify:

- A plugin can do anything you can. Plugins run as you, with your credentials, and nothing
  sandboxes them; see [Writing target plugins](https://lv3rmeulen.github.io/Deployify/extending/plugins/#security).
- Secrets you put in `deployify-state.json` as plain text yourself (Deployify warns about these),
  or remote state stored in a bucket, container or namespace that others can read.
- Vulnerabilities in the platforms and tools Deployify drives (Nomad, Kubernetes, Docker, the AWS
  and Azure CLIs, …). Report those upstream. A vulnerable NuGet dependency of Deployify is in
  scope, though.
