---
title: "Docker Compose"
description: "Deploying an Aspire app to Docker Compose with Deployify."
---

Target ID: `Compose`

Generates a `docker-compose.yml` (and optionally a merged override file and `.env`). Applies via `docker compose up`.

```json
"targetConfig": {
  "compose": {
    "projectName": "myapp",
    "restartPolicy": "unless-stopped",
    "generateProfiles": false,
    "generateEnvFile": false,
    "generateMergedFile": true,
    "generateHealthChecks": false
  }
}
```

Init flags: `--compose-project`, `--compose-restart`

`generateHealthChecks` is off by default: the images `dotnet publish` produces contain neither
`curl` nor `wget`, so a generated health check would mark every service unhealthy. Turn it on
only when your images include a tool the check can use.
