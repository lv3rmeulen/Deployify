job "pg" {
  namespace = "default"
  datacenters = ["dc1"]
  type = "service"

  meta = {
    "deployify-managed" = "true"
  }

  group "pg" {
    count = 1

    update {
      min_healthy_time = "30s"
      healthy_deadline = "10m"
      progress_deadline = "15m"
      auto_revert = true
    }

    network {
      mode = "host"
      port "tcp" { static = 5432 }
    }

    task "pg" {
      driver = "podman"

      config {
        image = "postgres:17"
        network_mode = "host"
      }

      template {
        data        = "{{ with nomadVar \"nomad/jobs/pg\" -}}\nPOSTGRES_PASSWORD={{ index . \"POSTGRES_PASSWORD\" }}\n{{- end }}\n"
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
