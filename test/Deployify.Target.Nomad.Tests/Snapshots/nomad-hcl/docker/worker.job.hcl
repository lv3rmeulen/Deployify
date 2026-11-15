job "worker" {
  namespace = "apps"
  datacenters = ["dc1", "dc2"]
  type = "service"

  meta = {
    "deployify-managed" = "true"
  }

  group "worker" {
    count = 1

    update {
      min_healthy_time = "30s"
      healthy_deadline = "10m"
      progress_deadline = "15m"
      auto_revert = false
    }

    task "worker" {
      driver = "docker"

      config {
        image = "registry.example.com/myapp/worker:1"
        network_mode = "host"
        args = ["--mode", "fast", "--api", "http://localhost:8080"]
        auth {
          username = "ci"
          password = "__DEPLOYIFY_REGISTRY_PASSWORD__"
          server_address = "https://registry.example.com"
        }
      }

      template {
        data        = "{{ $svc0 := \"\" }}{{ range service \"api\" }}{{ $svc0 = .Address }}{{ end -}}\nAPI_URL=http://{{ $svc0 }}:8080\n"
        destination = "secrets/deployify.env"
        env         = true
        change_mode = "restart"
      }

      resources {
        cpu = 300
        memory = 512
      }
    }
  }
}
