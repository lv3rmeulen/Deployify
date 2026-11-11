# Deployify.Plugin.Sample.Compose

A reference target plugin: Deployify's own Docker Compose target, served out of process through
`Deployify.Plugin.Sdk` under the ID `compose-plugin`. The plugin end-to-end tests start it as a
real child process.

Guide: <https://lv3rmeulen.github.io/Deployify/extending/plugins/>

```bash
cd path/to/YourApp.AppHost
dotnet publish /path/to/Deployify/samples/Deployify.Plugin.Sample.Compose -c Release -o .deployify/plugins
deployify plugin list
deployify plan --target compose-plugin
```
