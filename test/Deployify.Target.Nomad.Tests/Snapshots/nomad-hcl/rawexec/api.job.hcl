job "api" {
  namespace = "default"
  datacenters = ["dc1"]
  type = "service"

  meta = {
    "deployify-managed" = "true"
  }

  group "api" {
    count = 3

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

    service {
      name = "api"
      port = "http"
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

    task "api" {
      driver = "raw_exec"

      artifact {
        source = "https://artifacts.example.com/api.zip"
        destination = "local/"
        options {
          archive = "zip"
        }
      }

      config {
        command = "local/api"
      }

      env = {
        "PLAIN" = "hello \"quoted\" %%{x} $${y}"
        "Odd.Key-Name" = "x"
      }

      template {
        data        = "{{ $svc0 := \"\" }}{{ range nomadService 1 (env \"NOMAD_ALLOC_ID\") \"pg\" }}{{ $svc0 = .Address }}{{ end -}}\n{{ with nomadVar \"nomad/jobs/api\" -}}\nConnectionStrings__db={{ index . \"ConnectionStrings__db\" | replaceAll \"@@deployify-svc:pg@@\" $svc0 }}\n{{- end }}\n"
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
