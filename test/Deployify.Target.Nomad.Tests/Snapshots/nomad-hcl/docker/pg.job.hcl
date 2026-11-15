job "pg" {
  namespace = "apps"
  datacenters = ["dc1", "dc2"]
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
      auto_revert = false
    }

    network {
      mode = "host"
      port "tcp" { static = 5432 }
    }

    service {
      name = "pg"
      port = "tcp"
      provider = "consul"

      check {
        type = "tcp"
        interval = "15s"
        timeout = "1500ms"

        check_restart {
          limit = 3
          grace = "30s"
        }
      }
    }

    task "pg" {
      driver = "docker"

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
        cpu = 300
        memory = 512
      }
    }
  }
}
