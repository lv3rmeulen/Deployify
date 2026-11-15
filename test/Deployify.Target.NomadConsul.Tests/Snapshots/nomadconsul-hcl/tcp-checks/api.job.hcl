job "api" {
  namespace = "mesh"
  datacenters = ["dc1"]
  type = "service"

  meta = {
    "deployify-managed" = "true"
  }

  group "api" {
    count = 1

    update {
      min_healthy_time = "30s"
      healthy_deadline = "10m"
      progress_deadline = "15m"
      auto_revert = true
    }

    network {
      mode = "bridge"
      port "http" { to = 8080 }
      port "metrics" { to = 9090 }
    }

    service {
      name = "api"
      port = "8080"

      check {
        type = "tcp"
        port = "http"
        interval = "10s"
        timeout = "2s"

        check_restart {
          limit = 3
          grace = "30s"
        }
      }

      connect {
        sidecar_service {
          proxy {
            upstreams {
              destination_name = "pg"
              local_bind_port = 20000
            }
          }
        }
      }
    }

    task "api" {
      driver = "docker"

      config {
        image = "reg.example.com/app/api:v1.0.0"
        auth {
          username = "ci"
          password = "__DEPLOYIFY_REGISTRY_PASSWORD__"
          server_address = "https://reg.example.com"
        }
        ports = ["http", "metrics"]
      }

      env = {
        "NOMAD_UPSTREAM_ADDR_PG" = "localhost:20000"
        "PLAIN" = "hello \"quoted\" $${y}"
      }

      template {
        data        = "{{ with nomadVar \"nomad/jobs/api\" -}}\nConnectionStrings__db={{ index . \"ConnectionStrings__db\" }}\n{{- end }}\n"
        destination = "secrets/deployify.env"
        env         = true
        change_mode = "restart"
      }

      resources {
        cpu = 256
        memory = 256
      }
    }

    task "api-dapr" {
      driver = "docker"
      config {
        image = "daprio/daprd:latest"
        args = ["./daprd", "--app-id", "api", "--dapr-http-port", "3500", "--dapr-grpc-port", "50001", "--log-level", "info", "--app-port", "8080", "--resources-path", "./components"]
      }
      resources {
        cpu = 50
        memory = 64
      }
      lifecycle {
        hook = "prestart"
        sidecar = true
      }
    }
  }
}
