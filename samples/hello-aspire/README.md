# hello-aspire

The smallest useful Aspire app, deployed with Deployify to Docker Compose on your own machine:
an ASP.NET Core API (`api`, a project resource) that counts visits in Redis (`cache`, a container
resource).

Walkthrough: <https://lv3rmeulen.github.io/Deployify/getting-started/first-deployment/>

## Run it

Needs the .NET 10 SDK, Docker (or Podman) and the `deployify` tool.

```bash
cd samples/hello-aspire/HelloAspire.AppHost

deployify plan                   # builds the manifest, generates compose files, shows the change
deployify publish                # builds the api image (api:local) into your local Docker
deployify apply --auto-approve   # deploys cache, then api, and waits for each to be healthy

curl http://localhost:8080/      # {"message":"Hello from Aspire, deployed by Deployify","visits":1}

deployify destroy --auto-approve --volumes
```

`deployify-state.json` is committed here so the sample runs as is. In your own app, create it with
`deployify init` and keep it out of git.

## What it shows

- Project and container resources, and a reference between them (`ConnectionStrings__cache`)
- A generated parameter (the Redis password), saved in `deployify-parameters.json` and reused
- The Compose target's per-service files under `deployify-output/`

CI builds this sample and runs `deployify generate` on every pull request, and deploys it to
Docker Compose nightly (`.github/workflows/samples.yml`).
