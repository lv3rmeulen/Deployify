---
title: "Nomad raw_exec and artifact stores"
description: "Deploying published binaries instead of images on Nomad."
---

## `package`

Zips the published build output for each project service and uploads it to the configured artifact store. Only applicable for Nomad `RawExec` deployments.

```bash
# Zip and upload using the configured artifact store
deployify package

# Preview what would be zipped without uploading
deployify package --dry-run

# Specify a custom publish output directory
deployify package --publish-dir ./out/publish

# Tag the uploaded artifacts (defaults to the environment's imageTag)
deployify package --image-tag v1.0.0
```

After running `package`, the artifact URLs are written back to `deployify-state.json` under `serviceOverrides[name].artifactUrl`. The next `generate`/`plan`/`apply` picks them up and emits an `artifact { source = "..." }` block in the HCL.

**Typical raw_exec workflow:**

```bash
dotnet publish src/MyApp.AppHost -c Release
deployify package --image-tag v1.0.0
deployify plan
deployify apply --auto-approve
```

## Artifact stores

When `deploymentMode` is `RawExec`, Deployify can zip your published binaries and upload them to an artifact store. Nomad then downloads and unzips the artifact before running the binary via `raw_exec`.

The generated HCL looks like:

```hcl
task "api" {
  driver = "raw_exec"

  artifact {
    source      = "https://artifacts.example.com/api-v1.0.0.zip"
    destination = "local/"
    options {
      archive = true
    }
  }

  config {
    command = "local/api"
  }
}
```

### Supported stores

#### HTTP

```json
"artifactStore": {
  "type": "Http",
  "httpBaseUrl": "https://artifacts.example.com",
  "httpToken": "optional-bearer-token"
}
```

Artifacts are PUT to `{httpBaseUrl}/{name}-{version}.zip`.

#### S3

```json
"artifactStore": {
  "type": "S3",
  "s3Bucket": "my-artifacts",
  "s3Region": "us-east-1",
  "s3Prefix": "deployify",
  "s3AwsProfile": "default"
}
```

Uses the AWS CLI. Credentials from the default chain (`~/.aws/credentials`, env vars, instance role). Nomad downloads via `s3::https://s3.{region}.amazonaws.com/{bucket}/{key}`.

#### Azure Blob

```json
"artifactStore": {
  "type": "AzureBlob",
  "azureStorageAccount": "myaccount",
  "azureContainer": "artifacts",
  "azureConnectionString": "optional-connection-string-or-sas"
}
```

Uses the Azure CLI. If `azureConnectionString` is omitted, uses `az login` / managed identity.

#### Nexus

```json
"artifactStore": {
  "type": "Nexus",
  "nexusUrl": "https://nexus.example.com",
  "nexusRepository": "raw-hosted",
  "nexusUsername": "deployer",
  "nexusPassword": "secret"
}
```

Uploads to `{nexusUrl}/repository/{nexusRepository}/{name}/{name}-{version}.zip` via HTTP PUT.

#### Artifactory

```json
"artifactStore": {
  "type": "Artifactory",
  "artifactoryUrl": "https://mycompany.jfrog.io/artifactory",
  "artifactoryRepository": "releases",
  "artifactoryToken": "my-api-key"
}
```

Uploads to `{artifactoryUrl}/{repository}/{name}/{name}-{version}.zip` via HTTP PUT.

#### GCS

```json
"artifactStore": {
  "type": "Gcs",
  "gcsBucket": "my-artifacts",
  "gcsPrefix": "deployify",
  "gcsCredentialsFile": "/path/to/service-account.json"
}
```

Uses the `gcloud storage cp` CLI. If `gcsCredentialsFile` is omitted, uses Application Default Credentials. Nomad downloads via `gcs::https://storage.googleapis.com/{bucket}/{key}`.
