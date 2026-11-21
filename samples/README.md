# Samples

| Sample | Shows | Target |
|---|---|---|
| [hello-aspire](hello-aspire/) | A first deployment covering every kind of resource: a project, a Dockerfile build, containers (Redis, Postgres) and a secret parameter | Docker Compose |
| [aspire-deploy](aspire-deploy/) | Running Deployify from `aspire deploy` with `Deployify.Hosting` | Docker Compose |
| [Deployify.Plugin.Sample.Compose](Deployify.Plugin.Sample.Compose/) | A deployment target written as an out-of-process plugin | — |

Every sample is built on each pull request and deployed nightly by
[`.github/workflows/samples.yml`](../.github/workflows/samples.yml), so what you see here runs.
