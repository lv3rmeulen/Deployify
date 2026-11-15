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
      driver = "raw_exec"

      config {
        command = "local/worker"
        args = ["--mode", "fast", "--api", "http://localhost:8080"]
      }

      template {
        data        = "{{ $svc0 := \"\" }}{{ range nomadService 1 (env \"NOMAD_ALLOC_ID\") \"api\" }}{{ $svc0 = .Address }}{{ end -}}\nAPI_URL=http://{{ $svc0 }}:8080\n"
        destination = "secrets/deployify.env"
        env         = true
        change_mode = "restart"
      }

      resources {
        cpu = 256
        memory = 256
      }
    }
  }
}
