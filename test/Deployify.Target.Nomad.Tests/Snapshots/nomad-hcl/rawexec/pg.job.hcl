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

    service {
      name = "pg"
      port = "tcp"
      provider = "nomad"

      check {
        type = "tcp"
        interval = "10s"
        timeout = "2s"

        check_restart {
          limit = 3
          grace = "30s"
        }
      }
    }

    task "pg" {
      driver = "raw_exec"

      config {
        command = "local/pg"
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
