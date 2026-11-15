job "pg" {
  namespace = "mesh"
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
      mode = "bridge"
      port "tcp" { to = 5432 }
    }

    service {
      name = "pg"
      port = "5432"

      connect {
        sidecar_service {
        }
      }
    }

    task "pg" {
      driver = "podman"

      config {
        image = "postgres:17"
        ports = ["tcp"]
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
