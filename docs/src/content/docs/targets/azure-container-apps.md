---
title: "Azure Container Apps"
description: "Deploying an Aspire app to Azure Container Apps with Deployify."
---

Target ID: `AzureContainerApps`

Generates Container App spec JSON files and deploys via the Azure CLI.

```json
"targetConfig": {
  "aca": {
    "subscriptionId": "00000000-0000-0000-0000-000000000000",
    "resourceGroup": "my-rg",
    "location": "eastus",
    "environmentName": "my-env",
    "minReplicas": 0,
    "maxReplicas": 3,
    "defaultCpu": 0.25,
    "defaultMemory": "0.5Gi",
    "acrLoginServer": "myregistry.azurecr.io",
    "acrIdentityResourceId": "/subscriptions/.../userAssignedIdentities/my-acr-pull",
    "tenantId": "optional-tenant-id"
  }
}
```

- When `subscriptionId` is empty, the Azure CLI's current subscription (`az account show`) is used.
- Only endpoints marked external in the AppHost get external ingress; everything else gets
  internal ingress.
- With `acrLoginServer` set, apps pull from ACR using a managed identity: the user-assigned
  identity in `acrIdentityResourceId` when given, otherwise the app's system-assigned identity
  (which needs the `AcrPull` role).

Init flags: `--aca-subscription`, `--aca-resource-group`, `--aca-location`, `--aca-environment`, `--aca-acr`

Requires the [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/) on `PATH`.
