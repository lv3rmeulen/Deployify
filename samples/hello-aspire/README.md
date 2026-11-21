# hello-aspire

A small Aspire app that covers every kind of resource Deployify deploys, deployed to Docker
Compose on your own machine:

| Resource | Kind | What it is |
|---|---|---|
| `api` | Project | An ASP.NET Core API that counts visits in Redis and logs them in Postgres |
| `web` | Dockerfile | A static site served by nginx, built from `HelloAspire.Web/Dockerfile` |
| `cache` | Container | Redis |
| `postgres` | Container | Postgres, with the `visitsdb` database |
| `admin-key` | Secret parameter | The key `POST /reset` asks for. You supply it |

Walkthrough: <https://lv3rmeulen.github.io/Deployify/getting-started/first-deployment/>

## Run it

Needs the .NET 10 SDK, Docker (or Podman) and the `deployify` tool. Ports 5432, 6379, 8080, 8081
and 8085 must be free.

```bash
cd samples/hello-aspire/HelloAspire.AppHost

export Parameters__admin_key=change-me   # PowerShell: $env:Parameters__admin_key = "change-me"

deployify plan                   # builds the manifest, generates compose files, shows the change
deployify publish                # builds the api and web images (api:local, web:local) into your local Docker
deployify apply --auto-approve   # deploys cache and postgres before api, and waits for each to be healthy

curl http://localhost:8080/      # {"message":"Hello from Aspire, deployed by Deployify","visits":1,"logged":1}
curl http://localhost:8085/      # the static site
curl -X POST -H "X-Admin-Key: change-me" http://localhost:8080/reset   # 204; without the key, 401

deployify destroy --auto-approve --volumes
```

Set `Parameters__admin_key` in every shell you run `deployify` from, since `plan` and `apply` write the
value into the api's environment. Running the AppHost with `dotnet run` instead asks for it in the
Aspire dashboard, or reads `Parameters:admin-key` from user secrets.

`deployify-state.json` is committed here so the sample runs as is. In your own app, create it with
`deployify init` and keep it out of git.

## What it shows

- All four kinds of resource Deployify deploys: a project, a Dockerfile build, containers and a
  secret parameter
- References between them (`ConnectionStrings__cache`, `ConnectionStrings__visitsdb`), and the
  database child resource `visitsdb`, which deploys as part of the `postgres` service
- Secret values kept out of `compose.yml`: the admin key and the generated Redis and Postgres
  passwords go into each service's `.env` file under `deployify-output/`
- Generated parameters (the Redis and Postgres passwords), saved in `deployify-parameters.json` and
  reused on every deploy
- The Compose target's per-service files under `deployify-output/`

Aspire creates `visitsdb` itself when it runs the app locally, but a deployed Postgres container
doesn't. The AppHost sets `POSTGRES_DB` so the postgres image creates the database on first start.

CI builds this sample and runs `deployify generate` on every pull request. It also publishes the
`api` and `web` images to a throwaway registry and checks that the generated artifacts pin them by
digest. It then promotes them with `crane` to a second path on that registry and deploys that
copy with `--require-digest`, checking the digests didn't change. On every pull request it also
deploys the sample to a [kind](https://kind.sigs.k8s.io/) cluster with `deployify ci
--auto-approve`, once with the Kubernetes target and once with the Helm target, calls it and
destroys it. Nightly, it deploys the sample to Docker Compose (`.github/workflows/samples.yml`).
