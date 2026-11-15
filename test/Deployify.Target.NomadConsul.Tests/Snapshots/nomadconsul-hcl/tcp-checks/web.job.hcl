job "web" {
  namespace = "mesh"
  datacenters = ["dc1"]
  type = "service"

  meta = {
    "deployify-managed" = "true"
  }

  group "web" {
    count = 1

    update {
      min_healthy_time = "30s"
      healthy_deadline = "10m"
      progress_deadline = "15m"
      auto_revert = true
    }

    network {
      mode = "bridge"
      port "http" { to = 3000 }
    }

    service {
      name = "web"
      port = "3000"

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
              destination_name = "api"
              local_bind_port = 20000
            }
            upstreams {
              destination_name = "pg"
              local_bind_port = 20001
            }
          }
        }
      }
    }

    task "web" {
      driver = "docker"

      config {
        image = "registry.example.com/myapp/web:1"
        args = ["--api", "http://localhost:20000"]
        auth {
          username = "ci"
          password = "__DEPLOYIFY_REGISTRY_PASSWORD__"
          server_address = "https://reg.example.com"
        }
        ports = ["http"]
      }

      env = {
        "NOMAD_UPSTREAM_ADDR_API" = "localhost:20000"
        "NOMAD_UPSTREAM_ADDR_PG" = "localhost:20001"
        "API_URL" = "http://localhost:20000"
      }

      resources {
        cpu = 256
        memory = 256
      }
    }
  }
}
