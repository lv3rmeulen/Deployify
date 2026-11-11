# aspire-deploy

Deployify as a step in Aspire's own deployment pipeline: `aspire deploy` runs `deployify-deploy`
after Aspire writes its manifest. Deploys to Docker Compose.

Guide: <https://lv3rmeulen.github.io/Deployify/guides/aspire-deploy/>

## Run it

Needs the .NET 10 SDK, the Aspire CLI and Docker.

```bash
cd samples/aspire-deploy/AspireDeploy.AppHost

aspire deploy --list-steps                                    # … publish-manifest → deployify-deploy → deploy
aspire deploy                                                 # asks before deploying
Deployify__AutoApprove=true aspire deploy --non-interactive   # how CI runs it

curl http://localhost:8090/
deployify destroy --auto-approve
```

The AppHost references `src/Deployify.Hosting` as a project so the sample always runs the code in
this repository. In your own AppHost, reference the `Deployify.Hosting` package instead (once it is on NuGet), at the
version that pins the same Aspire version as your `Aspire.AppHost.Sdk`.
