---
title: "Parameters and secrets"
description: "How Aspire parameters are resolved and where secret values go on each target."
---

Aspire `parameter.v0` resources (`builder.AddParameter(...)`, generated database passwords, …) are
resolved for every target. Values are looked up in this order:

1. `parameters` in the environment's config — use `"env:NAME"` for anything secret
2. A `Parameters__<name>` environment variable (Aspire's convention; a `-` in the name may be written as `_`)
3. The manifest's `default.value`
4. A generated value for inputs with `default.generate`, saved in `deployify-parameters.json` next to
   the state file so every deploy reuses the same password

```json
"environments": {
  "prod": {
    "parameters": {
      "api-key": "env:API_KEY",
      "region": "westeurope"
    }
  }
}
```

`deployify-parameters.json` holds generated secrets in plain text — add it to `.gitignore`.

Env vars whose value contains a **secret** parameter never go into plain-text artifacts. Each target
stores them its own way:

| Target | Where secret values go |
|---|---|
| Compose | The service's `.env` file (`generateEnvFile`), or `${Parameters__name}` in `compose.yml` with the values in a `.env` next to it |
| Kubernetes | A `<service>-secrets` Secret, loaded with `envFrom.secretRef` |
| AzureContainerApps | `configuration.secrets`, referenced with `secretRef` |
| AwsEcs | SSM Parameter Store SecureStrings under `secretsPrefix` (default `/deployify/<cluster>`), written by `apply` and referenced from the task definition's `secrets`. The execution role needs `ssm:GetParameters` |
| Nomad / NomadConsul | Nomad Variables at `nomad/jobs/<job>`, written by `apply` and read by a `template` block (Nomad 1.4+) |

`explain` masks secret values. A reference that can't be resolved is left in place and reported.

To pass a literal `{name.property}` through without resolving it, double the braces: `{{name.property}}`.

## Secret backends: keeping values out of output and state

The table above is the default `Inline` backend: secret values end up in files in
`deployify-output/` (`secret.yaml`, `.env`) and generated values in `deployify-parameters.json`.
An external backend keeps every secret value out of both. Generated artifacts and
`deployify-state.json` then only hold *references*, and the value is joined to the reference by the
platform at run time.

```json
"prod": {
  "secrets": {
    "backend": "Vault",
    "vault": { "address": "https://vault.example.com:8200", "token": "env:VAULT_TOKEN" }
  }
}
```

| `backend` | Targets | Generated artifacts hold | Generated values (`default.generate`) live in |
|---|---|---|---|
| `Inline` (default) | all | the values (see table above) | `deployify-parameters.json` |
| `KubernetesSecret` | Kubernetes | `secretKeyRef`s; values are server-side applied by `apply` from memory | Secret `deployify-parameters` in `<prefix>-system` |
| `Vault` | Kubernetes | Vault Agent annotations and a template | KV v2 `<mount>/<pathPrefix>/<prefix>/_parameters` |
| `Ssm` | AwsEcs | SSM parameter names (as with `Inline`) | SecureStrings under `<secretsPrefix>/_parameters/` |
| `KeyVault` | AzureContainerApps, Kubernetes | ACA: `keyVaultUrl` references. Kubernetes: a `SecretProviderClass` and `secretKeyRef`s | Key Vault secrets `<prefix>-parameters-*`, tagged |

**Kubernetes Secrets.** A value that is exactly one secret becomes a `secretKeyRef`. A value that embeds one, such as
a connection string, uses Kubernetes dependent variables: the secret is loaded into
`Parameters__<name>` first, and the value refers to it as `$(Parameters__<name>)`. The kubelet
expands it when the container starts. `apply` writes the Secret before the Deployment and restarts the
pods when its data changed.

**Vault.** `apply` writes each service's secrets to `<mount>/data/<pathPrefix>/<prefix>/<service>`.
With `manageAuth` (default `true`) it also writes a read-only policy and a Kubernetes auth role
`<prefix>-<service>`, bound to the service's own ServiceAccount, so a pod can read only its own path.
The Vault Agent Injector renders a .NET configuration file into the pod at
`/vault/secrets/appsettings.secrets.json`. Load it in the app (the path is in `DEPLOYIFY_SECRETS_FILE`):

```csharp
builder.Configuration.AddJsonFile(
    Environment.GetEnvironmentVariable("DEPLOYIFY_SECRETS_FILE") ?? "/vault/secrets/appsettings.secrets.json",
    optional: true, reloadOnChange: true);
```

Container resources (postgres, redis, …) can't read that file. They fall back to a Kubernetes Secret
with `secretKeyRef`, as with `KubernetesSecret`. Each service folder also gets a `vault-policy.hcl`
showing the policy, useful when `manageAuth` is off and roles are managed elsewhere.

| `vault` setting | Default | |
|---|---|---|
| `address` | `VAULT_ADDR` | |
| `token` | `VAULT_TOKEN` | Token Deployify writes with. Use `"env:NAME"`; pods never see it |
| `namespace` | `VAULT_NAMESPACE` | Enterprise namespace |
| `mount` | `secret` | KV v2 mount |
| `pathPrefix` | `deployify` | |
| `authMount` | `kubernetes` | Kubernetes auth method mount |
| `manageAuth` | `true` | Write policies and roles |
| `secretsFileName` | `appsettings.secrets.json` | |
| `agentAnnotations` | | Extra `vault.hashicorp.com/*` annotations |

**SSM.** ECS already references secrets from SSM. With `Ssm`, `apply` also skips values that haven't
changed (instead of creating a new parameter version on every deploy) and generated values move from
`deployify-parameters.json` into SSM.

**Azure Key Vault.** Each secret env var of a container app becomes a Key Vault secret named
`<prefix>-<app>-<name>` (prefix defaults to `deployify-<environmentName>`, so environments can share
a vault). The app spec's `configuration.secrets` holds only a `keyVaultUrl` and the identity used to
read it. `apply` writes changed values before deploying, then restarts the latest revision when an
existing secret changed. Container Apps would otherwise only pick up a new value on its next periodic refresh.

```json
"secrets": {
  "backend": "KeyVault",
  "keyVault": {
    "vaultName": "shop-kv",
    "identityResourceId": "/subscriptions/…/resourceGroups/…/providers/Microsoft.ManagedIdentity/userAssignedIdentities/shop-apps"
  }
}
```

Give the identity the **Key Vault Secrets User** role on the vault (or a `get` access policy). Give
whoever runs `deployify apply` **Key Vault Secrets Officer**. Use a user-assigned identity: a
system-assigned one only exists once the app does, so it can't be granted access before the first
revision tries to read its secrets. Without `identityResourceId` the apps use `"system"`, and the first
deploy will fail until you grant that identity access. Set `vaultUri` for sovereign clouds
(e.g. `https://shop-kv.vault.azure.cn`) and `secretPrefix` to choose the name prefix.

**Azure Key Vault on Kubernetes (AKS).** This uses the Azure Key Vault provider for the Secrets
Store CSI driver: the AKS add-on `azure-keyvault-secrets-provider`, or the Helm chart elsewhere.
`apply` writes each secret parameter a service uses to `<prefix>-<service>-<parameter>` (prefix
defaults to `deployify-<namespacePrefix>`). Each service gets a `SecretProviderClass` that mounts
those secrets and syncs them into a `<service>-secrets` Secret. Env vars read that Secret with
`secretKeyRef`, and connection strings use `$(VAR)` as with `KubernetesSecret`, so containers such as
postgres work too. The synced Secret only exists while a pod mounts the volume, which every deployment
that needs it does.

```json
"secrets": {
  "backend": "KeyVault",
  "keyVault": {
    "vaultName": "shop-kv",
    "tenantId": "<tenant id>",
    "clientId": "<client id of the identity pods read secrets with>"
  }
}
```

| `keyVault` setting (Kubernetes) | Default | |
|---|---|---|
| `kubernetesIdentity` | `WorkloadIdentity` | `WorkloadIdentity`: each service gets a ServiceAccount annotated with `clientId`, and pods are labeled `azure.workload.identity/use`. `VmManagedIdentity`: the node pool's identity (e.g. the add-on's), no ServiceAccount |
| `cloudName` | public cloud | e.g. `AzureChinaCloud` |
| `subscriptionId` | `az` default | Subscription of the vault, for `apply` |

