job "worker" {
  namespace = "default"
  datacenters = ["dc1"]
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
      auto_revert = true
    }

    task "worker" {
      driver = "podman"

      config {
        image = "registry.example.com/myapp/worker:1"
        network_mode = "host"
        args = ["--mode", "fast", "--api", "http://localhost:8080"]
      }

      env = {
        "API_URL" = "http://localhost:8080"
      }

      resources {
        cpu = 256
        memory = 256
      }
    }
  }
}
