---
title: "Signing in to AWS and Azure from CI"
description: "Use OIDC federation so pipelines deploy with short-lived credentials instead of stored access keys or client secrets."
---

Deployify has no cloud credentials of its own. It calls the `aws` and `az` CLIs, and pushes
images with the credentials `docker login` stored, so a pipeline is signed in as whatever those
tools are signed in as. Sign them in with **OIDC federation**: the CI system issues the job a
signed token, and the cloud exchanges it for credentials that expire within the hour. Then there is
no access key or client secret in your CI settings to leak, rotate or forget to revoke.

You set this up once per cloud account and repository:

1. Tell the cloud to trust your CI system's token issuer.
2. Create a role (AWS) or identity (Azure) and say which pipelines may use it. You match on the
   token's subject, such as "this repository's `prod` environment".
3. Give that role or identity only the permissions Deployify needs.
4. In the pipeline, request a token and exchange it before running Deployify.

Nothing changes in `deployify-state.json`, with one exception: leave `targetConfig.ecs.awsProfile`
(and `State.S3.Profile`) empty in CI. The CLI then uses the credentials the sign-in step set.

## AWS from GitHub Actions

### The OIDC provider and the role

Create the provider once per AWS account:

```bash
aws iam create-open-id-connect-provider \
  --url https://token.actions.githubusercontent.com \
  --client-id-list sts.amazonaws.com
```