With workload identity, enable the OIDC issuer and workload identity on the cluster. Then create a
federated credential on the identity for each service's ServiceAccount (`system:serviceaccount:<prefix>-<service>:<service>`):

```bash
az identity federated-credential create --name shop-api --identity-name shop-apps --resource-group shop-rg \
  --issuer "$(az aks show -n shop-aks -g shop-rg --query oidcIssuerProfile.issuerUrl -o tsv)" \
  --subject system:serviceaccount:shop-api:api
```

Rotation works like the other backends: `apply` only writes changed values and restarts the
deployments whose secrets changed, which makes the driver fetch and sync the new values.

**Safety rails**, active with any external backend:

- Generated values are reused from the backend, never regenerated. New ones are written by `apply`
  *before* anything that uses them is deployed. If the backend can't be read, `plan` warns and `apply`
  refuses to run, so a password a database already uses is never replaced.
- Switching from `Inline` carries existing values over from `deployify-parameters.json` on the next
  `apply`. After that, the file can be deleted.
- After writing artifacts, Deployify scans them for every secret value it resolved. If one is found, the
  file is deleted and the command fails. The state file is checked the same way before it's saved.
- `apply` records where each secret went, never its value, under `secrets.references`
  (`backend`, `location`, `key`, backend `version`).
