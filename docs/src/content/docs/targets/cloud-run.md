---
title: "Google Cloud Run"
description: "Deploying an Aspire app to Google Cloud Run with Deployify."
---

Target ID: `CloudRun`

Each Aspire service with an HTTP endpoint becomes a Cloud Run service. Deployify generates a
service spec per service (`<service>.service.json`, a Knative `serving.knative.dev/v1` Service)
and applies it with `gcloud run services replace`. Every deploy creates a revision, so a rollback
only has to send the traffic back to the revision that ran before.

```json
"targetConfig": {
  "cloudrun": {
    "projectId": "shop-prod",
    "projectNumber": "123456789012",
    "region": "europe-west1",
    "serviceAccount": "run@shop-prod.iam.gserviceaccount.com",
    "minInstances": 0,
    "maxInstances": 3,
    "cpu": "1",
    "memory": "512Mi",
    "concurrency": 80,
    "internalIngress": "internal",
    "allowUnauthenticated": true,
    "vpcConnector": "projects/shop-prod/locations/europe-west1/connectors/run",
    "vpcEgress": "all-traffic"
  }
},
"secrets": { "backend": "GcpSecretManager" }
```

| Setting | Default | Meaning |
|---|---|---|
| `projectId` | | The Google Cloud project. Required. |
| `projectNumber` | looked up | Part of every service's address; looked up with `gcloud projects describe` when not set |
| `region` | `us-central1` | |
| `serviceAccount` | the default compute service account | What the services run as |
| `minInstances`, `maxInstances` | `0`, `3` | A service's `count` override replaces `maxInstances` |
| `cpu`, `memory` | `1`, `512Mi` | Limits per instance |
| `concurrency` | Cloud Run's (80) | Requests per instance |
| `internalIngress` | `internal` | Ingress of services whose endpoint isn't external: `internal`, `internal-and-cloud-load-balancing` or `all` |
| `allowUnauthenticated` | `true` | Grant `allUsers` the Cloud Run Invoker role |
| `vpcConnector`, or `network` and `subnet` | | VPC egress: a Serverless VPC Access connector, or Direct VPC egress |
| `vpcEgress` | `private-ranges-only` | `all-traffic` sends everything through the VPC |

- **Addresses.** Services reach each other at their `https://<service>-<projectNumber>.<region>.run.app`
  address. That's what Aspire's endpoint references (`{api.bindings.http.url}`) resolve to.
- **Ingress.** An endpoint marked external in the AppHost gets ingress `all`. Everything else gets
  `internalIngress`. With `internal`, only traffic from the project's VPC networks gets in, so a
  service that calls an internal one needs VPC egress with `vpcEgress: all-traffic`.
- **Authentication.** Aspire apps call each other with plain HTTP requests, without Google identity
  tokens, so `allowUnauthenticated` is on by default, and ingress decides who can reach a service.
  An organization policy that forbids `allUsers` makes the grant fail with a warning; then set
  `allowUnauthenticated: false` and have callers send identity tokens.
- **Only HTTP services.** Cloud Run runs HTTP services. A container such as Redis or Postgres stops
  `plan` with an error: run it as a managed service (Memorystore, Cloud SQL), pass its address in
  as a parameter, and list it in `ignoreResources`.
- `PORT`, `K_SERVICE`, `K_REVISION` and `K_CONFIGURATION` are set by Cloud Run and left out of the
  spec.

## Secrets

Cloud Run reads secret values from Google Secret Manager, so a service with secret parameters
needs `"secrets": { "backend": "GcpSecretManager" }`. `apply` writes each secret env var to its
own secret, `<prefix>-<service>-<VAR>` (the prefix defaults to `deployify-<environment>`; set
`secrets.gcpSecretManager.secretPrefix` and `projectId` to change it or the project). A new
version is only added when the value changed. The spec in the output folder references
`latest`, and the deployed revision is pinned to the version just written. So each revision keeps
the secrets it was deployed with, and a rollback to an older revision brings back the older
values too. Generated values (`default.generate`) live in one secret,
`<prefix>-generated`. The services' service account needs the Secret Manager Secret Accessor
role. Destroy and prune delete a service's secrets unless `--keep-secrets` is given.

## Rollouts

A deploy is healthy when Cloud Run has picked up the change, the new revision is ready and it has
all the traffic. `gcloud run services replace --async` hands the change to Cloud Run, and the
[health gate](/Deployify/guides/rollouts/) watches it. Rolling back sends all traffic back to the
revision that served before (`gcloud run services update-traffic --to-revisions`). A service the
rollout created is deleted.

[Canary and blue-green](/Deployify/guides/rollout-strategies/) use exact traffic splits between
revisions. The candidate is tagged `candidate`, which makes it reachable at
`https://candidate---<service host>` before it gets any traffic.

## Plan, diff and logs

`plan` and `diff` compare a hash of each generated spec with the `deployify-spec` label on the
live service. Only services carrying Deployify's labels for the environment are listed for
removal. `deployify logs <service>` reads the services' stdout and stderr from Cloud Logging, and
`--follow` asks for new lines every five seconds. The tail is the service's most recent lines as a
whole, not per instance. Cloud Logging looks back one day unless `--since` is given.

Init flags: `--cloudrun-project`, `--cloudrun-region`, `--cloudrun-service-account`

Requires the [Google Cloud CLI](https://cloud.google.com/sdk/docs/install) (`gcloud`) on `PATH`,
signed in with access to the project (a user, a service account key, or workload identity
federation in CI). The images must be in a registry Cloud Run can pull from, such as Artifact
Registry (`containerRegistry: "europe-docker.pkg.dev/shop-prod/apps"`).