Then create a role that only jobs in your repository's `prod` environment can assume. Save this as
`trust.json`, replacing the account ID, organization and repository:

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Principal": { "Federated": "arn:aws:iam::111122223333:oidc-provider/token.actions.githubusercontent.com" },
    "Action": "sts:AssumeRoleWithWebIdentity",
    "Condition": {
      "StringEquals": {
        "token.actions.githubusercontent.com:aud": "sts.amazonaws.com",
        "token.actions.githubusercontent.com:sub": "repo:my-org/my-app:environment:prod"
      }
    }
  }]
}
```

```bash
aws iam create-role --role-name deployify-prod --assume-role-policy-document file://trust.json
```

The subject depends on the job. A job with `environment: prod` gets
`repo:<org>/<repo>:environment:prod`. A job without an environment gets
`repo:<org>/<repo>:ref:refs/heads/main` for a push to main, or
`repo:<org>/<repo>:pull_request` for a pull request. Matching on an environment is the safest
choice, because GitHub environments can require a reviewer and limit which branches may deploy.
Don't use a wildcard such as `repo:my-org/*`: it lets every repository in the organization assume
the role.

### A permissions policy for the ECS target

This covers `ci` against the ECS target with either secret backend and images in ECR. Replace the
region, account, cluster (`shop`), repository and role names. Attach it with
`aws iam put-role-policy --role-name deployify-prod --policy-name deployify --policy-document file://policy.json`.

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "Ecs",
      "Effect": "Allow",
      "Action": [
        "ecs:DescribeClusters", "ecs:ListServices", "ecs:DescribeServices",
        "ecs:CreateService", "ecs:UpdateService", "ecs:DeleteService", "ecs:TagResource",
        "ecs:RegisterTaskDefinition", "ecs:ListTaskDefinitions", "ecs:DeregisterTaskDefinition"
      ],
      "Resource": "*"
    },
    {
      "Sid": "PassTaskRoles",
      "Effect": "Allow",
      "Action": "iam:PassRole",
      "Resource": [
        "arn:aws:iam::111122223333:role/ecsTaskExecutionRole",
        "arn:aws:iam::111122223333:role/shop-task"
      ],
      "Condition": { "StringEquals": { "iam:PassedToService": "ecs-tasks.amazonaws.com" } }
    },
    {
      "Sid": "SsmSecrets",
      "Effect": "Allow",
      "Action": ["ssm:GetParameter", "ssm:GetParametersByPath", "ssm:PutParameter", "ssm:DeleteParameters"],
      "Resource": [
        "arn:aws:ssm:eu-west-1:111122223333:parameter/deployify/shop",
        "arn:aws:ssm:eu-west-1:111122223333:parameter/deployify/shop/*"
      ]
    },
    {
      "Sid": "SecretsManager",
      "Effect": "Allow",
      "Action": [
        "secretsmanager:GetSecretValue", "secretsmanager:PutSecretValue", "secretsmanager:CreateSecret",
        "secretsmanager:TagResource", "secretsmanager:DeleteSecret", "secretsmanager:RestoreSecret"
      ],
      "Resource": "arn:aws:secretsmanager:eu-west-1:111122223333:secret:deployify/shop/*"
    },
    {
      "Sid": "EcrLogin",
      "Effect": "Allow",
      "Action": "ecr:GetAuthorizationToken",
      "Resource": "*"
    },
    {
      "Sid": "EcrPush",
      "Effect": "Allow",
      "Action": [
        "ecr:BatchCheckLayerAvailability", "ecr:BatchGetImage", "ecr:GetDownloadUrlForLayer",
        "ecr:InitiateLayerUpload", "ecr:UploadLayerPart", "ecr:CompleteLayerUpload", "ecr:PutImage"
      ],
      "Resource": "arn:aws:ecr:eu-west-1:111122223333:repository/shop/*"
    }
  ]
}
```

- Drop `SsmSecrets` or `SecretsManager`, whichever backend you don't use. Even with
  `SecretsManager`, `destroy` removes leftover SSM parameters under the service's path, so keep
  `ssm:GetParametersByPath` and `ssm:DeleteParameters` if you destroy services.
- If your secrets use your own KMS key, add `kms:Encrypt`, `kms:Decrypt` and
  `kms:GenerateDataKey` on that key.
- With [remote state in S3](/Deployify/concepts/state/#remote-state), add `s3:GetObject`,
  `s3:PutObject` and `s3:DeleteObject` on `arn:aws:s3:::<bucket>/<prefix>/*`, and
  `s3:ListBucket` on the bucket.
- For a pull-request job that only runs `plan`, create a second role trusted for
  `repo:<org>/<repo>:pull_request`, with the `Describe`/`List` actions and read access to the
  generated values (`ssm:GetParametersByPath` on `<secretsPrefix>/_parameters`, or
  `secretsmanager:GetSecretValue` on the `_parameters` secret). It can't deploy anything.

### The workflow

```yaml
permissions:
  contents: read
  id-token: write   # lets the job request an OIDC token

jobs:
  deploy:
    runs-on: ubuntu-latest
    environment: prod   # must match the role's subject
    steps:
      - uses: actions/checkout@v4
      - uses: aws-actions/configure-aws-credentials@v6
        with:
          role-to-assume: arn:aws:iam::111122223333:role/deployify-prod
          aws-region: eu-west-1
      - uses: aws-actions/amazon-ecr-login@v2
      - name: Deploy
        run: deployify ci --project-path src/MyApp.AppHost --env prod --auto-approve
```

`configure-aws-credentials` puts the role's temporary credentials in environment variables, which
the `aws` CLI reads in every later step. `amazon-ecr-login` runs `docker login` for your account's
registry. The credentials last an hour by default. If a deploy, including waiting for services to
become stable, can take longer, set `role-duration-seconds` and raise the role's maximum session
duration to match.

On EKS (the Kubernetes, Helm or Argo CD / Flux targets), add
`aws eks update-kubeconfig --name <cluster> --region <region>` after signing in, and map the role
to a Kubernetes identity with an EKS access entry.

## Azure from GitHub Actions

### The identity and its federated credential

Use a user-assigned managed identity. It needs no app registration and has no secret at all.

```bash
az identity create --name deployify-prod --resource-group deploy-identities
az identity federated-credential create \
  --name github-prod \
  --identity-name deployify-prod \
  --resource-group deploy-identities \
  --issuer https://token.actions.githubusercontent.com \
  --subject repo:my-org/my-app:environment:prod \
  --audiences api://AzureADTokenExchange
```

The subject follows the same rules as on AWS. Azure matches it exactly, with no wildcards, so add one
federated credential per environment or branch that deploys.

### Role assignments for the Container Apps target

Grant the identity's principal ID (`az identity show ... --query principalId`) these roles:

| Role | Scope | For |
|---|---|---|
| Contributor | the resource group of the Container Apps environment | creating and updating container apps |
| AcrPush | the container registry | publishing images |
| Key Vault Secrets Officer | the vault | the `KeyVault` secret backend |
| Managed Identity Operator | the apps' user-assigned identities, if they live in another resource group | attaching `identityResourceId` and `acrIdentityResourceId` to apps |
| Storage Blob Data Contributor | the state container | [remote state in Azure Blob](/Deployify/concepts/state/#remote-state) without a SAS token |

```bash
az role assignment create --assignee-object-id <principal-id> --assignee-principal-type ServicePrincipal \
  --role Contributor --scope /subscriptions/<sub>/resourceGroups/shop-prod
```

### The workflow

```yaml
permissions:
  contents: read
  id-token: write

jobs:
  deploy:
    runs-on: ubuntu-latest
    environment: prod
    steps:
      - uses: actions/checkout@v4
      - uses: azure/login@v3
        with:
          client-id: ${{ vars.AZURE_CLIENT_ID }}        # the identity's client ID
          tenant-id: ${{ vars.AZURE_TENANT_ID }}
          subscription-id: ${{ vars.AZURE_SUBSCRIPTION_ID }}
      - run: az acr login --name myregistry
      - name: Deploy
        run: deployify ci --project-path src/MyApp.AppHost --env prod --auto-approve
```

The three IDs aren't secrets: without a matching federated credential they can't be used, so
repository variables are fine. `az acr login` signs Docker in to the registry.

The `az` session can't be renewed without a fresh token from GitHub, so a job that runs longer than
about an hour after `azure/login` fails with `AADSTS700024`. Split long work into separate jobs,
or run `azure/login` again before the deploy step.

On AKS, add `az aks get-credentials --resource-group <rg> --name <cluster>`. With Azure RBAC on the
cluster, grant the identity "Azure Kubernetes Service RBAC Writer" on the namespaces it deploys to;
`kubelogin convert-kubeconfig -l azurecli` lets `kubectl` use the `az` sign-in.

## GitLab CI

GitLab issues a token to any job that asks for one with `id_tokens`. Its subject looks like
`project_path:my-group/my-app:ref_type:branch:ref:main`, and the issuer is `https://gitlab.com` (or
your own GitLab's URL).

**AWS.** Create the provider with `--url https://gitlab.com --client-id-list sts.amazonaws.com`,
and use `gitlab.com:aud` and `gitlab.com:sub` in the trust policy. The AWS CLI does the exchange
itself when these variables are set:

```yaml
deploy:
  image: my-registry/deployify-ci:latest   # with the AWS CLI, Docker and Deployify
  environment: production
  id_tokens:
    AWS_ID_TOKEN:
      aud: sts.amazonaws.com
  variables:
    AWS_ROLE_ARN: arn:aws:iam::111122223333:role/deployify-prod
    AWS_WEB_IDENTITY_TOKEN_FILE: $CI_BUILDS_DIR/.aws-token
    AWS_REGION: eu-west-1
  script:
    - echo "$AWS_ID_TOKEN" > "$AWS_WEB_IDENTITY_TOKEN_FILE"
    - aws ecr get-login-password | docker login --username AWS --password-stdin 111122223333.dkr.ecr.eu-west-1.amazonaws.com
    - deployify ci --env prod --auto-approve
  rules:
    - if: $CI_COMMIT_BRANCH == "main"
```

**Azure.** Create the federated credential with `--issuer https://gitlab.com`, the GitLab subject
and the default audience, then:

```yaml
deploy:
  id_tokens:
    AZURE_ID_TOKEN:
      aud: api://AzureADTokenExchange
  script:
    - az login --service-principal -u "$AZURE_CLIENT_ID" -t "$AZURE_TENANT_ID" --federated-token "$AZURE_ID_TOKEN"
    - az account set --subscription "$AZURE_SUBSCRIPTION_ID"
    - az acr login --name myregistry
    - deployify ci --env prod --auto-approve
```

## Azure Pipelines

Create an Azure Resource Manager service connection of type **Workload identity federation**.
Azure DevOps sets up the federated credential for you. Run Deployify inside an `AzureCLI@2` task, so
it runs while that task's `az` session is signed in:

```yaml
- task: AzureCLI@2
  inputs:
    azureSubscription: deployify-prod   # the service connection
    scriptType: bash
    scriptLocation: inlineScript
    inlineScript: |
      az acr login --name myregistry
      deployify ci --env prod --auto-approve
```

For AWS from Azure Pipelines, create an AWS service connection from the AWS Toolkit for Azure DevOps
(1.15 or later) with **Use OIDC** checked and the role to assume. Then run Deployify from the
toolkit's `AWSShellScript@1` task, which signs the AWS CLI in for the script.

## When the sign-in fails

| Error | Usually means |
|---|---|
| `Not authorized to perform sts:AssumeRoleWithWebIdentity` | The token's `sub` or `aud` doesn't match the trust policy. Check whether the job uses an `environment` (it changes the subject) and that the workflow has `id-token: write`. |
| `Credentials could not be loaded` (configure-aws-credentials) | `id-token: write` is missing, or the job runs for a pull request from a fork, which gets no token. |
| `The config profile (...) could not be found` | `awsProfile` or `State.S3.Profile` is set in `deployify-state.json`. Leave them empty in CI. |
| `AADSTS70021: No matching federated identity record found` | No federated credential has exactly this issuer, subject and audience. |
| `AADSTS700024: Client assertion is not within its valid time range` | The job ran over an hour after signing in. Sign in again. |
| `AuthorizationFailed` / `AccessDeniedException` during `apply` | The sign-in worked. The role or identity is missing a permission listed above. |
