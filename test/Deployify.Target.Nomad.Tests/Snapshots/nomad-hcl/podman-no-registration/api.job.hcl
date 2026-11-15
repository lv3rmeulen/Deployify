job "api" {
  namespace = "default"
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
      mode = "host"
      port "http" { static = 8080 }
      port "https" { static = 8443 }
    }

    task "api" {
      driver = "podman"

      config {
        image = "registry.example.com/myapp/api:v1.0.0"
        network_mode = "host"
      }

      env = {
        "PLAIN" = "hello \"quoted\" %%{x} $${y}"
        "Odd.Key-Name" = "x"
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
      driver = "podman"
      config {
        image = "daprio/daprd:latest"
        network_mode = "host"
        args = ["./daprd", "--app-id", "api-app", "--dapr-http-port", "3500", "--dapr-grpc-port", "50001", "--log-level", "info", "--app-port", "8080", "--resources-path", "./components", "--config", "./dapr.yaml"]
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
